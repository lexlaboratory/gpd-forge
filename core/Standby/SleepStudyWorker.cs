// GPD Forge - keeps the sleep study findings fresh without blocking a request. GPL-3.0-or-later.
//
// The report costs tens of seconds and ~9 MB, and the data it covers changes at the pace of the
// user's sleep habits, so this runs rarely and never on the request path. The first run is delayed:
// the daemon starts with the machine, and generating a sleep study while Windows is still bringing
// services up would compete with the boot it is meant to observe.
using GpdForge.Tdp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GpdForge.Standby;

public sealed class SleepStudyWorker(
    SleepStudyCache cache,
    IProcessRunner? runner = null,
    ILogger<SleepStudyWorker>? logger = null,
    TimeSpan? interval = null,
    TimeSpan? initialDelay = null,
    Func<DateTimeOffset>? now = null,
    int days = 7) : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMinutes(2);

    private readonly SleepStudyProbe _probe = new(runner ?? new SystemProcessRunner());
    private readonly TimeSpan _interval = interval ?? DefaultInterval;
    private readonly TimeSpan _initialDelay = initialDelay ?? DefaultInitialDelay;
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_initialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(_interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Sleep study sampling stopped.");
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var outcome = await _probe.RunAsync(days, ct);
        if (!outcome.Available || outcome.Report is null)
        {
            // Not an error worth shouting about: outside an elevated session powercfg simply refuses,
            // and the panel says so rather than showing an empty report as if it were a clean one.
            cache.RecordFailure(outcome.Error ?? "the sleep study could not be generated.");
            logger?.LogInformation("Sleep study unavailable: {Error}", outcome.Error);
            return;
        }

        var summary = SleepStudyDigest.Summarise(outcome.Report, _now());
        cache.Record(summary);

        foreach (var f in summary.Findings.Where(f => f.Kind != SleepStudyDigest.WorstDrain))
            logger?.LogWarning("Sleep study [{Kind}] {At:u}: {Detail}", f.Kind, f.At, f.Detail);

        logger?.LogInformation(
            "Sleep study parsed: {Sessions} sessions, {Findings} finding(s).",
            summary.Sessions, summary.Findings.Count);
    }
}
