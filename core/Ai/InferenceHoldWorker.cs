// GPD Forge — driving the keep-awake hold from third-party inference activity. GPL-3.0-or-later.
//
// InferenceActivity.cs decides WHETHER a hold is justified. This file is the part that actually
// touches AntiStandbyService, and it is split out for one reason: the thing most likely to go wrong
// here is not the hysteresis, it is the bookkeeping around the hold — engaging twice and leaking a
// ref count that nothing will ever release, or releasing one we never took and stealing the manual
// toggle's hold out from under the user. That bookkeeping lives in InferenceHoldDriver, which is a
// plain class with no timer and no BackgroundService, so the tests can prove "exactly one hold, never
// leaked, never over-released" by stepping it deterministically instead of racing a real loop.
//
// DEFAULT OFF, and this is a considered position rather than timidity. The repo's other opt-in gates
// split two ways: GPDFORGE_ENABLE_HARDWARE guards writes that can cook the machine, and
// GPDFORGE_AUTO_PROFILES is default-ON-with-"0"-to-disable because being wrong only mislabels a mode.
// This feature is in neither camp. It performs no hardware write — SetThreadExecutionState is an
// unprivileged, self-cleaning power request, so it explicitly does NOT sit behind the hardware gate —
// but being wrong about it costs the user a flat battery, which is exactly the failure that was fixed
// on 2026-08-28 (14.4 W, 36 %/h, all night, because the machine never really slept). A CPU heuristic
// with numbers that have not yet been watched against this user's real workloads does not get to make
// that decision unsupervised on day one.
//
// So the default is not "off" in the sense of absent — it is OBSERVE-ONLY. The worker always runs,
// always samples, and always publishes what it WOULD have held for and since when. That way the
// evidence for turning enforcement on is collected by the feature itself, visible in the API and the
// panel, before it is ever allowed to keep the machine awake. GPDFORGE_INFERENCE_HOLD=1 promotes it
// from observing to enforcing; one flag, no rebuild, and the honest data to justify flipping it.
// An alternative considered and rejected: not registering the worker at all when disabled. It makes
// the gate simpler and the feature unobservable, which is the wrong trade for a heuristic nobody has
// measured yet.
//
// WHAT THE LOOP DOES WHEN SAMPLING FAILS, and why this is the most dangerous code in the feature.
// Every safety property of this design — the per-tick re-justification, the release streak, and the
// MaxHold ceiling — is evaluated INSIDE InferenceHoldEngine.Tick. A loop that catches a sampler
// failure and skips the tick therefore does not "pause" the feature: it freezes the engine with the
// hold still taken, and nothing in the system can ever take it back, because the only code that could
// is the code being skipped. That was the bug. A persistently failing sampler (a wedged WMI, a
// permissions change, a resume with a broken process table) pinned the machine awake indefinitely on
// hours-stale evidence, which is precisely the overnight drain this project exists to have fixed.
// So the loop now never skips a tick:
//   - a sampler that throws is converted into a tick with NO samples and EVERY watched name marked
//     unreadable. The engine then treats those processes as unmeasured — release streak advances,
//     the hold goes away, and the published reason says we could not see rather than "exited". This
//     is the same bias stated in InferenceActivity.cs's header: failing to measure must always push
//     toward letting the machine sleep;
//   - if the driver itself throws (a bug, not an environment failure), consecutive failures are
//     counted and the hold is handed back outright past a small threshold, on the principle that a
//     component we cannot reason about must not be the thing holding the machine awake. The engine's
//     view is left alone, so the hold is only re-taken when the engine next transitions into one —
//     which errs, again, toward sleep.
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GpdForge.Ai;

/// <summary>
/// Owns the one keep-awake hold this feature is allowed to take. Not a BackgroundService on purpose:
/// everything risky about hold ownership is testable here without a clock.
/// </summary>
public sealed class InferenceHoldDriver
{
    private readonly AntiStandbyService _anti;
    private readonly InferenceHoldState _state;
    private readonly InferenceHoldOptions _opt;
    private readonly InferenceHoldEngine _engine;
    private readonly ILogger? _log;

    /// <summary>
    /// The single source of truth for whether WE are holding. Every Start() is guarded by it being
    /// false and every Stop() by it being true, so no code path can double-acquire (a leaked hold
    /// nothing releases) or double-release (stealing the manual toggle's hold).
    /// </summary>
    private bool _held;

    public InferenceHoldDriver(
        AntiStandbyService anti, InferenceHoldState state, InferenceHoldOptions options, ILogger? logger = null)
    {
        _anti = anti;
        _state = state;
        _opt = options;
        _engine = new InferenceHoldEngine(options);
        _log = logger;
    }

    /// <summary>True while this driver is holding the keep-awake lock.</summary>
    public bool Held => _held;

