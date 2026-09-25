// GPD Forge — background worker.
// Copyright (C) 2026 lexlaboratory. GPL-3.0-or-later. See ../LICENSE.

using GpdForge.Api;
using GpdForge.Fan;
using GpdForge.Guardian;
using GpdForge.History;
using GpdForge.Profiles;
using GpdForge.SystemControl;
using GpdForge.Tdp;
using GpdForge.Telemetry;
using GpdForge.Tuner;
using GpdForge.Alerts;
using GpdForge.Battery;
using GpdForge.Sessions;

namespace GpdForge;

/// <summary>
/// Orchestrates the hardware subsystems: consumes the sampler's telemetry (it never reads hardware
/// sensors itself — see TelemetrySampler), and (in gaming mode, once FPS telemetry is
/// available) steers TDP toward a target FPS via the tested PID — or, while an auto-tuner sweep is
/// running, steps TDP through the sweep instead (the two never run the same tick; see below). Applies
/// the active mode once at start, and every 30 s reads the limits back and re-applies the last write
/// only if it moved (TdpReasserter). Thaws
/// any frozen processes on stop. The fan is not driven from here: it has its own 1 s loop
/// (core/Fan/FanWorker.cs), so a slow ryzenadj apply in this tick can never delay it.
/// </summary>
public sealed class ForgeWorker(
    ILogger<ForgeWorker> logger,
    ITdpController tdp,
    IFanController fan,
    ITelemetrySource telemetry,
    ModeState mode,
    AutoFpsState autoFps,
    FpsTdpController fpsController,
    FreezerService freezer,
    GuardianService guardian,
    TelemetryHistory history,
    ProfileApplier profileApplier,
    PowerSourceState powerSource,
    TunerState tuner,
    AlertService alerts,
    ChargeGuardService chargeGuard,
    SessionRecorder sessions,
    TdpIntent intent,
    TdpState tdpState,
    TdpReasserter reasserter) : BackgroundService
{
    // Last observed AC state, so the per-power-source switch (below) fires only ON THE FLIP rather
    // than re-applying every tick. Null until the first snapshot arrives.
    private bool? _lastAcConnected;

    // Monotonic clock for the throttle re-assert interval below.
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    // How long one wait for the next sample may last before the loop re-checks cancellation. Not a
    // tick rate — the sampler sets that — just a bound so a stalled sampler cannot park the loop
    // in a single await forever.
    private static readonly TimeSpan SampleWait = TimeSpan.FromSeconds(2);

    // The guardian's last applied ceiling, so a steady throttle is not re-applied every tick.
    private const double ThrottleReassertSeconds = 30.0;
    private TdpProfile? _lastThrottleApplied;
    private double _lastThrottleAppliedAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GPD Forge service starting.");
        await fan.InitializeAsync(stoppingToken);

        try
        {
            await ApplyStartupTdpAsync(stoppingToken);

            long lastSequence = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                // Paced by the sampler: one tick per NEW sample, instead of a hardware read of its
                // own followed by Delay(1 s). A tick that overran (a slow ryzenadj) simply picks up the
                // newest sample next; a sampler that stalls leaves the tick waiting, exactly as the
                // old blocking read did, rather than re-processing a stale snapshot as if it were new
                // (which would put duplicate rows in the history and feed the guardian old data).
                var reading = await telemetry.WaitForNewerAsync(lastSequence, SampleWait, stoppingToken);
                if (reading.Sequence == lastSequence) continue;
                lastSequence = reading.Sequence;
                var snapshot = reading.Snapshot;
                var sampledAt = reading.SampledAt ?? DateTimeOffset.UtcNow;

                // Stamped with when the hardware was READ, not when this tick got round to it.
                history.Add(new HistorySample(sampledAt.ToUnixTimeMilliseconds(), snapshot));
                sessions.Observe(snapshot, sampledAt);

                // Per-power-source auto mode-switch — only on the AC/battery edge, mirroring how
                // POST /mode applies: flip ModeState.Active, then apply it through the same
                // ProfileApplier (yields if another power controller owns TDP).
                if (_lastAcConnected is bool prevAc && prevAc != snapshot.AcConnected)
                {
                    string? desired = PowerSourceProfiles.Resolve(snapshot.AcConnected, powerSource.Config, mode.Active);
                    if (desired is not null)
                    {
                        mode.Active = desired;
                        await profileApplier.ApplyAsync(mode.Active, stoppingToken);
                    }
                }
                _lastAcConnected = snapshot.AcConnected;

                // Thermal/battery guardian — evaluated every tick. A safety throttle takes priority
                // over auto-FPS; alerts are logged and surfaced via GET /guardian.
                var g = guardian.Observe(snapshot);
                if (g.Alert is not null)
                {
                    logger.LogWarning("Guardian [{Severity}]: {Alert}", g.Severity, g.Alert);
                    var severity = g.Severity switch { "critical" => AlertSeverity.Critica, "warn" => AlertSeverity.Aviso, _ => AlertSeverity.Info };

                    // Key on the PHENOMENON, not on the message. The key used to be
                    // $"guardian:{g.Severity}:{g.Alert}" and the message embeds the reading, so one
                    // degree of change opened a new alert: measured on this device, 77 rows with 67 of
                    // them Count == 1, one per degree of CPU temperature and one per percent of
                    // battery. AlertStore's coalescing was never broken — it was handed a key that
                    // could not repeat. Same rule already written one screen below for the charge
                    // guard: one key for the whole feature, not one per reading.
                    //
                    // Kind falls back to the old shape only if a decision forgot to set one, so a
                    // missing Kind degrades to yesterday's noisy behaviour rather than silently
                    // merging two unrelated phenomena under one key.
                    var dedupeKey = g.Kind ?? $"guardian:{g.Severity}:{g.Alert}";

                    // Category and title follow the phenomenon too: a low battery filed under
                    // "Thermal guardian" is a small lie that shows up on every row of the alert list,
                    // and all 77 rows on this device carried that title.
                    var isBattery = g.Kind is GuardianKind.BatteryLow or GuardianKind.BatteryCritical;
                    var category = isBattery ? AlertCategory.System : AlertCategory.Thermal;
                    var title = isBattery ? "Battery guardian" : "Thermal guardian";

                    alerts.Publish(category, severity, title, g.Alert,
                        $"cpuTempC={snapshot.CpuTempC:F1}; batteryPct={snapshot.BatteryPct}", dedupeKey);
                }

                if (g.ThrottleToW is int throttleW)
                {
                    // Hard cool-down: a ceiling that never raises any limit of what the user has in
                    // force (see GuardianThrottle.cs — it used to lift `windows` from 15 W to 25 W).
                    // Under the intent, not the preset: built on the preset, a 10 W manual override
                    // met the 12 W throttle floor and went UP. Skips auto-FPS this tick.
                    var throttle = GuardianThrottle.Profile(throttleW, intent.Resolve(mode.Active),
                        (int)guardian.Config.TempCriticalC);
                    // An unchanged ceiling is re-asserted every ThrottleReassertSeconds, not every
                    // tick: each apply runs ryzenadj twice, and under a sustained throttle that
                    // stretched the loop to ~1.7 s per tick (measured 2026-09-24).
                    double nowS = _clock.Elapsed.TotalSeconds;
                    if (throttle != _lastThrottleApplied || nowS - _lastThrottleAppliedAt >= ThrottleReassertSeconds)
                    {
                        await tdp.ApplyAsync(throttle, TdpOwner.ThermalGuardian, stoppingToken);
                        _lastThrottleApplied = throttle;
                        _lastThrottleAppliedAt = nowS;
                    }
                }
                else
                {
                    _lastThrottleApplied = null;   // a new episode applies its first ceiling at once

                    // Charge guard — evaluated only when the THERMAL guardian is not throttling.
                    // Safety outranks battery longevity: the thermal ceiling is always the lower and
                    // more urgent of the two, and letting a charge-health ceiling contend with it
                    // would mean two features writing STAPM in the same tick.
                    var cg = chargeGuard.Observe(snapshot, DateTimeOffset.UtcNow);
                    if (cg.Alert is not null)
                    {
                        // One dedupe key for the whole feature, not one per hour count: the alert
                        // already fires once per episode, and keying on the text would open a new
                        // alert every time the number changed.
                        alerts.Publish(AlertCategory.System, AlertSeverity.Info,
                            "Battery charge guard", cg.Alert,
                            $"batteryPct={snapshot.BatteryPct}; hours={cg.EpisodeHours:F1}",
                            "charge-guard:high-soc");
                    }

                    var coolTo = ChargeGuardPolicy.EffectiveCeiling(
                        cg.CoolToW, intent.Resolve(mode.Active)?.StapmW ?? int.MaxValue);

                    // Whether anything below wrote TDP this tick; the 30 s readback waits for a tick
                    // that did not (it would only be reading back a write the closed loop just verified).
                    bool wrote = false;

                    if (coolTo is int coolW)
                    {
                        // A ceiling, held flat. EffectiveCeiling has already refused to raise power,
                        // so reaching here means this is genuinely lower than the mode would run.
                        await tdp.ApplyAsync(new TdpProfile(coolW, coolW, coolW, 90), TdpOwner.ChargeGuard, stoppingToken);
                        wrote = true;
                    }
                    else if (g.ClearThrottle || cg.ClearCool)
                    {
                        // Back to what the user had: a manual override if one is set in this mode,
                        // else the preset. Restoring the preset unconditionally is how a hand-set
                        // 20 W came out of a hot spell as 15/20/17 W with nothing saying why.
                        var restore = intent.Resolve(mode.Active);
                        if (restore is not null)
                        {
                            await tdp.ApplyAsync(restore.Value, TdpOwner.Restore, stoppingToken);
                            wrote = true;
                        }
                    }

                    // Auto-tuner sweep takes priority over auto-FPS while it's running (both steer
                    // STAPM; running both at once would fight each other) — starting a sweep is a
                    // deliberate, explicit action, so it wins until it finishes or is restarted.
                    // Apply this tick's candidate STAPM (a flat profile — see
                    // TunerState.CurrentProfile) then feed the resulting telemetry back in. Fps stays
                    // 0 on this HX370 until PresentMon is wired, so Tick() honestly records nothing
                    // useful rather than inventing a reading — see TunerState.Tick.
                    if (tuner.Running)
                    {
                        await tdp.ApplyAsync(tuner.CurrentProfile(), TdpOwner.Tuner, stoppingToken);
                        tuner.Tick(snapshot.Fps, snapshot.CpuTempC);
                        wrote = true;
                    }
                    // Auto-TDP to target FPS — only when we actually have an FPS reading (PresentMon).
                    // `is double fps && fps > 0` rather than a lifted comparison: since telemetry went
                    // nullable, no probe means null and a probe watching an idle desktop means 0.0.
                    // Both must leave the governor inert, but they are different facts and the pattern
                    // match says which one is being handled instead of relying on `null > 0` quietly
                    // being false.
                    // ModeCatalogue.AutoFpsEligible, not `== "gaming"`. The literal meant that any
                    // second gaming-shaped mode either inherited the governor or did not depending on
                    // its spelling, with nothing anywhere stating which was intended. It matters now:
                    // `gaming-battery` deliberately does NOT get the governor, because its strategy is
                    // a driver-level frame CAP, and a cap below an active target is the one
                    // pathological pairing — the governor climbs forever chasing frames the driver is
                    // withholding, hot and loud, with no error raised anywhere.
                    else if (autoFps.Enabled && ModeCatalogue.AutoFpsEligible(mode.Active)
                             && snapshot.Fps is double measuredFps && measuredFps > 0)
                    {
                        // Written only when the answer changes what is in force (AutoFpsStep): inside
                        // the deadband this used to re-write the same limit every second. The
                        // integrator starts from the last write, so after a mode switch or a manual
                        // value it steers from what is actually applied, not from a stale 25 W.
                        var gaming = ModeProfiles.For(mode.Active) ?? new TdpProfile(25, 33, 28, 95);
                        var step = AutoFpsStep.Decide(fpsController, autoFps.TargetFps, measuredFps,
                            gaming, tdpState.Last?.Requested, minW: 8, maxW: 30);
                        autoFps.CurrentStapm = step.NextStapm;
                        if (step.Write is TdpProfile write)
                        {
                            await tdp.ApplyAsync(write, TdpOwner.AutoFps, stoppingToken);
                            wrote = true;
                        }
                    }

                    // Every 30 s, read the limits back and re-apply the last write only if they
                    // moved (TdpReasserter) — never blind, never over a rival controller. Not during
                    // a throttle: the guardian re-asserts its own ceiling above.
                    if (!wrote) await reasserter.ReassertIfDueAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            try { freezer.ThawAll(); } catch { /* best effort */ }
            // File the in-flight session so quitting the service does not lose the evening.
            try { sessions.Flush(DateTimeOffset.UtcNow); } catch { /* best effort */ }
            // The fan's shutdown restore to AUTOMATIC lives in FanWorker, next to the loop that
            // could have left it in manual.
            logger.LogInformation("GPD Forge service stopping.");
        }
    }

    /// <summary>
    /// Applies the active mode once, when the daemon starts. Until 2026-09-24 nothing did: a mode's
    /// TDP was written only when the mode CHANGED, so after a reboot or a service restart the machine
    /// ran on whatever the last writer — or the firmware default — had left, while `GET /mode` named a
    /// mode whose limits were not in force. Through ProfileApplier, so it yields exactly as a mode
    /// switch does when MotionAssistant or GPD Tool is running. A failure is logged and the loop starts
    /// anyway: the rest of this worker (guardian, history, sessions) must not depend on one ryzenadj run.
    /// </summary>
    private async Task ApplyStartupTdpAsync(CancellationToken ct)
    {
        try
        {
            var outcome = await profileApplier.ApplyAsync(mode.Active, ct);
            logger.LogInformation("Startup TDP for mode '{Mode}': {Outcome}", mode.Active, outcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Startup TDP for mode '{Mode}' failed; continuing without it.", mode.Active);
        }
    }
}
