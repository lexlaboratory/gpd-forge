// GPD Forge — auto-tuner pick logic tests (pure). GPL-3.0-or-later.
using GpdForge.Tuner;
using Xunit;

namespace GpdForge.Core.Tests;

public class AutoTunerTests
{
    private const int Cap = 96;

    // --- empty / degenerate input --------------------------------------------------------------

    [Fact]
    public void Null_points_returns_null_for_every_goal()
    {
        Assert.Null(AutoTuner.PickBest(null!, TuneGoal.MaxFps, null, Cap));
        Assert.Null(AutoTuner.PickBest(null!, TuneGoal.BestEfficiency, null, Cap));
        Assert.Null(AutoTuner.PickBest(null!, TuneGoal.HoldTarget, 60, Cap));
    }

    [Fact]
    public void Empty_points_returns_null_for_every_goal()
    {
        var empty = Array.Empty<TunePoint>();
        Assert.Null(AutoTuner.PickBest(empty, TuneGoal.MaxFps, null, Cap));
        Assert.Null(AutoTuner.PickBest(empty, TuneGoal.BestEfficiency, null, Cap));
        Assert.Null(AutoTuner.PickBest(empty, TuneGoal.HoldTarget, 60, Cap));
    }

    // --- temp-cap exclusion ---------------------------------------------------------------------

    [Fact]
    public void All_points_above_the_temp_cap_returns_null()
    {
        var points = new[] { new TunePoint(10, 60, 99), new TunePoint(20, 80, 100) };
        Assert.Null(AutoTuner.PickBest(points, TuneGoal.MaxFps, null, tempCapC: 96));
    }

    [Fact]
    public void A_point_exactly_at_the_cap_is_included_inclusive_boundary()
    {
        var points = new[] { new TunePoint(20, 80, 96) };
        var best = AutoTuner.PickBest(points, TuneGoal.MaxFps, null, tempCapC: 96);
        Assert.NotNull(best);
        Assert.Equal(20, best!.Value.StapmW);
    }

    [Fact]
    public void Hottest_point_is_excluded_even_though_it_has_the_highest_fps()
    {
        var points = new[]
        {
            new TunePoint(10, 50, 70),
            new TunePoint(30, 99, 99), // best fps, but over the cap
        };
        var best = AutoTuner.PickBest(points, TuneGoal.MaxFps, null, tempCapC: 90);
        Assert.Equal(10, best!.Value.StapmW);
        Assert.Equal(50, best.Value.Fps);
    }

    // --- MaxFps ----------------------------------------------------------------------------------

    [Fact]
    public void MaxFps_picks_the_highest_fps_point()
    {
        var points = new[] { new TunePoint(10, 40, 70), new TunePoint(20, 65, 80), new TunePoint(30, 50, 90) };
        var best = AutoTuner.PickBest(points, TuneGoal.MaxFps, null, Cap);
        Assert.Equal(20, best!.Value.StapmW);
        Assert.Equal(65, best.Value.Fps);
    }

    [Fact]
    public void MaxFps_tie_break_prefers_the_lowest_watts()
    {
        var points = new[] { new TunePoint(25, 60, 80), new TunePoint(15, 60, 75), new TunePoint(20, 60, 78) };
        var best = AutoTuner.PickBest(points, TuneGoal.MaxFps, null, Cap);
        Assert.Equal(15, best!.Value.StapmW); // all tied at 60 fps -> least power wins
    }

    [Fact]
    public void MaxFps_note_is_a_non_empty_explanation()
    {
        var points = new[] { new TunePoint(20, 60, 80) };
        var best = AutoTuner.PickBest(points, TuneGoal.MaxFps, null, Cap);
        Assert.False(string.IsNullOrWhiteSpace(best!.Value.Note));
    }

    // --- BestEfficiency ----------------------------------------------------------------------------

    [Fact]
    public void BestEfficiency_picks_the_highest_fps_per_watt()
    {
        // 10W->40fps = 4.0 fps/W ; 20W->65fps = 3.25 fps/W ; 8W->30fps = 3.75 fps/W
        var points = new[] { new TunePoint(10, 40, 70), new TunePoint(20, 65, 80), new TunePoint(8, 30, 65) };
        var best = AutoTuner.PickBest(points, TuneGoal.BestEfficiency, null, Cap);
        Assert.Equal(10, best!.Value.StapmW);
    }

