// GPD Forge — pure aggregation helpers for play sessions. GPL-3.0-or-later.
//
// No clock, no I/O, no state: everything here is a function of its arguments, so the numbers the UI
// shows are the numbers the tests pin down.

namespace GpdForge.Sessions;

public static class SessionMath
{
    /// <summary>Mean of a series, or null when the series is empty. Never returns 0 for "no data" —
    /// a zero average frame rate and an unmeasured one are completely different statements.</summary>
    public static double? MeanOrNull(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return null;
        double sum = 0;
        foreach (var v in values) sum += v;
        return Round(sum / values.Count);
    }

    /// <summary>
    /// The session's 1% low, in FPS: the mean of the worst 1% (at least one) of the per-window 1%-low
    /// readings collected during the session. This is a percentile of percentiles, not of raw frames —
    /// the raw frame times only ever exist inside the probe's two-second window and are never
    /// retained — so it is deliberately the conservative reading of the stutter the session actually
    /// contained, not a re-derivation of it.
    /// </summary>
    public static double? OnePercentLow(IReadOnlyList<double> perWindowLows)
    {
        ArgumentNullException.ThrowIfNull(perWindowLows);
        if (perWindowLows.Count == 0) return null;
        var sorted = perWindowLows.OrderBy(x => x).ToArray(); // worst (lowest FPS) first
        int take = Math.Max(1, sorted.Length / 100);
        return Round(sorted.Take(take).Average());
    }

    /// <summary>
    /// Reduces a series to at most <paramref name="points"/> values by averaging equal-width buckets,
    /// preserving order. Averaging rather than picking every Nth sample keeps a spike from vanishing
    /// between the samples that survive.
    /// </summary>
    public static IReadOnlyList<double> Downsample(IReadOnlyList<double> values, int points)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThan(points, 1);
        if (values.Count <= points) return values.Select(Round).ToArray();

        var result = new double[points];
        for (int i = 0; i < points; i++)
        {
            int start = (int)((long)i * values.Count / points);
            int end = (int)((long)(i + 1) * values.Count / points);
            if (end <= start) end = start + 1;
            double sum = 0;
            for (int j = start; j < end; j++) sum += values[j];
            result[i] = Round(sum / (end - start));
        }
        return result;
    }

    /// <summary>
    /// Rolls sessions up per application, most-played first. Averages are weighted by duration, so a
    /// two-minute run cannot pull the average of a three-hour one around. A game whose sessions never
    /// carried an FPS reading keeps null averages rather than gaining an invented zero.
    /// </summary>
    public static IReadOnlyList<GameSummary> PerGame(IEnumerable<GameSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return sessions
            .GroupBy(s => s.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GameSummary(
                App: g.First().App,
                Sessions: g.Count(),
                TotalSeconds: Round(g.Sum(s => s.DurationSeconds)),
                LastPlayedUtc: g.Max(s => s.StartedUtc),
                FpsAvg: Weighted(g, s => s.FpsAvg),
                FpsBest: MaxOrNull(g, s => s.FpsMax ?? s.FpsAvg),
                Fps1PctLow: Weighted(g, s => s.Fps1PctLow),
                CpuTempMaxC: MaxOrNull(g, s => s.CpuTempMaxC)))
            .OrderByDescending(x => x.TotalSeconds)
            .ThenByDescending(x => x.LastPlayedUtc)
            .ToArray();
    }

    private static double? Weighted(IEnumerable<GameSession> sessions, Func<GameSession, double?> selector)
    {
        double weight = 0, total = 0;
        foreach (var s in sessions)
        {
            if (selector(s) is not double value) continue;
            // A session with no measured duration still carries a reading; weight it as one sample
            // rather than discarding it.
            double w = s.DurationSeconds > 0 ? s.DurationSeconds : 1;
            weight += w;
            total += value * w;
        }
        return weight > 0 ? Round(total / weight) : null;
    }

    private static double? MaxOrNull(IEnumerable<GameSession> sessions, Func<GameSession, double?> selector)
    {
        double? best = null;
        foreach (var s in sessions)
            if (selector(s) is double value && (best is null || value > best)) best = value;
        return best is double b ? Round(b) : null;
    }

    /// <summary>One decimal is the resolution the sensors and the HUD actually have; more would be
    /// noise, and it keeps the persisted JSON small.</summary>
    internal static double Round(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
