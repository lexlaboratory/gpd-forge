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
/// Orchestrates the hardware subsystems: reads telemetry, and (in gaming mode, once FPS telemetry is
/// available) steers TDP toward a target FPS via the tested PID — or, while an auto-tuner sweep is
/// running, steps TDP through the sweep instead (the two never run the same tick; see below). Thaws
/// any frozen processes on stop.
/// </summary>
public sealed class ForgeWorker(
    ILogger<ForgeWorker> logger,
    ITdpController tdp,
    IFanController fan,
    ITelemetryService telemetry,
    ModeState mode,
    AutoFpsState autoFps,
    FpsTdpController fpsController,
    FreezerService freezer,
    GuardianService guardian,
    TelemetryHistory history,
    ProfileApplier profileApplier,
    PowerSourceState powerSource,
    TunerState tuner,
    FanState fanState,
    IGpdFanController fanControl,
    AlertService alerts,
    ChargeGuardService chargeGuard,
    SessionRecorder sessions) : BackgroundService
{
    // Last observed AC state, so the per-power-source switch (below) fires only ON THE FLIP rather
    // than re-applying every tick. Null until the first snapshot arrives.
    private bool? _lastAcConnected;

    // Gated fan (PWM duty) control state — see the tick block below. _lastFanMode lets Auto restore
    // fire only ONCE per transition (not every tick); _lastFanDuty is the duty last written.
    private string? _lastFanMode;
    private int _lastFanDuty;

    // Curve mode is a three-stage pipeline: TempSmoother (what temperature to react to) →
    // FanCurve.DutyForTemp (what duty that calls for, with hysteresis) → FanDutyRamp (how fast the
    // fan may get there). _lastFanTarget is the curve's own last answer, which is what its
    // hysteresis must compare against; _lastFanDuty is what was actually written. All of it resets
    // together (ResetFanCurveState) so a stale average or ramp never leaks into the next session.
    private readonly TempSmoother _fanTempSmoother = new();
    private readonly FanDutyRamp _fanRamp = new();
    private int _lastFanTarget;

    // Elapsed time between fan ticks, measured rather than assumed: a slow TDP apply earlier in the
    // tick can stretch one loop to several seconds, and both the smoother and the ramp are rates.
    private readonly System.Diagnostics.Stopwatch _fanClock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastFanTickSeconds;

    // A single missed sensor read must not hand the fan back to firmware: that SetAuto is followed
    // by a MAX safety write when curve mode resumes, which is an audible burst. The last usable
    // reading is reused for up to this long before giving up.
    private const double FanSensorGraceSeconds = 3.0;
    private double? _lastUsableTempC;
    private double _lastUsableTempAtSeconds;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GPD Forge service starting.");
        await fan.InitializeAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var snapshot = await telemetry.ReadAsync(stoppingToken);
                history.Add(new HistorySample(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), snapshot));
                sessions.Observe(snapshot, DateTimeOffset.UtcNow);

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
                    // Hard cool-down: a ceiling that never raises any limit of the active mode (see
                    // GuardianThrottle.cs — it used to lift `windows` from 15 W to 25 W). Skips
                    // auto-FPS this tick.
                    var throttle = GuardianThrottle.Profile(throttleW, ModeProfiles.For(mode.Active),
                        (int)guardian.Config.TempCriticalC);
                    await tdp.ApplyAsync(throttle, TdpOwner.ThermalGuardian, stoppingToken);
                }
                else
                {
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
                        cg.CoolToW, ModeProfiles.For(mode.Active)?.StapmW ?? int.MaxValue);

                    if (coolTo is int coolW)
                    {
                        // A ceiling, held flat. EffectiveCeiling has already refused to raise power,
                        // so reaching here means this is genuinely lower than the mode would run.
                        await tdp.ApplyAsync(new TdpProfile(coolW, coolW, coolW, 90), TdpOwner.ChargeGuard, stoppingToken);
                    }
                    else if (g.ClearThrottle || cg.ClearCool)
                    {
                        var restore = ModeProfiles.For(mode.Active);
                        if (restore is not null) await tdp.ApplyAsync(restore.Value, TdpOwner.Restore, stoppingToken);
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
                        var gaming = ModeProfiles.For(mode.Active) ?? new TdpProfile(25, 33, 28, 95);
                        int next = fpsController.NextStapm(autoFps.TargetFps, measuredFps, autoFps.CurrentStapm, minW: 8, maxW: 30);
                        autoFps.CurrentStapm = next;
                        await tdp.ApplyAsync(gaming with { StapmW = next }, TdpOwner.AutoFps, stoppingToken);
                    }
                }

                // Gated fan (PWM duty) control — see core/Fan/GpdFanController.cs. Deliberately AFTER
                // the guardian throttle above: guardian's panic path can set FanState.Mode to
                // Aggressive, and that switch must take effect the very same tick. `fanControl` is a
                // no-op (NoOpGpdFanController) whenever the fan-control gate is closed or the board is
                // unmatched, so this block is always safe to run unconditionally.
                switch (fanState.Mode)
                {
                    case "Auto":
                        // Only write on the transition INTO Auto, not every tick.
                        if (_lastFanMode != "Auto") { fanControl.SetAuto(); _lastFanMode = "Auto"; ResetFanCurveState(); }
                        break;
                    case "Manual":
                        if (_lastFanMode != "Manual") ResetFanCurveState();
                        _lastFanDuty = fanState.ManualDuty;
                        _ = fanControl.SetManualDuty(_lastFanDuty);   // failures are already logged inside GpdFanController
                        _lastFanMode = "Manual";
                        break;
                    case "Quiet" or "Balanced" or "Aggressive":
                    {
                        double nowS = _fanClock.Elapsed.TotalSeconds;
                        double dtS = _lastFanMode == fanState.Mode ? nowS - _lastFanTickSeconds : 0;
                        _lastFanTickSeconds = nowS;

                        // Zero/non-finite means telemetry is unavailable, not that the CPU is cold.
                        // Never take firmware control without a trustworthy temperature sensor —
                        // but a single missed read is reused briefly rather than bouncing the fan
                        // through firmware and back (see FanSensorGraceSeconds).
                        double tempC;
                        if (FanControlPolicy.IsUsableTemperature(snapshot.CpuTempC))
                        {
                            tempC = snapshot.CpuTempC!.Value;
                            _lastUsableTempC = tempC;
                            _lastUsableTempAtSeconds = nowS;
                        }
                        else if (_lastUsableTempC is double held && nowS - _lastUsableTempAtSeconds <= FanSensorGraceSeconds)
                        {
                            tempC = held;
                        }
                        else
                        {
                            fanControl.SetAuto();
                            _lastFanMode = "Auto";
                            ResetFanCurveState();
                            break;
                        }

                        var curve = FanCurve.ForMode(fanState.Mode) ?? FanCurve.Balanced;
                        // Smoothed, not raw: Tctl swings ten-plus degrees tick to tick under a bursty
                        // load, and DutyForTemp never delays a rise. The guardian above reacts on its
                        // own input regardless, so this never dilutes the safety margin.
                        double smoothedTempC = _fanTempSmoother.Add(tempC, dtS);
                        _lastFanTarget = FanCurve.DutyForTemp(smoothedTempC, curve, FanCurve.DefaultHysteresisC, _lastFanTarget);
                        _lastFanDuty = _fanRamp.Step(_lastFanTarget, dtS);
                        _ = fanControl.SetManualDuty(_lastFanDuty);   // failures are already logged inside GpdFanController
                        _lastFanMode = fanState.Mode;
                        break;
                    }
                    default:
                        // Defense in depth for imported/legacy state: invalid state can never leave
                        // a previous manual duty pinned. The HTTP API rejects it before this point.
                        fanControl.SetAuto();
                        _lastFanMode = "Auto";
                        ResetFanCurveState();
                        break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            try { freezer.ThawAll(); } catch { /* best effort */ }
            // File the in-flight session so quitting the service does not lose the evening.
            try { sessions.Flush(DateTimeOffset.UtcNow); } catch { /* best effort */ }
            // Critical safety: always restore AUTOMATIC fan control on shutdown, even if we were
            // never in manual this run (SetAuto is idempotent / a no-op controller ignores it).
            try { fanControl.SetAuto(); } catch { /* best effort */ }
            logger.LogInformation("GPD Forge service stopping.");
        }
    }

    private void ResetFanCurveState()
    {
        _lastFanDuty = 0;
        _lastFanTarget = 0;
        _fanTempSmoother.Reset();
        _fanRamp.Reset();
        _lastUsableTempC = null;
    }
}
