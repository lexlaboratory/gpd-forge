// GPD Forge — background worker.
// Copyright (C) 2026 lexlaboratory. GPL-3.0-or-later. See ../LICENSE.

using GpdForge.Api;
using GpdForge.Fan;
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
    FreezerService freezer) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GPD Forge service starting.");
        await fan.InitializeAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var snapshot = await telemetry.ReadAsync(stoppingToken);

                // Auto-TDP to target FPS — only when we actually have an FPS reading (PresentMon).
                // Without a real FPS source Fps is 0, so this stays inert instead of ramping TDP to max.
                if (autoFps.Enabled && mode.Active == "gaming" && snapshot.Fps > 0)
                {
                    var gaming = ModeProfiles.For("gaming") ?? new TdpProfile(25, 33, 28, 95);
                    int next = fpsController.NextStapm(autoFps.TargetFps, snapshot.Fps, autoFps.CurrentStapm, minW: 8, maxW: 30);
                    autoFps.CurrentStapm = next;
                    await tdp.ApplyAsync(gaming with { StapmW = next }, stoppingToken);
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
