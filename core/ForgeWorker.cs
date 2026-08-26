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

namespace GpdForge;

/// <summary>
/// Orchestrates the hardware subsystems: reads telemetry, and (in gaming mode, once FPS telemetry is
/// available) steers TDP toward a target FPS via the tested PID. Thaws any frozen processes on stop.
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
    PowerSourceState powerSource) : BackgroundService
{
    // Last observed AC state, so the per-power-source switch (below) fires only ON THE FLIP rather
    // than re-applying every tick. Null until the first snapshot arrives.
    private bool? _lastAcConnected;

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

                    // Auto-TDP to target FPS — only when we actually have an FPS reading (PresentMon).
                    // Without a real FPS source Fps is 0, so this stays inert instead of ramping TDP to max.
                    if (autoFps.Enabled && mode.Active == "gaming" && snapshot.Fps > 0)
                    {
                        var gaming = ModeProfiles.For("gaming") ?? new TdpProfile(25, 33, 28, 95);
                        int next = fpsController.NextStapm(autoFps.TargetFps, snapshot.Fps, autoFps.CurrentStapm, minW: 8, maxW: 30);
                        autoFps.CurrentStapm = next;
                        await tdp.ApplyAsync(gaming with { StapmW = next }, stoppingToken);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            try { freezer.ThawAll(); } catch { /* best effort */ }
            logger.LogInformation("GPD Forge service stopping.");
        }
    }
}
