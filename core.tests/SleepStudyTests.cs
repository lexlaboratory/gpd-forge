// GPD Forge - powercfg /sleepstudy parsing tests. GPL-3.0-or-later.
//
// The report looks like an HTML table and is not one: the tables are client-side templates and the
// data lives in a `var LocalSprData = {...}` blob that only resembles JSON. These tests pin the
// traps that make the naive implementation pass review and fail in the field - a JS object literal
// that System.Text.Json rejects, a duration in the wrong unit, and a battery "reading" of zero that
// is really the absence of a reading.
using System;
using System.Linq;
using GpdForge.Standby;
using Xunit;

namespace GpdForge.Core.Tests;

public class SleepStudyTests
{
    /// <summary>Wraps a LocalSprData body in the surrounding report the way powercfg emits it.</summary>
    private static string Report(string sessionsJson) => $$"""
        <html><head><title>Sleep Study Report</title></head><body>
        <table class="spr-table" aria-label="${$Scope.FriendlyName}"><tr><th>DURATION</th></tr></table>
        <script>
            window.onload = function() {
                var LocalSprData = {"ReportInformation":{"ReportVersion":"1.1","ReportDuration":7},
                                    "ScenarioInstances":{{sessionsJson}}};
                render(LocalSprData);
            };
        </script></body></html>
        """;

    private static string Session(
        int type = 2, string entry = "2026-08-29T09:45:14Z", string exit = "2026-08-29T14:44:00Z",
        long durationUs = 17_929_285_492, bool onAc = false, string exitReason = "Unknown",
        int entryCap = 7854, int entryFull = 40229, int exitCap = 0, int exitFull = 0,
        string extra = "") => $$"""
        {"Type":{{type}},"SessionId":7,"EnterReason":"MonitorOff","ExitReason":"{{exitReason}}",
         "EntryTimestamp":"{{entry}}","ExitTimestamp":"{{exit}}","Duration":{{durationUs}},
         "OnAc":{{(onAc ? "true" : "false")}},"BatteryCountChanged":false,
         "EntryRemainingCapacity":{{entryCap}},"EntryFullChargeCapacity":{{entryFull}},
         "ExitRemainingCapacity":{{exitCap}},"ExitFullChargeCapacity":{{exitFull}}{{extra}}}
        """;

    /// <summary>A one-hour Sleep session read at both ends: 1000 mWh gone out of a 40000 mWh pack.</summary>
    private static string MeasurableSleep(string entry = "2026-08-27T09:42:00Z") => Session(
        type: 2, entry: entry, durationUs: 3_600_000_000,
        entryCap: 20000, entryFull: 40000, exitCap: 19000, exitFull: 40000);

    // --- the shapes that break a JSON parser -------------------------------------------------------

    [Fact]
    public void Single_quoted_values_do_not_stop_the_parse()
    {
        // powercfg emits {"Value":'0x0'} - legal JavaScript, illegal JSON, and it appears ~180 KB in,
        // so a parser that cannot cope looks healthy on a short report and dies on a real one.
        var html = Report("""
            [{"Type":1,"ExitReason":"Unknown","Status":'0x0',"Duration":0,"OnAc":true,
              "EntryTimestamp":"2026-08-29T09:45:14Z","ExitTimestamp":"2026-08-29T09:45:14Z",
              "EntryRemainingCapacity":0,"EntryFullChargeCapacity":0,
              "ExitRemainingCapacity":0,"ExitFullChargeCapacity":0}]
            """);

        Assert.Single(SleepStudyParser.Parse(html).Sessions);
    }

    [Fact]
    public void A_brace_inside_a_string_does_not_end_the_payload_early()
    {
        // Scanning for a balanced '}' without tracking string state truncates the blob at the first
        // process name or path that happens to contain one.
        var html = Report("[" + Session(exitReason: "Input } Keyboard") + "]");

        Assert.Equal("Input } Keyboard", SleepStudyParser.Parse(html).Sessions[0].ExitReason);
    }

    [Fact]
    public void A_double_quote_inside_a_single_quoted_value_survives_normalisation()
    {
        var html = Report("""
            [{"Type":1,"ExitReason":"Unknown","Note":'say "hi"',"Duration":0,"OnAc":true,
              "EntryTimestamp":"2026-08-29T09:45:14Z","ExitTimestamp":"2026-08-29T09:45:14Z",
              "EntryRemainingCapacity":0,"EntryFullChargeCapacity":0,
              "ExitRemainingCapacity":0,"ExitFullChargeCapacity":0}]
            """);

        Assert.Single(SleepStudyParser.Parse(html).Sessions);
    }

    [Fact]
    public void A_report_without_the_payload_is_an_error_not_an_empty_report()
    {
        // "No sessions" and "this is not a sleep study" must not look the same to the caller.
        Assert.Throws<FormatException>(() => SleepStudyParser.Parse("<html><body>nope</body></html>"));
    }

    // --- units --------------------------------------------------------------------------------------

    [Fact]
    public void Duration_is_microseconds()
    {
        // Read as milliseconds this session is 5 minutes long and every derived figure is 60x off.
        var report = SleepStudyParser.Parse(Report("[" + Session() + "]"));

        Assert.Equal(4.98, report.Sessions[0].Duration.TotalHours, 2);
    }

    // --- when a drain figure is allowed to exist ----------------------------------------------------

    [Fact]
    public void A_sleep_read_at_both_ends_reports_a_measured_drain()
    {
        var s = SleepStudyParser.Parse(Report("[" + MeasurableSleep() + "]")).Sessions[0];

        Assert.Equal(1000, s.DrainMilliwatts!.Value, 0);
        Assert.Equal(2.5, s.DrainPctPerHour!.Value, 2);
    }

