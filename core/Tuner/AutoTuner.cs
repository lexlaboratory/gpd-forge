// GPD Forge — auto-tuner pick logic (pure). GPL-3.0-or-later.
//
// A TDP sweep (driven by the worker — see TunerState.Tick) holds a sequence of STAPM values and
// records what FPS/temperature the system settled at while holding each one. AutoTuner.PickBest is
// the pure decision on top of that recorded data: given the sweep's points and a goal, which STAPM
// is "best"? No I/O, no timers, no hardware — trivially unit-testable, and the only part of the
// tuner that needs to be exhaustive, since the sweep-stepping around it is just plumbing.
namespace GpdForge.Tuner;

/// <summary>One dwell sample from a TDP sweep: the STAPM watts held, and what FPS/temperature the
/// system settled at while holding it. Fps is only ever recorded when it is a real, positive
/// reading — see the honesty note on <see cref="TunerState.Tick"/>.</summary>
public readonly record struct TunePoint(int StapmW, double Fps, double TempC);

/// <summary>What the auto-tuner optimizes for.</summary>
public enum TuneGoal
{
    /// <summary>Highest FPS, regardless of watts (within the temp cap).</summary>
    MaxFps,

    /// <summary>Highest FPS-per-watt (within the temp cap) — the most frames for the least power.</summary>
    BestEfficiency,

    /// <summary>Lowest watts whose FPS still meets or exceeds a target (within the temp cap).</summary>
    HoldTarget,
}

/// <summary>The tuner's pick: which STAPM watts to run at, what the sweep measured there, and a
/// human-readable reason (surfaced by the UI/MCP so the choice is explainable, not a black box).</summary>
public readonly record struct TuneResult(int StapmW, double Fps, double TempC, string Note);

/// <summary>
/// PURE decision logic over a set of measured sweep points. Deterministic: identical inputs always
/// produce an identical result (or lack of one), so it is trivially unit-testable and safe to call
/// as often as GET /tuner is polled.
/// </summary>
public static class AutoTuner
{
    /// <summary>
    /// Picks the best recorded point for <paramref name="goal"/>, or <c>null</c> when there is
    /// nothing usable to pick from: no points at all, every point exceeds <paramref
    /// name="tempCapC"/>, or (for <see cref="TuneGoal.HoldTarget"/>) no point reaches <paramref
    /// name="targetFps"/> — including when <paramref name="targetFps"/> itself is null, since there
    /// is nothing to hold. Never throws.
    /// </summary>
    public static TuneResult? PickBest(IReadOnlyList<TunePoint> points, TuneGoal goal, int? targetFps, int tempCapC)
    {
        if (points is null || points.Count == 0) return null;

        var underCap = points.Where(p => p.TempC <= tempCapC).ToList();
        if (underCap.Count == 0) return null;

        return goal switch
        {
            TuneGoal.MaxFps => PickMaxFps(underCap),
            TuneGoal.BestEfficiency => PickBestEfficiency(underCap),
            TuneGoal.HoldTarget => PickHoldTarget(underCap, targetFps),
            _ => null,
        };
    }

    // Highest fps wins; tie -> lowest watts (same performance for less power is strictly better).
    private static TuneResult PickMaxFps(List<TunePoint> pts)
    {
        var best = pts.OrderByDescending(p => p.Fps).ThenBy(p => p.StapmW).First();
        return new TuneResult(best.StapmW, best.Fps, best.TempC, $"Highest FPS at or under the {best.TempC:0}°C cap.");
    }

    // Fps-per-watt wins; tie on efficiency -> lowest watts; still tied -> highest fps (more headroom
    // for the same efficiency and power).
    private static TuneResult PickBestEfficiency(List<TunePoint> pts)
    {
        var best = pts.OrderByDescending(Efficiency).ThenBy(p => p.StapmW).ThenByDescending(p => p.Fps).First();
        return new TuneResult(best.StapmW, best.Fps, best.TempC, $"Best FPS-per-watt ({Efficiency(best):0.00} fps/W).");
    }

    // A profile never reports 0 (or negative) STAPM, but guard the division anyway rather than
    // trusting the caller.
    private static double Efficiency(TunePoint p) => p.StapmW > 0 ? p.Fps / p.StapmW : 0;

    // Lowest watts that still holds the target; tie -> highest fps (more headroom for free). No
    // target, or no point reaching it, is an honest "nothing to pick" rather than a fallback guess.
    private static TuneResult? PickHoldTarget(List<TunePoint> pts, int? targetFps)
    {
        if (targetFps is not int target) return null;

        var candidates = pts.Where(p => p.Fps >= target).ToList();
        if (candidates.Count == 0) return null;

        var best = candidates.OrderBy(p => p.StapmW).ThenByDescending(p => p.Fps).First();
        return new TuneResult(best.StapmW, best.Fps, best.TempC, $"Lowest watts holding ≥{target} fps.");
    }
}
