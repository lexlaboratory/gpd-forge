// GPD Forge — frame-pacing metrics over a frame-time series (plan F2). GPL-3.0-or-later.
//
// Pure: a function of the frame times it is handed, no clock and no state, so GET /frames, the session
// recorder and the tests all read the same numbers. The input is IFrameTimeSource's 10 s buffer.
//
// Why these metrics: an average FPS hides exactly what a player feels. Two runs at 60 FPS can differ by
// a handful of 60 ms frames, and those are the hitches. The 1 % / 0.1 % lows and the frame-time spread
// say how uneven the run is; the stutter count says how often it visibly hitched.
namespace GpdForge.Telemetry;

/// <param name="Frames">Frames the metrics were computed over (after discarding non-durations).</param>
/// <param name="SpanSeconds">The time those frames cover — the denominator of <paramref name="StuttersPerMin"/>.</param>
/// <param name="Fps1PctLow">1000 / the mean of the slowest 1 % of frames (at least one).</param>
/// <param name="Fps01PctLow">1000 / the mean of the slowest 0.1 % of frames (at least one).</param>
/// <param name="FrameTimeStdDevMs">Population standard deviation of the frame times.</param>
/// <param name="Stutters">Frames slower than both twice the rolling median and <see cref="FramePacing.StutterFloorMs"/>.</param>
public sealed record FramePacingMetrics(
    int Frames,
    double SpanSeconds,
    double FpsAvg,
    double Fps1PctLow,
    double Fps01PctLow,
    double FrameTimeStdDevMs,
    int Stutters,
    double StuttersPerMin);

public static class FramePacing
{
    /// <summary>A frame must take longer than this to count as a stutter. Without a floor, a 20 ms frame
    /// at 125 FPS would count: it is 2.5x the median, yet faster than a steady 50 FPS frame, and nobody
    /// feels it. 25 ms is a 40 FPS frame — the point where a single late frame becomes visible.</summary>
    public const double StutterFloorMs = 25;

    /// <summary>A stutter is a frame slower than this multiple of the local median.</summary>
    public const double StutterFactor = 2;

    /// <summary>Frames in the rolling-median window, centred on the frame judged. ~0.5 s at 60 FPS: wide
    /// enough that a spike cannot drag its own median up, narrow enough that a scene change from 60 to
    /// 30 FPS moves the median with it instead of reading as a run of stutters.</summary>
    public const int MedianWindow = 31;

    /// <summary>The metrics of a frame-time series, or null when it holds fewer than two real frames —
    /// too little to say anything, which must read as "no data", never as a zero.</summary>
    public static FramePacingMetrics? Compute(IReadOnlyList<double> frameTimesMs)
    {
        ArgumentNullException.ThrowIfNull(frameTimesMs);
        // A zero, negative or NaN frame time is a PresentMon artefact, not a frame.
        var times = frameTimesMs.Where(t => double.IsFinite(t) && t > 0).ToArray();
        if (times.Length < 2) return null;

        double totalMs = times.Sum();
        double mean = totalMs / times.Length;
        double variance = times.Sum(t => (t - mean) * (t - mean)) / times.Length;
        int stutters = CountStutters(times);
        double minutes = totalMs / 60_000;

        return new FramePacingMetrics(
            Frames: times.Length,
            SpanSeconds: Round1(totalMs / 1000),
            FpsAvg: Round1(1000 / mean),
            Fps1PctLow: Round1(1000 / SlowestMeanMs(times, 0.01)),
            Fps01PctLow: Round1(1000 / SlowestMeanMs(times, 0.001)),
            FrameTimeStdDevMs: Math.Round(Math.Sqrt(variance), 2, MidpointRounding.AwayFromZero),
            Stutters: stutters,
            StuttersPerMin: minutes > 0 ? Round1(stutters / minutes) : 0);
    }

    /// <summary>The newest <paramref name="max"/> frames, oldest first — GET /frames caps its series so a
    /// 240 FPS game does not put 2 400 numbers on the wire every poll for a graph 380 px wide.</summary>
    public static IReadOnlyList<double> Latest(IReadOnlyList<double> frameTimesMs, int max)
    {
        ArgumentNullException.ThrowIfNull(frameTimesMs);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        return frameTimesMs.Count <= max ? frameTimesMs : frameTimesMs.Skip(frameTimesMs.Count - max).ToArray();
    }

    /// <summary>Mean of the slowest <paramref name="fraction"/> of frames, always at least one — the same
    /// definition as <see cref="FrameWindow.OnePercentLowMs"/>, so the two 1 % lows agree.</summary>
    private static double SlowestMeanMs(double[] times, double fraction)
    {
        int take = Math.Max(1, (int)(times.Length * fraction));
        return times.OrderByDescending(t => t).Take(take).Average();
    }

    private static int CountStutters(double[] times)
    {
        int half = MedianWindow / 2, count = 0;
        var window = new double[MedianWindow];
        for (int i = 0; i < times.Length; i++)
        {
            if (times[i] <= StutterFloorMs) continue; // cheap reject: most frames never need a median
            int from = Math.Max(0, i - half), to = Math.Min(times.Length - 1, i + half), n = to - from + 1;
            Array.Copy(times, from, window, 0, n);
            Array.Sort(window, 0, n);
            double median = n % 2 == 1 ? window[n / 2] : (window[n / 2 - 1] + window[n / 2]) / 2;
            if (times[i] > StutterFactor * median) count++;
        }
        return count;
    }

    private static double Round1(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
