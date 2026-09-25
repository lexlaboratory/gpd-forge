// GPD Forge — the fan's own loop. GPL-3.0-or-later.
//
// Why this is not part of ForgeWorker any more (2026-09-24): the fan used to be the last block of
// ForgeWorker's tick, after the guardian's and the charge guard's TDP applies. Each apply runs
// ryzenadj twice, and under a sustained throttle that stretched one tick to ~1.7 s (measured the
// same day) — so the fan answered a heat spike late by however long a TDP write happened to take,
// which is exactly when it matters most.
//
// Now it runs on its own fixed 1 s timer and reads the temperature from the sampler's cache
// (TelemetrySampler), which costs a field load. Nothing it waits on belongs to ForgeWorker. The
// decision itself lives in FanTickPolicy; this class only schedules it and talks to the EC.
using GpdForge.Api;
using GpdForge.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GpdForge.Fan;

public sealed class FanWorker(
    ITelemetrySource telemetry,
    FanState fanState,
    IGpdFanController fanControl,
    ILogger<FanWorker> logger,
    TimeProvider? time = null,
    Func<bool>? sustained = null) : BackgroundService
{
    /// <summary>
    /// Whether <paramref name="powerMode"/> is a catalogue mode flagged <c>Sustained</c> (today only
    /// <c>ai</c>). Read from the catalogue rather than compared to "ai" so a future sustained mode
    /// gets the sustained fan by construction, like it already gets ProfileShaper's flat power.
    /// </summary>
    public static bool IsSustainedMode(string? powerMode) =>
        GpdForge.Profiles.ModeCatalogue.Find(powerMode)?.Sustained == true;

    private readonly Func<bool> _sustained = sustained ?? (() => false);

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly FanTickPolicy _policy = new();
    private readonly long _startTimestamp = (time ?? TimeProvider.System).GetTimestamp();

    // The sequence of the last sample this worker consumed. A tick that finds the same sequence has
    // no new reading: the sampler has not published since (a skipped tick, or a stall). It is handed
    // to the policy as "no reading", so a stalled sampler gets the same short grace as a missed
    // sensor read and then firmware takes over — a cached temperature must never drive the curve
    // forever just because nothing replaced it.
    private long _lastSequence;
    private bool _tickFailing;   // touched only by Tick, which only the loop (or a test) calls

    /// <summary>
    /// One fan tick: the latest cached sample and the current <see cref="FanState"/> in, at most one
    /// EC command out. Never throws — an unexpected error hands the fan to firmware and the next
    /// tick starts from scratch. Public so tests can step it without the timer.
    /// </summary>
    public void Tick()
    {
        try
        {
            var reading = telemetry.Latest;
            double? freshTempC = null;
            if (reading.Sequence != _lastSequence)
            {
                _lastSequence = reading.Sequence;
                freshTempC = reading.Snapshot.CpuTempC;
            }

            // Read once: /fan and /panic write these from request threads, and the policy must see
            // one consistent pair for the whole tick.
            string mode = fanState.Mode;
            int manualDuty = fanState.ManualDuty;
            double nowS = _time.GetElapsedTime(_startTimestamp).TotalSeconds;

            var command = _policy.Next(mode, manualDuty, freshTempC, nowS, _sustained());
            switch (command.Kind)
            {
                case FanCommandKind.Auto:
                    fanControl.SetAuto();
                    break;
                case FanCommandKind.Duty:
                    _ = fanControl.SetManualDuty(command.Duty);   // failures are already logged inside GpdFanController
                    break;
            }

            if (_tickFailing) logger.LogInformation("Fan control recovered.");
            _tickFailing = false;
        }
        catch (Exception ex)
        {
            // Unreachable today (SetAuto never throws, SetManualDuty logs its own failures), which is
            // why it matters that it is safe if it ever is reached: firmware gets the fan, the curve
            // forgets its state, and the loop lives on. When this was an unhandled throw inside
            // ForgeWorker it would have stopped the host. Warned once per outage, not once a second.
            if (!_tickFailing) logger.LogWarning(ex, "Fan tick failed; fan handed back to firmware until it recovers.");
            _tickFailing = true;
            _policy.Reset();
            try { fanControl.SetAuto(); } catch { /* best effort */ }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Host startup must not wait on anything below.
        await Task.Yield();
        try
        {
            // The first tick waits (bounded) for the first real sample. Before it, the cache holds an
            // all-null placeholder: acting on that would hand the fan to firmware and then pay a MAX
            // safety write a second later when the first reading arrived.
            var first = await telemetry.ReadAsync(stoppingToken);
            if (!first.IsSampled) logger.LogWarning("No telemetry sample yet; the fan stays on firmware until one arrives.");

            // PeriodicTimer, not Delay(1 s) after each tick: the cadence is fixed, and a tick that
            // overruns is skipped rather than queued, so there is never a burst of catch-up writes.
            // The policy measures real elapsed time anyway, so a late tick is still a correct one.
            using var timer = new PeriodicTimer(Interval, _time);
            do { Tick(); }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* shutting down */ }
        finally
        {
            // Critical safety: always restore AUTOMATIC fan control on shutdown, even if we were
            // never in manual this run (SetAuto is idempotent / a no-op controller ignores it).
            try { fanControl.SetAuto(); } catch { /* best effort */ }
            logger.LogInformation("Fan control stopped; fan handed back to firmware.");
        }
    }
}
