// GPD Forge — auto-tuner sweep-stepping tests (pure planner + stateful TunerState). GPL-3.0-or-later.
using GpdForge.Tuner;
using Xunit;

namespace GpdForge.Core.Tests;

public class TunerSweepPlannerTests
{
    [Fact]
    public void Steps_forward_by_stepW()
    {
        Assert.Equal(12, TunerSweepPlanner.NextStapmW(10, minW: 8, maxW: 30, stepW: 2));
    }

    [Fact]
    public void Returns_null_once_the_next_step_would_exceed_maxW()
    {
        Assert.Null(TunerSweepPlanner.NextStapmW(29, minW: 8, maxW: 30, stepW: 2));
    }

    [Fact]
    public void Lands_exactly_on_maxW_when_it_divides_evenly()
    {
        Assert.Equal(30, TunerSweepPlanner.NextStapmW(28, minW: 8, maxW: 30, stepW: 2));
    }

    [Fact]
    public void A_non_positive_step_falls_back_to_one_watt_instead_of_looping_forever()
    {
        Assert.Equal(11, TunerSweepPlanner.NextStapmW(10, minW: 8, maxW: 30, stepW: 0));
        Assert.Equal(11, TunerSweepPlanner.NextStapmW(10, minW: 8, maxW: 30, stepW: -5));
    }
}

public class TunerStateTests
{
    [Fact]
    public void Starts_idle_with_no_points_and_no_best()
    {
        var t = new TunerState();
        Assert.False(t.Running);
        Assert.Empty(t.Points);
        Assert.Null(t.Best);
        Assert.Null(t.Note);
    }

    [Fact]
    public void Start_arms_the_sweep_at_minW()
    {
        var t = new TunerState();
        t.Start(TuneGoal.MaxFps, targetFps: null, minW: 10, maxW: 20, tempCapC: 90);

        Assert.True(t.Running);
        Assert.Equal(10, t.CurrentStapmW);
        Assert.Equal(10, t.MinW);
        Assert.Equal(20, t.MaxW);
        Assert.Equal(90, t.TempCapC);
        Assert.Empty(t.Points);
    }

    [Fact]
    public void Start_normalizes_a_swapped_min_max()
    {
        var t = new TunerState();
        t.Start(TuneGoal.MaxFps, null, minW: 30, maxW: 10, tempCapC: 90);

        Assert.Equal(10, t.MinW);
        Assert.Equal(30, t.MaxW);
        Assert.Equal(10, t.CurrentStapmW);
    }

    [Fact]
    public void Start_clamps_bounds_into_the_safe_TDP_band()
    {
        var t = new TunerState();
        t.Start(TuneGoal.MaxFps, null, minW: 0, maxW: 999, tempCapC: 90);

        Assert.Equal(5, t.MinW);
        Assert.Equal(40, t.MaxW);
    }

    [Fact]
    public void Start_clears_points_and_note_from_a_previous_run()
    {
        var t = new TunerState();
        t.Start(TuneGoal.MaxFps, null, minW: 8, maxW: 8, tempCapC: 90); // single-step sweep
        for (int i = 0; i < TunerState.DwellTicks; i++) t.Tick(fps: 60, tempC: 70); // finishes, records one point

        Assert.False(t.Running);
        Assert.NotEmpty(t.Points);

        t.Start(TuneGoal.MaxFps, null, minW: 10, maxW: 30, tempCapC: 90);
        Assert.Empty(t.Points);
        Assert.Null(t.Note);
    }

    [Fact]
    public void Tick_before_dwell_completes_holds_the_current_stapm_and_records_nothing()
    {
        var t = new TunerState();
        t.Start(TuneGoal.MaxFps, null, minW: 8, maxW: 30, tempCapC: 90);

        for (int i = 0; i < TunerState.DwellTicks - 1; i++)
        {
            int? applied = t.Tick(fps: 60, tempC: 70);
            Assert.Equal(8, applied);
        }
        Assert.Empty(t.Points);
    }

