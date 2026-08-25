// GPD Forge — background worker.
// Copyright (C) 2026 lexlaboratory. GPL-3.0-or-later. See ../LICENSE.

using GpdForge.Tdp;
using GpdForge.Fan;
using GpdForge.Telemetry;

namespace GpdForge;

/// <summary>
/// Orchestrates the hardware subsystems: applies the active profile, drives the
/// telemetry-fed closed loops, and restores state on resume. Phase-0 skeleton.
/// </summary>
public sealed class ForgeWorker(
    ILogger<ForgeWorker> logger,
    ITdpController tdp,
    IFanController fan,
    ITelemetryService telemetry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GPD Forge service starting.");

        // Phase 1: initialize broker + EC, load active profile, start API host.
        await fan.InitializeAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = await telemetry.ReadAsync(stoppingToken);
            logger.LogDebug("telemetry: {Snapshot}", snapshot);

            // Phase 1: closed-loop TDP verification + fan curve evaluation happen here.
            _ = tdp; // wired in gpd-tdp-control implementation

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }

        logger.LogInformation("GPD Forge service stopping.");
    }
}