    /// <summary>
    /// One tick: decide, act on the edge, publish. Publishing happens on every tick, not only on a
    /// transition, because the panel's "what is keeping this awake right now" readout has to stay true
    /// while the state is steady — which is nearly all of the time.
    /// </summary>
    public InferenceHoldDecision Tick(DateTimeOffset now, IReadOnlyList<ProcessCpuSample> samples)
        => Tick(now, new ProcessSampleResult(samples, []));

    /// <summary>
    /// The same tick, told which watched names could not be enumerated. Prefer this overload wherever
    /// the sampler can report a failure: the other one asserts "these are all the watched processes
    /// there are", and asserting that after a failed enumeration is how a live run gets called exited.
    /// </summary>
    public InferenceHoldDecision Tick(DateTimeOffset now, ProcessSampleResult sample)
    {
        var d = _engine.Tick(now, sample.Samples, sample.UnreadableNames);

        // Observe-only mode runs the entire engine and publishes everything; it just never reaches
        // for the hold. That is deliberate: the numbers the user needs in order to trust enforcement
        // are the same numbers enforcement would act on.
        if (_opt.Enforce)
        {
            if (d.Action == InferenceHoldAction.Engage && !_held)
            {
                _anti.Start();
                _held = true;
                _log?.LogInformation("Inference keep-awake ENGAGED: {Reason}.", d.Reason);
            }
            else if (d.Action == InferenceHoldAction.Release && _held)
            {
                _anti.Stop();
                _held = false;
                _log?.LogInformation("Inference keep-awake RELEASED: {Reason}.", d.Reason);
            }
        }

        _state.Current = new InferenceHoldStatus(
            Enforcing: _opt.Enforce,
            Holding: _opt.Enforce ? _held : d.Holding,
            HoldingSince: (_opt.Enforce ? _held : d.Holding) ? _engine.HoldingSince : null,
            Workers: d.Workers,
            LastTickAt: now,
            LastReason: d.Reason,
            WatchedNames: _opt.WatchedNames,
            BusyCpuFraction: _opt.BusyCpuFraction,
            Unmeasured: d.Unmeasured);

        return d;
    }

    /// <summary>
    /// Give the hold back on service shutdown. Idempotent, so a StopAsync racing a final tick cannot
    /// release twice. SetThreadExecutionState would be undone by process exit anyway, but relying on
    /// that would leave AntiStandbyService's ref count wrong for anyone still reading it.
    /// </summary>
    public void Shutdown()
    {
        if (!_held) return;
        _anti.Stop();
        _held = false;
        _log?.LogInformation("Inference keep-awake RELEASED: service shutting down.");
    }
}

/// <summary>
/// The timer. Deliberately thin — it samples, hands the samples to the driver, and guarantees the
/// hold is given back however the loop ends. The one thing it is NOT allowed to do is skip a tick;
/// see the failure note in this file's header.
/// </summary>
public sealed class InferenceHoldWorker(
    IProcessCpuSampler sampler,
    AntiStandbyService anti,
    InferenceHoldState state,
    InferenceHoldOptions options,
    ILogger<InferenceHoldWorker> logger) : BackgroundService
{
    /// <summary>
    /// How many consecutive driver failures are tolerated before the hold is surrendered outright.
    /// Small on purpose: one or two is a blip worth riding out, a sustained run means the component
    /// deciding to keep the machine awake is broken, and a broken component does not get to win.
    /// </summary>
    private const int MaxConsecutiveDriverFailures = 3;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var driver = new InferenceHoldDriver(anti, state, options, logger);
        logger.LogInformation(
            "Inference keep-awake {Mode} (watching {Count} process names, busy at >= {Pct:P0} of total CPU).",
            options.Enforce ? "ENFORCING" : "observing only (set GPDFORGE_INFERENCE_HOLD=1 to enforce)",
            options.WatchedNames.Count, options.BusyCpuFraction);

        int failures = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ProcessSampleResult sample;
                try
                {
                    sample = sampler.SampleDetailed(options.WatchedNames);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The whole pass failed, so we know nothing about any watched name. Tick anyway,
                    // declaring exactly that: no samples, everything unreadable. The engine advances
                    // its release streak and lets the machine sleep instead of freezing mid-hold.
                    logger.LogDebug(ex, "inference activity sampling failed; ticking as fully unmeasured");
                    sample = new ProcessSampleResult([], options.WatchedNames);
                }

                try
                {
                    driver.Tick(DateTimeOffset.UtcNow, sample);
                    failures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures++;
                    logger.LogWarning(ex, "inference activity tick failed ({Count} in a row)", failures);
                    if (failures >= MaxConsecutiveDriverFailures && driver.Held)
                    {
                        // We can no longer justify the hold each tick, so we stop claiming it.
                        logger.LogWarning(
                            "Inference keep-awake surrendered after {Count} consecutive tick failures.", failures);
                        driver.Shutdown();
                    }
                }

                await Task.Delay(options.EffectiveTickInterval, ct);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        finally { driver.Shutdown(); }
    }
}