    [Fact]
    public void Tick_records_a_point_and_steps_once_the_dwell_completes()
    {
        var t = new TunerState();
        t.Start(TuneGoal.MaxFps, null, minW: 8, maxW: 30, tempCapC: 90);

        for (int i = 0; i < TunerState.DwellTicks - 1; i++) t.Tick(60, 70);
        int? next = t.Tick(60, 70); // dwell completes here

        var only = Assert.Single(t.Points);
        Assert.Equal(new TunePoint(8, 60, 70), only);
        Assert.Equal(8 + TunerState.StepW, next);
        Assert.Equal(next, t.CurrentStapmW);
        Assert.True(t.Running); // more steps remain up to maxW=30
    }

    [Fact]
    public void Tick_never_records_a_zero_fps_reading_the_honesty_gate()
    {
        var t = new TunerState();
        t.Start(TuneGoal.MaxFps, null, minW: 8, maxW: 10, tempCapC: 90); // two steps: 8, 10

        // Drive the whole sweep with fps=0, exactly what this HX370 reports today (no PresentMon).
        while (t.Running) t.Tick(fps: 0, tempC: 70);

        Assert.Empty(t.Points);
        Assert.Null(t.Best);
    }

    [Fact]
    public void Sweep_finishing_with_no_points_sets_an_honest_note()
    {
        var t = new TunerState();
        t.Start(TuneGoal.MaxFps, null, minW: 8, maxW: 8, tempCapC: 90); // single step
        for (int i = 0; i < TunerState.DwellTicks; i++) t.Tick(fps: 0, tempC: 70);

        Assert.False(t.Running);
        Assert.False(string.IsNullOrWhiteSpace(t.Note));
    }

    [Fact]
    public void Sweep_finishing_with_points_leaves_the_note_null()
    {
        var t = new TunerState();
        t.Start(TuneGoal.MaxFps, null, minW: 8, maxW: 8, tempCapC: 90);
        for (int i = 0; i < TunerState.DwellTicks; i++) t.Tick(fps: 60, tempC: 70);

        Assert.False(t.Running);
        Assert.Null(t.Note);
        Assert.NotNull(t.Best);
    }

    [Fact]
    public void Sweep_runs_to_completion_across_every_step_up_to_maxW()
    {
        var t = new TunerState();
        t.Start(TuneGoal.MaxFps, null, minW: 8, maxW: 12, tempCapC: 90); // steps: 8, 10, 12 (StepW=2)

        int ticks = 0;
        while (t.Running && ticks < 1000)
        {
            t.Tick(fps: 50 + t.CurrentStapmW, tempC: 70); // monotonic model: more watts -> more fps
            ticks++;
        }

        Assert.False(t.Running);
        Assert.Equal(3, t.Points.Count);
        Assert.Equal(new[] { 8, 10, 12 }, t.Points.Select(p => p.StapmW));
        Assert.Equal(12, t.Best!.Value.StapmW); // MaxFps -> the highest-watt step won here
    }

    [Fact]
    public void Tick_is_a_noop_once_not_running()
    {
        var t = new TunerState();
        Assert.Null(t.Tick(fps: 60, tempC: 70));
        Assert.Empty(t.Points);
    }

    [Fact]
    public void CurrentProfile_is_flat_with_the_temp_cap_as_the_thermal_limit()
    {
        var t = new TunerState();
        t.Start(TuneGoal.MaxFps, null, minW: 15, maxW: 30, tempCapC: 88);

        var p = t.CurrentProfile();
        Assert.Equal(15, p.StapmW);
        Assert.Equal(15, p.FastW);
        Assert.Equal(15, p.SlowW);
        Assert.Equal(88, p.TctlC);
    }

    [Fact]
    public void HoldTarget_with_no_target_set_never_produces_a_best_even_with_points()
    {
        var t = new TunerState();
        t.Start(TuneGoal.HoldTarget, targetFps: null, minW: 8, maxW: 8, tempCapC: 90);
        for (int i = 0; i < TunerState.DwellTicks; i++) t.Tick(fps: 60, tempC: 70);

        Assert.NotEmpty(t.Points);
        Assert.Null(t.Best);
    }
}
