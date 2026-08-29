// GPD Forge - powercfg /sleepstudy parsing. GPL-3.0-or-later.
//
// This is the diagnostic that sees what the System event log does not. On 2026-08-29 the Win 4 went
// to sleep and had to be power-cycled by hand; the event log recorded no standby transition at all
// for that night, while the sleep study had the session, the bugcheck two days earlier and every
// abnormal shutdown in the week.
//
// Three things about the report defeat the obvious implementation:
//
//   1. It is not an HTML table. The <table> elements are client-side templates full of
//      ${$Scope.Foo} placeholders; scraping them yields the scaffolding and none of the data. The
//      data is a single `var LocalSprData = {...}` blob, whose keys stay English on a Spanish
//      Windows precisely because the surrounding markup is the part that gets localised.
//   2. That blob is a JavaScript object literal, not JSON. A handful of values are single-quoted
//      ({"Value":'0x0'}), which System.Text.Json rejects - and the first of them sits ~180 KB in,
//      so a JSON-only parser passes a short fixture and dies on a real report.
//   3. Battery drain is only meaningful for some session types. The report's own script exposes
//      `ShowBatteryDrainInfo = [0 /*Active*/, 1 /*ScreenOff*/, 2 /*ModernSleep*/]` and computes a
//      discharge only when both full-charge readings are present and the battery count did not
//      change. Those rules are mirrored here rather than reinvented: without them a Hibernate
//      session - during which the machine is off and the exit reading is absent - yields a
//      confident four-figure milliwatt number that means nothing.
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GpdForge.Standby;

/// <summary>
/// Session kinds, in the report's own numbering (its `SESSION_TYPE_NAMES` array). Parsed from the
/// data rather than guessed: the long low-power overnight sessions on this machine look like Modern
/// Standby and are actually <see cref="Hibernate"/>, which changes what the numbers mean.
/// </summary>
public enum SleepStudySessionType
{
    Active = 0,
    ScreenOff = 1,
    Sleep = 2,
    Standby = 3,
    HybridSleep = 4,
    Hibernate = 5,
    HybridShutdown = 6,
    Shutdown = 7,
    SystemSleepTransitionUnknown = 8,
    AbnormalShutdown = 9,
    Bugcheck = 10,
    ReportGenerated = 11,
}

public sealed record SleepStudySession(
    SleepStudySessionType Type,
    DateTimeOffset EntryAt,
    DateTimeOffset ExitAt,
    TimeSpan Duration,
    bool OnAc,
    bool BatteryCountChanged,
    string ExitReason,
    int EntryCapacityMwh,
    int EntryFullChargeMwh,
    int ExitCapacityMwh,
    int ExitFullChargeMwh,
    IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>Below this a session is a Modern Standby dip, not a sleep worth rating.</summary>
    public static readonly TimeSpan MinimumEpisode = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether a discharge figure means anything for this session, per the report's own rules. A
    /// hibernating machine is off: its exit battery reading is absent, not zero, and the session is
    /// timestamped to when the user powered it back on rather than to when it stopped drawing.
    /// </summary>
    public bool DrainIsMeaningful =>
        Type is SleepStudySessionType.Active or SleepStudySessionType.ScreenOff or SleepStudySessionType.Sleep
        && !BatteryCountChanged
        && EntryFullChargeMwh > 0
        && ExitFullChargeMwh > 0
        && Duration > TimeSpan.Zero;

    /// <summary>Average draw across the session, in mW, or null when no honest figure exists.</summary>
    public double? DrainMilliwatts
    {
        get
        {
            if (!DrainIsMeaningful) return null;
            var drop = EntryCapacityMwh - ExitCapacityMwh;
            return drop > 0 ? drop / Duration.TotalHours : null;
        }
    }

    /// <summary>Percent of full charge per hour, derived only from a real measurement.</summary>
    public double? DrainPctPerHour =>
        DrainMilliwatts is { } mw ? 100.0 * mw / EntryFullChargeMwh : null;

    /// <summary>The stop code, for a <see cref="SleepStudySessionType.Bugcheck"/> session.</summary>
    public string? BugcheckCode =>
        Metadata.TryGetValue("EventLog.BugcheckCode", out var c) ? c : null;

    internal bool IsSuspend => Type is SleepStudySessionType.Sleep or SleepStudySessionType.Standby
        or SleepStudySessionType.HybridSleep or SleepStudySessionType.Hibernate;
}

public sealed record SleepStudyReport(IReadOnlyList<SleepStudySession> Sessions)
{
    /// <summary>The worst honestly-measurable drain across a session long enough to rate.</summary>
    public SleepStudySession? WorstDrain => Sessions
        .Where(s => s.Duration >= SleepStudySession.MinimumEpisode && s.DrainMilliwatts is not null)
        .OrderByDescending(s => s.DrainMilliwatts!.Value)
        .FirstOrDefault();

    public IReadOnlyList<SleepStudySession> Bugchecks =>
        Sessions.Where(s => s.Type == SleepStudySessionType.Bugcheck).ToList();

    public IReadOnlyList<SleepStudySession> AbnormalShutdowns =>
        Sessions.Where(s => s.Type == SleepStudySessionType.AbnormalShutdown).ToList();

