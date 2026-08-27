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
    IGpdFanController fanControl) : BackgroundService
{
    // Last observed AC state, so the per-power-source switch (below) fires only ON THE FLIP rather
    // than re-applying every tick. Null until the first snapshot arrives.
    private bool? _lastAcConnected;

    // Gated fan (PWM duty) control state — see the tick block below. _lastFanMode lets Auto restore
    // fire only ONCE per transition (not every tick); _lastFanDuty feeds FanCurve's hysteresis and
    // starts at 0 so a cold start simply adopts the curve's first reading with no holdback.
    private string? _lastFanMode;
    private int _lastFanDuty;

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
                    logger.LogWarning("Guardian [{Severity}]: {Alert}", g.Severity, g.Alert);

                if (g.ThrottleToW is int throttleW)
                {
                    // Hard cool-down: hold a flat sustained ceiling. Skips auto-FPS this tick.
                    await tdp.ApplyAsync(new TdpProfile(throttleW, throttleW, throttleW, (int)guardian.Config.TempCriticalC), stoppingToken);
                }
                else
                {
                    if (g.ClearThrottle)
                    {
                        var restore = ModeProfiles.For(mode.Active);
                        if (restore is not null) await tdp.ApplyAsync(restore.Value, stoppingToken);
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
                        await tdp.ApplyAsync(tuner.CurrentProfile(), stoppingToken);
                        tuner.Tick(snapshot.Fps, snapshot.CpuTempC);
                    }
                    // Auto-TDP to target FPS — only when we actually have an FPS reading (PresentMon).
                    // Without a real FPS source Fps is 0, so this stays inert instead of ramping TDP to max.
                    else if (autoFps.Enabled && mode.Active == "gaming" && snapshot.Fps > 0)
                    {
                        var gaming = ModeProfiles.For("gaming") ?? new TdpProfile(25, 33, 28, 95);
                        int next = fpsController.NextStapm(autoFps.TargetFps, snapshot.Fps, autoFps.CurrentStapm, minW: 8, maxW: 30);
                        autoFps.CurrentStapm = next;
                        await tdp.ApplyAsync(gaming with { StapmW = next }, stoppingToken);
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
                        if (_lastFanMode != "Auto") { fanControl.SetAuto(); _lastFanMode = "Auto"; }
                        break;
                    case "Manual":
                        _lastFanDuty = fanState.ManualDuty;
                        _ = fanControl.SetManualDuty(_lastFanDuty);   // failures are already logged inside GpdFanController
                        _lastFanMode = "Manual";
                        break;
                    case "Quiet" or "Balanced" or "Aggressive":
                        // Zero/non-finite means telemetry is unavailable, not that the CPU is cold.
                        // Never take firmware control without a trustworthy temperature sensor.
                        if (!FanControlPolicy.IsUsableTemperature(snapshot.CpuTempC))
                        {
                            fanControl.SetAuto();
                            _lastFanDuty = 0;
                            _lastFanMode = "Auto";
                            break;
                        }
                        var curve = FanCurve.ForMode(fanState.Mode) ?? FanCurve.Balanced;
                        _lastFanDuty = FanCurve.DutyForTemp(snapshot.CpuTempC, curve, FanCurve.DefaultHysteresisC, _lastFanDuty);
                        _ = fanControl.SetManualDuty(_lastFanDuty);   // failures are already logged inside GpdFanController
                        _lastFanMode = fanState.Mode;
                        break;
                    default:
                        // Defense in depth for imported/legacy state: invalid state can never leave
                        // a previous manual duty pinned. The HTTP API rejects it before this point.
                        fanControl.SetAuto();
                        _lastFanDuty = 0;
                        _lastFanMode = "Auto";
                        break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            try { freezer.ThawAll(); } catch { /* best effort */ }
            // Critical safety: always restore AUTOMATIC fan control on shutdown, even if we were
            // never in manual this run (SetAuto is idempotent / a no-op controller ignores it).
            try { fanControl.SetAuto(); } catch { /* best effort */ }
            logger.LogInformation("GPD Forge service stopping.");
        }
    }
}
