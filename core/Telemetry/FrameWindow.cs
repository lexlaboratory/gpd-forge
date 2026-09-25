// GPD Forge — a trailing, time-bounded buffer of presented frames. GPL-3.0-or-later.
//
// Pure: the clock is passed in rather than read, so the tests are deterministic. The window no longer
// decides WHOSE frames make the reading (it used to pick "the app with the most frames", which could
// be dwm or a browser as easily as the game) — FrameTarget does, and the window answers for one app.
namespace GpdForge.Telemetry;

/// <summary>What one application presented inside a span: how many frames, and the latest one.</summary>
public readonly record struct AppActivity(string Application, int Frames, DateTimeOffset LastFrameAt);

public sealed class FrameWindow(TimeSpan window, int capacity = 4096)
{
    private readonly record struct Frame(DateTimeOffset At, double Ms, string App);
    private readonly Queue<Frame> _frames = new();
    private readonly Lock _gate = new();

    public TimeSpan Window => window;

    public void Add(string application, double frameTimeMs, DateTimeOffset at)
    {
        if (frameTimeMs <= 0 || string.IsNullOrEmpty(application)) return;
        lock (_gate)
        {
            _frames.Enqueue(new Frame(at, frameTimeMs, application));
            // Bound the queue even if Evict is never called: a runaway producer must not grow it
            // without limit. The probe sizes capacity for every app on the machine over 10 s.
            while (_frames.Count > capacity) _frames.Dequeue();
            Evict(at);
        }
    }

    /// <summary>Every application with at least one frame in the trailing <paramref name="span"/>.</summary>
    public IReadOnlyList<AppActivity> Activity(DateTimeOffset now, TimeSpan span)
    {
        lock (_gate)
        {
            Evict(now);
            return InSpan(now, span)
                .GroupBy(f => f.App, StringComparer.Ordinal)
                .Select(g => new AppActivity(g.Key, g.Count(), g.Max(f => f.At)))
                .ToArray();
        }
    }

    /// <summary>
    /// Aggregates one application's frames in the trailing <paramref name="span"/>. False when there
    /// is not enough recent data to say anything — the normal state when it is not rendering, and it
    /// must read as "no FPS", not "0 FPS".
    /// </summary>
    public bool TryAggregate(DateTimeOffset now, string application, TimeSpan span, out FpsSample sample)
    {
        sample = default;
        var times = FrameTimes(now, application, span);
        if (times.Length < 2) return false;

        double mean = times.Average();
        if (mean <= 0) return false;

        sample = new FpsSample(
            Math.Round(1000.0 / mean, 1),
            Math.Round(1000.0 / OnePercentLowMs(times), 1),
            application);
        return true;
    }

    /// <summary>
    /// One application's frame times (ms) in the trailing <paramref name="span"/>, oldest first.
    /// Sorted by frame time-stamp, not by arrival: PresentMon emits some rows out of order (seen in
    /// the 2.5.1 capture of 2026-09-24), and a frame-pacing reading of an unsorted buffer would find
    /// stutter that never happened.
    /// </summary>
    public double[] FrameTimes(DateTimeOffset now, string application, TimeSpan span)
    {
        lock (_gate)
        {
            Evict(now);
            return InSpan(now, span)
                .Where(f => string.Equals(f.App, application, StringComparison.Ordinal))
                .OrderBy(f => f.At)
                .Select(f => f.Ms)
                .ToArray();
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

    // Filtered by time as well as evicted: with out-of-order rows an old frame can sit behind a newer
    // head, where the queue's eviction does not reach it until the head leaves.
    private IEnumerable<Frame> InSpan(DateTimeOffset now, TimeSpan span)
    {
        var from = now - (span < window ? span : window);
        return _frames.Where(f => f.At >= from && f.At <= now);
    }

    private void Evict(DateTimeOffset now)
    {
        var cutoff = now - window;
        while (_frames.Count > 0 && _frames.Peek().At < cutoff) _frames.Dequeue();
    }
}
