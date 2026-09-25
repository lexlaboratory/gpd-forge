// GPD Forge — the raw frame-time buffer of the target process. GPL-3.0-or-later.
//
// READ-ONLY, same gate and same defensive contract as IFrameRateProbe: no data means TryGetFrameTimes
// returns false. FpsSample summarises 2 s into two numbers; frame pacing, stutter counts and the
// 0.1 % low (plan F2) need the individual frames, so this exposes them rather than more summaries.
namespace GpdForge.Telemetry;

/// <summary>
/// The target process's frame times over the trailing <see cref="IFrameTimeSource.Span"/>, in
/// milliseconds, oldest first, ordered by when each frame happened (not when PresentMon reported it).
/// </summary>
/// <param name="Process">PresentMon's application name, e.g. "EldenRing.exe".</param>
public sealed record FrameTimeSeries(string Process, IReadOnlyList<double> FrameTimesMs);

public interface IFrameTimeSource
{
    /// <summary>How much history a series covers: the last 10 s.</summary>
    static TimeSpan Span => TimeSpan.FromSeconds(10);

    /// <summary>False when no target is presenting, or PresentMon has produced nothing yet.</summary>
    bool TryGetFrameTimes(out FrameTimeSeries series);
}
