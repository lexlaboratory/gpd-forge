// GPD Forge — the battery sampler that makes an overnight drain measurable. GPL-3.0-or-later.
//
// A drain figure needs a battery reading from BEFORE the suspend, so something has to keep sampling
// while nothing is watching. One cheap WMI read a minute is enough: the resolution that matters is
// the length of the sleep, not the sampling rate, and a slow tick keeps the pre-suspend sample from
// being the thing that keeps the machine awake.
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GpdForge.Standby;

public sealed class StandbyDrainWorker(
    IStandbyService standby, ILogger<StandbyDrainWorker>? logger = null) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await standby.SampleAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            // A dead sampler must cost the drain figure only — never the whole service.
            logger?.LogWarning(ex, "Standby drain sampler stopped.");
        }
    }
}