    [Fact]
    public void Hibernation_never_reports_a_drain_however_tempting_the_arithmetic()
    {
        // The long overnight sessions on this machine look like Modern Standby and are Hibernate.
        // Subtracting the capacities yields a confident four-figure milliwatt number that is
        // meaningless: the machine is off, the exit reading is absent rather than zero, and the
        // session ends when the user powers it back on, not when it stopped drawing. The report's
        // own script excludes these types from battery drain, and so does this.
        var s = SleepStudyParser.Parse(Report("[" + Session(type: 5) + "]")).Sessions[0];

        Assert.Equal(SleepStudySessionType.Hibernate, s.Type);
        Assert.Null(s.DrainMilliwatts);
        Assert.Null(s.DrainPctPerHour);
    }

    [Fact]
    public void A_missing_exit_reading_is_not_treated_as_an_empty_battery()
    {
        // ExitFullChargeCapacity 0 means the battery could not be read, not that it read zero.
        var s = SleepStudyParser.Parse(Report("[" + Session(type: 2) + "]")).Sessions[0];

        Assert.Null(s.DrainMilliwatts);
        Assert.False(s.DrainIsMeaningful);
    }

    [Fact]
    public void A_swapped_battery_invalidates_the_drain_rather_than_skewing_it()
    {
        var html = Report("[" + Session(type: 2, durationUs: 3_600_000_000, entryCap: 20000,
            entryFull: 40000, exitCap: 19000, exitFull: 40000)
            .Replace("\"BatteryCountChanged\":false", "\"BatteryCountChanged\":true") + "]");

        Assert.Null(SleepStudyParser.Parse(html).Sessions[0].DrainMilliwatts);
    }

    [Fact]
    public void A_session_that_gained_charge_is_not_reported_as_negative_drain()
    {
        var html = Report("[" + Session(type: 2, durationUs: 3_600_000_000, entryCap: 10000,
            entryFull: 40000, exitCap: 12000, exitFull: 40000) + "]");

        Assert.Null(SleepStudyParser.Parse(html).Sessions[0].DrainMilliwatts);
    }

    [Fact]
    public void A_zero_length_session_produces_no_drain_rate_instead_of_infinity()
    {
        var html = Report("[" + Session(type: 2, durationUs: 0, entryCap: 20000,
            entryFull: 40000, exitCap: 19000, exitFull: 40000) + "]");

        Assert.Null(SleepStudyParser.Parse(html).Sessions[0].DrainMilliwatts);
    }

    [Fact]
    public void Sessions_too_short_to_be_a_sleep_are_not_ranked()
    {
        // Modern Standby dips in and out for seconds; a 4-second session containing one 1% battery
        // granularity step computes an absurd rate and would win every ranking.
        var html = Report("[" + Session(type: 2, durationUs: 4_000_000, entryCap: 20000,
            entryFull: 40000, exitCap: 19600, exitFull: 40000) + "]");

        Assert.Null(SleepStudyParser.Parse(html).WorstDrain);
    }

    // --- the findings that actually answer "why did it not wake up" ---------------------------------

    [Fact]
    public void A_bugcheck_session_surfaces_its_stop_code()
    {
        // The stop code is one of the single-quoted values, so this also proves the normalisation
        // reaches nested objects and not just the top level.
        const string metadata =
            ",\"Metadata\":{\"Values\":[" +
            "{\"Key\":\"EventLog._Header\",\"Value\":\"System Event Log\"}," +
            "{\"Key\":\"EventLog.BugcheckCode\",\"Value\":'0x133'}]}";

        var html = Report("[" + Session(type: 10, extra: metadata) + "]");

        var report = SleepStudyParser.Parse(html);

        Assert.Equal("0x133", Assert.Single(report.Bugchecks).BugcheckCode);
    }

    [Fact]
    public void A_suspend_followed_by_an_abnormal_shutdown_is_reported_as_a_failed_resume()
    {
        // The question the user arrives with is "it went to sleep and never came back". The report
        // has no field for that; the adjacency is what distinguishes it from a crash while in use.
        var html = Report("[" +
            Session(type: 5, entry: "2026-08-29T09:45:14Z") + "," +
            Session(type: 9, entry: "2026-08-29T14:44:03Z", durationUs: 0) + "]");

        var failed = Assert.Single(SleepStudyParser.Parse(html).FailedResumes);

        Assert.Equal(SleepStudySessionType.Hibernate, failed.Type);
    }

    [Fact]
    public void A_suspend_that_was_resumed_cleanly_is_not_a_failed_resume()
    {
        var html = Report("[" +
            Session(type: 5, entry: "2026-08-29T09:45:14Z") + "," +
            Session(type: 0, entry: "2026-08-29T14:44:03Z", durationUs: 0) + "]");

        Assert.Empty(SleepStudyParser.Parse(html).FailedResumes);
    }

    [Fact]
    public void An_abnormal_shutdown_out_of_nowhere_is_not_blamed_on_a_suspend()
    {
        var html = Report("[" +
            Session(type: 0, entry: "2026-08-29T09:45:14Z") + "," +
            Session(type: 9, entry: "2026-08-29T14:44:03Z", durationUs: 0) + "]");

        var report = SleepStudyParser.Parse(html);

        Assert.Single(report.AbnormalShutdowns);
        Assert.Empty(report.FailedResumes);
    }

    [Fact]
    public void An_empty_session_list_is_a_valid_report_with_nothing_to_show()
    {
        var report = SleepStudyParser.Parse(Report("[]"));

        Assert.Empty(report.Sessions);
        Assert.Null(report.WorstDrain);
        Assert.Empty(report.FailedResumes);
    }
}