    /// <summary>
    /// Suspends the machine never came back from: a suspend immediately followed by an abnormal
    /// shutdown. This is an inference from adjacency, not a field the report provides — but it is
    /// the question a user actually arrives with ("it slept and never woke up"), and the adjacency
    /// is exactly what distinguishes that from a crash while in use.
    /// </summary>
    public IReadOnlyList<SleepStudySession> FailedResumes
    {
        get
        {
            var ordered = Sessions.OrderBy(s => s.EntryAt).ToList();
            var failed = new List<SleepStudySession>();
            for (var i = 0; i < ordered.Count - 1; i++)
            {
                if (ordered[i].IsSuspend && ordered[i + 1].Type == SleepStudySessionType.AbnormalShutdown)
                    failed.Add(ordered[i]);
            }
            return failed;
        }
    }
}

public static class SleepStudyParser
{
    public static SleepStudyReport Parse(string html)
    {
        var json = ExtractPayload(html);
        using var doc = JsonDocument.Parse(json);

        var sessions = new List<SleepStudySession>();
        if (doc.RootElement.TryGetProperty("ScenarioInstances", out var instances) &&
            instances.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in instances.EnumerateArray())
            {
                var s = ReadSession(el);
                if (s is not null) sessions.Add(s);
            }
        }
        return new SleepStudyReport(sessions);
    }

    private static SleepStudySession? ReadSession(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var entry = Time(el, "EntryTimestamp");
        var exit = Time(el, "ExitTimestamp");
        if (entry is null || exit is null) return null;

        return new SleepStudySession(
            Type: (SleepStudySessionType)(int)Number(el, "Type"),
            EntryAt: entry.Value,
            ExitAt: exit.Value,
            // Microseconds. Read as milliseconds, a five-hour night becomes five minutes and every
            // derived figure comes out 60x too small — plausible, and wrong.
            Duration: TimeSpan.FromMicroseconds(Number(el, "Duration")),
            OnAc: Flag(el, "OnAc"),
            BatteryCountChanged: Flag(el, "BatteryCountChanged"),
            ExitReason: Text(el, "ExitReason") ?? "Unknown",
            EntryCapacityMwh: (int)Number(el, "EntryRemainingCapacity"),
            EntryFullChargeMwh: (int)Number(el, "EntryFullChargeCapacity"),
            ExitCapacityMwh: (int)Number(el, "ExitRemainingCapacity"),
            ExitFullChargeMwh: (int)Number(el, "ExitFullChargeCapacity"),
            Metadata: ReadMetadata(el));
    }

    /// <summary>Flattens Metadata.Values ([{Key,Value}, ...]) into a lookup.</summary>
    private static IReadOnlyDictionary<string, string> ReadMetadata(JsonElement el)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!el.TryGetProperty("Metadata", out var meta) || meta.ValueKind != JsonValueKind.Object)
            return map;
        if (!meta.TryGetProperty("Values", out var values) || values.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var v in values.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Object) continue;
            if (Text(v, "Key") is not { } key || !v.TryGetProperty("Value", out var val)) continue;
            map[key] = val.ValueKind == JsonValueKind.String ? val.GetString()! : val.ToString();
        }
        return map;
    }

    private static bool Flag(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Number(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static DateTimeOffset? Time(JsonElement el, string name) =>
        Text(el, name) is { } s &&
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t : null;

    // --- payload extraction ---------------------------------------------------------------------

    private const string Marker = "LocalSprData";

    /// <summary>
    /// Pulls the object literal out of the report and returns it as JSON. Tracks string state so a
    /// brace inside a process name or path cannot end the scan early, and rewrites single-quoted
    /// literals into double-quoted ones on the way past.
    /// </summary>
    private static string ExtractPayload(string html)
    {
        var marker = html.IndexOf(Marker, StringComparison.Ordinal);
        if (marker < 0)
            throw new FormatException($"No '{Marker}' payload — this does not look like a powercfg sleep study report.");

        var assign = html.IndexOf('=', marker + Marker.Length);
        if (assign < 0) throw new FormatException($"'{Marker}' is present but never assigned.");

        var start = assign + 1;
        while (start < html.Length && char.IsWhiteSpace(html[start])) start++;
        if (start >= html.Length || html[start] != '{')
            throw new FormatException($"'{Marker}' is not assigned an object literal.");

        var sb = new StringBuilder();
        var depth = 0;
        for (var i = start; i < html.Length; i++)
        {
            var c = html[i];
            if (c is '"' or '\'')
            {
                i = CopyString(html, i, sb);
                continue;
            }
            sb.Append(c);
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return sb.ToString();
        }
        throw new FormatException($"'{Marker}' object literal is never closed.");
    }

    /// <summary>
    /// Copies one string literal, emitting it double-quoted. Returns the index of its closing quote.
    /// </summary>
    private static int CopyString(string s, int open, StringBuilder sb)
    {
        var quote = s[open];
        sb.Append('"');
        for (var i = open + 1; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                var next = s[i + 1];
                // \' is legal JavaScript and illegal JSON; unescape it rather than pass it through.
                if (next == '\'') sb.Append('\'');
                else sb.Append(c).Append(next);
                i++;
                continue;
            }
            if (c == quote)
            {
                sb.Append('"');
                return i;
            }
            // Only reachable inside a single-quoted literal, where a bare " is legal.
            if (c == '"') { sb.Append("\\\""); continue; }
            sb.Append(c);
        }
        throw new FormatException($"Unterminated string literal in the '{Marker}' payload.");
    }
}
