// GPD Forge — PresentMon CSV parsing and frame-window aggregation. GPL-3.0-or-later.
//
// Pure and fully unit-testable: no process, no clock, no I/O. PresentMonFrameRateProbe owns the
// process and feeds this; everything that can be got wrong about the numbers lives here.
using System.Globalization;

namespace GpdForge.Telemetry;

/// <summary>
/// Column positions resolved from the CSV header by NAME, never by index.
/// PresentMon 1.x and 2.x ship different column sets in different orders (1.x calls the frame
/// interval "MsBetweenPresents", 2.x calls it "FrameTime"), so pinning indices would silently
/// produce garbage on a version bump instead of failing.
/// </summary>
public readonly record struct PresentMonColumns(int Application, int FrameTimeMs, int Width)
{
    public bool IsValid => Application >= 0 && FrameTimeMs >= 0 && Width > 0;
}

public readonly record struct PresentMonRow(string Application, double FrameTimeMs);

public static class PresentMonCsv
{
    private static readonly string[] AppNames = ["Application"];
    private static readonly string[] FrameTimeNames = ["FrameTime", "MsBetweenPresents"];

    /// <summary>Resolves the columns we need from a header line. False if this is not a header.</summary>
    public static bool TryParseHeader(string line, out PresentMonColumns columns)
    {
        columns = default;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var fields = Split(line);
        int app = IndexOfAny(fields, AppNames);
        int frame = IndexOfAny(fields, FrameTimeNames);
        if (app < 0 || frame < 0) return false;

        columns = new PresentMonColumns(app, frame, fields.Length);
        return true;
    }

    /// <summary>
    /// Parses one data row. False for anything unusable — a truncated line, a repeated header, a
    /// non-numeric or non-positive frame time. A dropped row is always better than a wrong FPS.
    /// </summary>
    public static bool TryParseRow(string line, in PresentMonColumns columns, out PresentMonRow row)
    {
        row = default;
        if (!columns.IsValid || string.IsNullOrWhiteSpace(line)) return false;

        var fields = Split(line);
        // PresentMon re-emits its header when a new capture starts; the row must also be wide
        // enough that the columns we resolved actually exist in it.
        if (fields.Length < columns.Width) return false;

        if (!double.TryParse(fields[columns.FrameTimeMs], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
            return false;
        if (double.IsNaN(ms) || double.IsInfinity(ms) || ms <= 0) return false;

        string app = fields[columns.Application].Trim();
        if (app.Length == 0) return false;

        row = new PresentMonRow(app, ms);
        return true;
    }

    private static string[] Split(string line) => line.Trim().Split(',');

    private static int IndexOfAny(string[] fields, string[] candidates)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            string f = fields[i].Trim();
            foreach (var c in candidates)
                if (string.Equals(f, c, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }
}

/// <summary>
/// A trailing window of frame intervals, aggregated into mean FPS and the 1% low.
/// The clock is passed in rather than read, so the tests are deterministic.
/// </summary>
public sealed class FrameWindow(TimeSpan window, int capacity = 4096)
{
    private readonly record struct Frame(DateTimeOffset At, double Ms, string App);
    private readonly Queue<Frame> _frames = new();
    private readonly Lock _gate = new();

    public void Add(string application, double frameTimeMs, DateTimeOffset now)
    {
        if (frameTimeMs <= 0) return;
        lock (_gate)
        {
            _frames.Enqueue(new Frame(now, frameTimeMs, application));
            // Bound the queue even if Evict is never called: a runaway producer must not grow it
            // without limit. capacity is generous enough that it never bites at real frame rates.
            while (_frames.Count > capacity) _frames.Dequeue();
            Evict(now);
        }
    }

    /// <summary>
    /// Aggregates the window. False when there is not enough recent data to say anything — which is
    /// the normal state when nothing is rendering, and must read as "no FPS", not "0 FPS".
    /// </summary>
    public bool TryAggregate(DateTimeOffset now, out FpsSample sample)
    {
        sample = default;
        lock (_gate)
        {
            Evict(now);
            if (_frames.Count < 2) return false;

            // One app owns the reading: the one presenting the most frames. Mixing a game with the
            // compositor's own presents would average into a meaningless number.
            string app = _frames.GroupBy(f => f.App)
                                .OrderByDescending(g => g.Count())
                                .First().Key;
            var times = _frames.Where(f => f.App == app).Select(f => f.Ms).ToArray();
            if (times.Length < 2) return false;

            double mean = times.Average();
            if (mean <= 0) return false;

            sample = new FpsSample(
                Math.Round(1000.0 / mean, 1),
                Math.Round(1000.0 / OnePercentLowMs(times), 1),
                app);
            return true;
        }
    }

    /// <summary>
    /// Mean frame time of the slowest 1% of frames, as milliseconds. Always at least one frame, so
    /// it degrades to "the single worst frame" on small samples rather than dividing by zero.
    /// </summary>
    public static double OnePercentLowMs(double[] frameTimesMs)
    {
        ArgumentNullException.ThrowIfNull(frameTimesMs);
        if (frameTimesMs.Length == 0) return 0;
        var sorted = frameTimesMs.OrderByDescending(t => t).ToArray(); // slowest first
        int take = Math.Max(1, sorted.Length / 100);
        return sorted.Take(take).Average();
    }

    private void Evict(DateTimeOffset now)
    {
        var cutoff = now - window;
        while (_frames.Count > 0 && _frames.Peek().At < cutoff) _frames.Dequeue();
    }
}
