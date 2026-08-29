// GPD Forge - tests for the sleep study digest and its background sampler. GPL-3.0-or-later.
//
// The point of these is the state machine, not the phrasing: "not sampled yet", "powercfg refused"
// and "sampled, nothing found" are three different answers, and collapsing any two of them turns a
// diagnostic into a reassurance.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GpdForge.Standby;
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

public class SleepStudyDigestTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);

    private static SleepStudySession S(
        SleepStudySessionType type, int hour, double hours = 1,
        int entryCap = 0, int entryFull = 0, int exitCap = 0, int exitFull = 0,
        IReadOnlyDictionary<string, string>? meta = null)
        => new(type, T0.AddHours(hour), T0.AddHours(hour + hours), TimeSpan.FromHours(hours),
               OnAc: false, BatteryCountChanged: false, ExitReason: "Unknown",
               entryCap, entryFull, exitCap, exitFull,
               meta ?? new Dictionary<string, string>());

    [Fact]
    public void A_failed_resume_becomes_a_finding()
    {
        var report = new SleepStudyReport([
            S(SleepStudySessionType.Hibernate, 0, 5),
            S(SleepStudySessionType.AbnormalShutdown, 5, 0),
        ]);

        var summary = SleepStudyDigest.Summarise(report, T0);

        var f = Assert.Single(summary.Findings, x => x.Kind == SleepStudyDigest.FailedResume);
        Assert.Contains("did not come back", f.Detail);
    }

    [Fact]
    public void A_bugcheck_carries_its_stop_code_into_the_finding()
    {
        var report = new SleepStudyReport([
            S(SleepStudySessionType.Bugcheck, 1, 0,
              meta: new Dictionary<string, string> { ["EventLog.BugcheckCode"] = "0x133" }),
        ]);

        var summary = SleepStudyDigest.Summarise(report, T0);

        Assert.Contains("0x133", Assert.Single(summary.Findings).Detail);
    }

    [Fact]
    public void A_clean_report_produces_a_summary_with_no_findings_rather_than_nothing()
    {
        // "We looked and all is well" must still carry the session count, or the panel cannot tell
        // it apart from "we never looked".
        var summary = SleepStudyDigest.Summarise(new SleepStudyReport([S(SleepStudySessionType.Active, 0)]), T0);

        Assert.Empty(summary.Findings);
        Assert.Equal(1, summary.Sessions);
    }

    [Fact]
    public void Findings_are_newest_first()
    {
        var report = new SleepStudyReport([
            S(SleepStudySessionType.Bugcheck, 1, 0,
              meta: new Dictionary<string, string> { ["EventLog.BugcheckCode"] = "0x1" }),
            S(SleepStudySessionType.Hibernate, 10, 1),
            S(SleepStudySessionType.AbnormalShutdown, 11, 0),
        ]);

        var findings = SleepStudyDigest.Summarise(report, T0).Findings;

        Assert.True(findings[0].At > findings[^1].At);
    }

    // --- the cache's three states -----------------------------------------------------------------

    [Fact]
    public void An_unread_cache_reports_that_it_never_ran()
    {
        var (ran, summary, error) = new SleepStudyCache().Read();

        Assert.False(ran);
        Assert.Null(summary);
        Assert.Null(error);
    }

    [Fact]
    public void A_failure_is_distinguishable_from_a_clean_run()
    {
        var cache = new SleepStudyCache();
        cache.RecordFailure("needs elevation");

        var (ran, summary, error) = cache.Read();

        Assert.True(ran);
        Assert.Null(summary);
        Assert.Equal("needs elevation", error);
    }

    [Fact]
    public void A_later_success_clears_an_earlier_failure()
    {
        var cache = new SleepStudyCache();
        cache.RecordFailure("needs elevation");
        cache.Record(new SleepStudySummary(T0, 3, []));

        var (_, summary, error) = cache.Read();

        Assert.Null(error);
        Assert.Equal(3, summary!.Sessions);
    }
}

public class SleepStudyWorkerTests
{
    /// <summary>Returns a canned report for powercfg, and records that it was asked.</summary>
    private sealed class FakeRunner(string? html) : IProcessRunner
    {
        public int Calls { get; private set; }

        public Task<string> RunAsync(string exePath, string arguments, CancellationToken ct)
        {
            Calls++;
            // The probe locates the report by the /output path it passed, so write one there.
            var start = arguments.IndexOf('"') + 1;
            var path = arguments[start..arguments.IndexOf('"', start)];
            if (html is not null) File.WriteAllText(path, html);
            return Task.FromResult(string.Empty);
        }
    }

    private const string Report = """
        <html><body><script>window.onload = function() {
        var LocalSprData = {"ScenarioInstances":[
          {"Type":5,"ExitReason":"Unknown","Duration":18000000000,"OnAc":false,"BatteryCountChanged":false,
           "EntryTimestamp":"2026-08-29T03:45:14Z","ExitTimestamp":"2026-08-29T08:44:00Z",
           "EntryRemainingCapacity":7854,"EntryFullChargeCapacity":40229,
           "ExitRemainingCapacity":0,"ExitFullChargeCapacity":0},
          {"Type":9,"ExitReason":"Unknown","Duration":0,"OnAc":false,"BatteryCountChanged":false,
           "EntryTimestamp":"2026-08-29T08:44:03Z","ExitTimestamp":"2026-08-29T08:44:03Z",
           "EntryRemainingCapacity":0,"EntryFullChargeCapacity":0,
           "ExitRemainingCapacity":0,"ExitFullChargeCapacity":0}]};
        };</script></body></html>
        """;

    private static async Task<SleepStudyCache> RunWorkerAsync(FakeRunner runner)
    {
        var cache = new SleepStudyCache();
        var worker = new SleepStudyWorker(
            cache, runner, logger: null,
            interval: TimeSpan.FromMilliseconds(50),
            initialDelay: TimeSpan.Zero,
            now: () => new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        for (var i = 0; i < 100 && !cache.Read().Ran; i++) await Task.Delay(10);
        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
        return cache;
    }

    [Fact]
    public async Task The_worker_surfaces_the_night_the_machine_did_not_wake_up()
    {
        var cache = await RunWorkerAsync(new FakeRunner(Report));

        var (ran, summary, error) = cache.Read();

        Assert.True(ran);
        Assert.Null(error);
        Assert.Contains(summary!.Findings, f => f.Kind == SleepStudyDigest.FailedResume);
    }

    [Fact]
    public async Task A_refused_powercfg_is_recorded_as_unavailable_not_as_a_clean_report()
    {
        // Outside an elevated session powercfg writes nothing and exits cleanly. Treating the missing
        // file as "no findings" would tell the user their machine is healthy on no evidence at all.
        var cache = await RunWorkerAsync(new FakeRunner(null));

        var (ran, summary, error) = cache.Read();

        Assert.True(ran);
        Assert.Null(summary);
        Assert.NotNull(error);
    }
}
