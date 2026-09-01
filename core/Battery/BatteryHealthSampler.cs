// GPD Forge — records one battery-health sample a day, unattended. GPL-3.0-or-later.
//
// Its own hosted service rather than a branch in ForgeWorker, which ticks at 1 Hz. A daily job
// living inside a one-second loop needs a counter that exists purely to say "not yet" 86,399 times,
// and the day someone changes the tick interval the sampling rate changes with it.
//
// It also must not depend on the panel being open. Health history is the input to every future
// battery decision, and a history that only accumulates when someone happens to look at a page is
// worst on exactly the machines that would benefit most — the ones left running unattended.
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GpdForge.Battery;

public sealed class BatteryHealthSampler(
    IBatteryHealthProbe probe,
    BatteryHealthHistory history,
    ILogger<BatteryHealthSampler>? logger = null) : BackgroundService
{
    /// <summary>Four attempts a day against a store that accepts one. The redundancy is the point:
    /// a handheld is asleep or off for most of the day, so a single daily attempt at a fixed hour
    /// would miss on any day the machine was not awake for it.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>Long enough for WMI to be ready, short enough that a machine used for ten minutes a
    /// day still records a sample.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reading = probe.Read();
                if (history.Observe(reading))
                    logger?.LogInformation(
                        "Battery health sampled: {Health}% ({Full} of {Design} mWh).",
                        reading.HealthPercent, reading.FullChargeMilliwattHours, reading.DesignedMilliwattHours);
            }
            catch (Exception ex)
            {
                // A failed sample is a gap in a years-long series, not a reason to stop sampling —
                // and certainly not a reason to take the daemon down. The next tick tries again.
                logger?.LogDebug(ex, "Battery health sampling failed; will retry at the next interval.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
