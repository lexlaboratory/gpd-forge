// GPD Forge — PresentMon CSV parsing. GPL-3.0-or-later.
//
// Pure and fully unit-testable: no process, no clock, no I/O. PresentMonFrameRateProbe owns the
// process, PresentMonFeed turns rows into a timed frame buffer; everything that can be got wrong
// about reading a single line lives here.
using System.Globalization;

namespace GpdForge.Telemetry;

/// <summary>
/// Column positions resolved from the CSV header by NAME, never by index.
/// PresentMon 1.x and 2.x ship different column sets in different orders, and 2.5.1 (the version
/// the installer ships, measured 2026-09-24) carries two different 2.x layouts in one binary, so
/// pinning indices would silently produce garbage on a version bump instead of failing.
/// </summary>
/// <param name="FrameTimeMs">The preferred frame interval: "FrameTime" if the layout has it, else
/// "MsBetweenPresents".</param>
public readonly record struct PresentMonColumns(int Application, int FrameTimeMs, int Width)
{
    /// <summary>"MsBetweenPresents" when <see cref="FrameTimeMs"/> is "FrameTime" — read only when
    /// the preferred interval of a row is "NA". -1 when there is none.</summary>
    public int FallbackFrameTimeMs { get; init; } = -1;

    /// <summary>"ProcessID", or -1.</summary>
    public int ProcessId { get; init; } = -1;

    /// <summary>The row's own time column, or -1 when the header has none in a unit we can read.</summary>
    public int Time { get; init; } = -1;

    /// <summary>Milliseconds per unit of <see cref="Time"/>: 1000 for "...InSeconds", 1 otherwise.</summary>
    public double TimeUnitMs { get; init; }

    public bool IsValid => Application >= 0 && FrameTimeMs >= 0 && Width > 0;
}

/// <param name="TimeMs">The row's capture-relative time in ms, or null when the layout has no time
/// column or this row's is "NA". Only differences between rows of one capture mean anything.</param>
public readonly record struct PresentMonRow(string Application, double FrameTimeMs)
{
    public double? TimeMs { get; init; }
    public int? ProcessId { get; init; }
}

public static class PresentMonCsv
{
    private static readonly string[] AppNames = ["Application"];
    // Candidate order is preference order: "FrameTime" is the 2.x name for the interval, and where a
    // layout also has "MsBetweenPresents" that one becomes the fallback for rows whose FrameTime is NA.
    private static readonly string[] FrameTimeNames = ["FrameTime", "MsBetweenPresents"];
    private static readonly string[] ProcessIdNames = ["ProcessID"];

    // The present's own time first, the CPU start of the frame second: both place the frame on the
    // capture's timeline, and either is enough to undo stdout buffering. Units are part of the name.
    // PresentMon 2.x prints every default time in milliseconds (2.5.1: "TimeInMs", "CPUStartTimeInMs";
    // the other 2.x layout: "CPUStartTime"); only the "...InSeconds" names — 1.x's "TimeInSeconds"
    // among them — are seconds. QPC ticks and date-times ("TimeInQPC", "CPUStartQPC",
    // "CPUStartDateTime", only produced by --qpc_time/--date_time, which the probe never passes) are
    // deliberately absent: misreading ticks as milliseconds would be worse than having no row time.
    private static readonly (string Name, double UnitMs)[] TimeNames =
    [
        ("TimeInMs", 1), ("TimeInSeconds", 1000),
        ("CPUStartTimeInMs", 1), ("CPUStartTime", 1), ("CPUStartTimeInSeconds", 1000),
    ];

    /// <summary>Resolves the columns we need from a header line. False if this is not a header.</summary>
    public static bool TryParseHeader(string line, out PresentMonColumns columns)
    {
        columns = default;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var fields = Split(line);
        int app = IndexOfFirst(fields, AppNames);
        if (app < 0) return false;

        int frameTime = IndexOf(fields, FrameTimeNames[0]);
        int msBetween = IndexOf(fields, FrameTimeNames[1]);
        int primary = frameTime >= 0 ? frameTime : msBetween;
        if (primary < 0) return false;

        int time = -1;
        double unit = 0;
        foreach (var (name, unitMs) in TimeNames)
        {
            int at = IndexOf(fields, name);
            if (at < 0) continue;
            time = at;
            unit = unitMs;
            break;
        }

        columns = new PresentMonColumns(app, primary, fields.Length)
        {
            FallbackFrameTimeMs = frameTime >= 0 ? msBetween : -1,
            ProcessId = IndexOfFirst(fields, ProcessIdNames),
            Time = time,
            TimeUnitMs = unit,
        };
        return true;
    }

    /// <summary>
    /// Parses one data row. False for anything unusable — a truncated line, a repeated header, no
    /// positive frame interval in either interval column. A dropped row is always better than a wrong
    /// FPS; but a row is only dropped for what it lacks, never for "NA" in a column we do not need.
    /// </summary>
    public static bool TryParseRow(string line, in PresentMonColumns columns, out PresentMonRow row)
    {
        row = default;
        if (!columns.IsValid || string.IsNullOrWhiteSpace(line)) return false;

        var fields = Split(line);
        // PresentMon re-emits its header when a new capture starts; the row must also be wide
        // enough that the columns we resolved actually exist in it.
        if (fields.Length < columns.Width) return false;

        double? ms = PositiveNumber(fields, columns.FrameTimeMs) ?? PositiveNumber(fields, columns.FallbackFrameTimeMs);
        if (ms is null) return false;

        string app = fields[columns.Application].Trim();
        if (app.Length == 0) return false;

        double? time = Number(fields, columns.Time) is double t && t >= 0 ? t * columns.TimeUnitMs : null;
        int? pid = columns.ProcessId >= 0 && columns.ProcessId < fields.Length
                   && int.TryParse(fields[columns.ProcessId], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p)
            ? p : null;

        row = new PresentMonRow(app, ms.Value) { TimeMs = time, ProcessId = pid };
        return true;
    }

    private static double? PositiveNumber(string[] fields, int index) =>
        Number(fields, index) is double v && v > 0 ? v : null;

    /// <summary>A finite number, or null for "NA", blanks, NaN, infinities and absent columns.</summary>
    private static double? Number(string[] fields, int index)
    {
        if (index < 0 || index >= fields.Length) return null;
        if (!double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
        return double.IsFinite(v) ? v : null;
    }

    private static string[] Split(string line) => line.Trim().Split(',');

    private static int IndexOfFirst(string[] fields, string[] candidates)
    {
        foreach (var c in candidates)
        {
            int at = IndexOf(fields, c);
            if (at >= 0) return at;
        }
        return -1;
    }

    private static int IndexOf(string[] fields, string name)
    {
        for (int i = 0; i < fields.Length; i++)
            if (string.Equals(fields[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