    [Fact]
    public void BestEfficiency_tie_break_prefers_the_lowest_watts()
    {
        // Both points are exactly 3.0 fps/W: 10W->30fps and 20W->60fps.
        var points = new[] { new TunePoint(20, 60, 80), new TunePoint(10, 30, 70) };
        var best = AutoTuner.PickBest(points, TuneGoal.BestEfficiency, null, Cap);
        Assert.Equal(10, best!.Value.StapmW);
    }

    [Fact]
    public void BestEfficiency_fully_tied_points_still_resolve_deterministically()
    {
        // Same watts and fps (e.g. a re-dwell reading) — same efficiency by construction, so there is
        // nothing left to break the tie on; the pick must still be stable across repeated calls
        // rather than throwing or alternating.
        var points = new[] { new TunePoint(10, 30, 70), new TunePoint(10, 30, 71) };
        var a = AutoTuner.PickBest(points, TuneGoal.BestEfficiency, null, Cap);
        var b = AutoTuner.PickBest(points, TuneGoal.BestEfficiency, null, Cap);
        Assert.NotNull(a);
        Assert.Equal(a, b);
    }

    // --- HoldTarget --------------------------------------------------------------------------------

    [Fact]
    public void HoldTarget_picks_the_lowest_watts_meeting_the_target()
    {
        var points = new[] { new TunePoint(10, 40, 70), new TunePoint(20, 65, 80), new TunePoint(30, 90, 90) };
        var best = AutoTuner.PickBest(points, TuneGoal.HoldTarget, targetFps: 60, Cap);
        Assert.Equal(20, best!.Value.StapmW);
        Assert.Equal(65, best.Value.Fps);
    }

    [Fact]
    public void HoldTarget_tie_break_prefers_the_highest_fps_among_equal_watts()
    {
        var points = new[] { new TunePoint(20, 61, 80), new TunePoint(20, 70, 82) };
        var best = AutoTuner.PickBest(points, TuneGoal.HoldTarget, targetFps: 60, Cap);
        Assert.Equal(70, best!.Value.Fps);
    }

    [Fact]
    public void HoldTarget_exact_match_counts_as_meeting_the_target_inclusive_boundary()
    {
        var points = new[] { new TunePoint(20, 60, 80) };
        var best = AutoTuner.PickBest(points, TuneGoal.HoldTarget, targetFps: 60, Cap);
        Assert.NotNull(best);
    }

    [Fact]
    public void HoldTarget_unreachable_within_the_points_returns_null()
    {
        var points = new[] { new TunePoint(10, 40, 70), new TunePoint(20, 55, 80) };
        Assert.Null(AutoTuner.PickBest(points, TuneGoal.HoldTarget, targetFps: 60, Cap));
    }

    [Fact]
    public void HoldTarget_without_a_target_returns_null()
    {
        var points = new[] { new TunePoint(10, 40, 70), new TunePoint(20, 90, 80) };
        Assert.Null(AutoTuner.PickBest(points, TuneGoal.HoldTarget, targetFps: null, Cap));
    }

    [Fact]
    public void HoldTarget_respects_the_temp_cap_before_the_target()
    {
        // Only the over-cap point meets the fps target -> still unreachable within the cap.
        var points = new[] { new TunePoint(10, 40, 70), new TunePoint(30, 90, 100) };
        Assert.Null(AutoTuner.PickBest(points, TuneGoal.HoldTarget, targetFps: 60, tempCapC: 90));
    }

    // --- determinism -------------------------------------------------------------------------------

    [Fact]
    public void Is_deterministic_for_identical_inputs()
    {
        var points = new[] { new TunePoint(10, 40, 70), new TunePoint(20, 65, 80), new TunePoint(30, 50, 90) };
        var a = AutoTuner.PickBest(points, TuneGoal.BestEfficiency, null, Cap);
        var b = AutoTuner.PickBest(points, TuneGoal.BestEfficiency, null, Cap);
        Assert.Equal(a, b);
    }

    // --- single point --------------------------------------------------------------------------

    [Fact]
    public void Single_point_is_its_own_best_for_every_goal()
    {
        var points = new[] { new TunePoint(15, 55, 75) };
        Assert.Equal(15, AutoTuner.PickBest(points, TuneGoal.MaxFps, null, Cap)!.Value.StapmW);
        Assert.Equal(15, AutoTuner.PickBest(points, TuneGoal.BestEfficiency, null, Cap)!.Value.StapmW);
        Assert.Equal(15, AutoTuner.PickBest(points, TuneGoal.HoldTarget, 50, Cap)!.Value.StapmW);
    }
}
