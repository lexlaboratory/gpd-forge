// GPD Forge — re-applies fan and power limits automatically when the machine wakes up.
// GPL-3.0-or-later.
//
// This is the piece that turns POST /standby/restore from a button into a behaviour. See
// ResumeDetector.cs for why the resume is detected from clock divergence rather than from a power
// broadcast: a Windows Service has no message pump, and WM_POWERBROADCAST would mean hosting a
// hidden window purely to be told something two clock reads already prove.
//
// The poll is deliberately faster than StandbyDrainWorker's. That one samples once a minute because
// the resolution that matters to a drain figure is the length of the sleep; here the resolution that
// matters is how long the machine runs with an uninitialized EC after waking, and a minute of hot
// and silent is the whole failure this is meant to prevent.
using GpdForge.Api;
using GpdForge.Profiles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GpdForge.Standby;

public sealed class ResumeRestoreWorker(
    IStandbyService standby,
    ModeState mode,
    IUnbiasedClock? clock = null,
    ILogger<ResumeRestoreWorker>? logger = null,
    TimeSpan? interval = null,
    Func<DateTimeOffset>? now = null) : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly IUnbiasedClock _clock = clock ?? new Win32UnbiasedClock();
    private readonly TimeSpan _interval = interval ?? DefaultInterval;
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly ResumeDetector _detector = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var unbiased = _clock.Read();
                if (unbiased is null)
                {
                    // Without a sleep-excluding clock a suspend is unprovable, and polling on would
                    // burn a syscall every five seconds forever to learn nothing. Say so once and
                    // stop: the manual POST /standby/restore still works.
                    logger?.LogWarning(
                        "Automatic resume restore is off: QueryUnbiasedInterruptTime is unavailable, " +
                        "so a suspend cannot be detected. POST /standby/restore still works.");
                    return;
                }

                var slept = _detector.Observe(_now(), unbiased.Value);
                if (slept is not null) await RestoreAsync(slept.Value, stoppingToken);

                await Task.Delay(_interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            // Same rule as the drain sampler: a dead worker costs its own feature, never the service.
            logger?.LogWarning(ex, "Automatic resume restore stopped.");
        }
    }

    private async Task RestoreAsync(TimeSpan slept, CancellationToken ct)
    {
        logger?.LogInformation(
            "Resume detected after {Minutes} min suspended; re-applying fan and power limits for mode {Mode}.",
            Math.Round(slept.TotalMinutes, 1), mode.Active);

        // RestoreAsync never throws and reports per step whether the write actually reached
        // hardware, so the log below is the outcome, not the intention.
        var outcome = await standby.RestoreAsync(ModeProfiles.For(mode.Active), ct);

        foreach (var step in outcome.Steps)
        {
            if (step.Restored) logger?.LogInformation("Resume restore [{Step}]: {Detail}", step.Name, step.Detail);
            else logger?.LogWarning("Resume restore [{Step}] did not restore: {Detail}", step.Name, step.Detail);
        }
    }
}
