// GPD Forge - the sleep study reduced to what a panel can show. GPL-3.0-or-later.
//
// Generating a sleep study costs tens of seconds and produces a ~9 MB report, so it cannot happen
// inside a GET. A background worker produces these findings on a slow cadence and drops them here;
// GET /standby reads whatever the last run left.
namespace GpdForge.Standby;

/// <summary>One thing worth telling the user, already phrased for display.</summary>
public sealed record SleepStudyFinding(string Kind, DateTimeOffset At, string Detail);

public sealed record SleepStudySummary(
    DateTimeOffset MeasuredAt, int Sessions, IReadOnlyList<SleepStudyFinding> Findings);

public static class SleepStudyDigest
{
    public const string FailedResume = "failed-resume";
    public const string Bugcheck = "bugcheck";
    public const string WorstDrain = "worst-drain";

    public static SleepStudySummary Summarise(SleepStudyReport report, DateTimeOffset at)
    {
        var findings = new List<SleepStudyFinding>();

        foreach (var f in report.FailedResumes)
        {
            findings.Add(new(FailedResume, f.EntryAt,
                $"{f.Type} lasting {f.Duration.TotalHours:F1} h — the next thing the machine did was " +
                "an abnormal shutdown, so it did not come back on its own."));
        }

        foreach (var b in report.Bugchecks)
        {
            findings.Add(new(Bugcheck, b.EntryAt,
                $"Bugcheck, stop code {b.BugcheckCode ?? "(not recorded)"}."));
        }

        // At most one, and only where a discharge was readable at both ends — see SleepStudySession.
        if (report.WorstDrain is { } w)
        {
            findings.Add(new(WorstDrain, w.EntryAt,
                $"{w.DrainMilliwatts:F0} mW ({w.DrainPctPerHour:F1} %/h) over {w.Duration.TotalHours:F1} h " +
                $"while {w.Type}."));
        }

        return new(at, report.Sessions.Count, findings.OrderByDescending(f => f.At).ToList());
    }
}

/// <summary>
/// The last sleep study result, shared between the worker that produces it and the endpoint that
/// serves it. Distinguishes three states that must never collapse into one: not run yet, ran and
/// failed (it needs elevation), and ran successfully with nothing to report.
/// </summary>
public sealed class SleepStudyCache
{
    private readonly object _gate = new();
    private SleepStudySummary? _summary;
    private string? _error;
    private bool _ran;

    public void Record(SleepStudySummary summary)
    {
        lock (_gate) { _summary = summary; _error = null; _ran = true; }
    }

    public void RecordFailure(string error)
    {
        lock (_gate) { _summary = null; _error = error; _ran = true; }
    }

    public (bool Ran, SleepStudySummary? Summary, string? Error) Read()
    {
        lock (_gate) return (_ran, _summary, _error);
    }
}
