// GPD Forge — slew limit + dwell for fan duty. GPL-3.0-or-later.
using GpdForge.Fan;
using Xunit;

namespace GpdForge.Core.Tests;

public class FanDutyRampTests
{
    [Fact]
    public void Cold_start_adopts_the_target_immediately()
    {
        var r = new FanDutyRamp();
        Assert.Equal(150, r.Step(150, dtSeconds: 1));
    }

    [Fact]
    public void A_rise_is_limited_to_the_up_rate()
    {
        var r = new FanDutyRamp();
        r.Step(100, 1);
        Assert.Equal(100 + FanDutyRamp.DefaultUpPerSecond, r.Step(255, 1));
    }

    [Fact]
    public void A_rise_smaller_than_the_limit_lands_exactly()
    {
        var r = new FanDutyRamp();
        r.Step(100, 1);
        Assert.Equal(110, r.Step(110, 1));
    }

    [Fact]
    public void A_drop_waits_out_the_hold_after_a_rise()
    {
        var r = new FanDutyRamp();
        r.Step(100, 1);
        r.Step(120, 1);                                     // rise: starts the hold
        for (int i = 0; i < (int)FanDutyRamp.DefaultHoldSeconds - 1; i++)
            Assert.Equal(120, r.Step(60, 1));               // still holding
    }

    [Fact]
    public void After_the_hold_a_drop_is_limited_to_the_down_rate()
    {
        var r = new FanDutyRamp();
        r.Step(200, 1);                                     // cold start is not a rise: no hold
        Assert.Equal(200 - FanDutyRamp.DefaultDownPerSecond, r.Step(60, 1));
        Assert.Equal(200 - 2 * FanDutyRamp.DefaultDownPerSecond, r.Step(60, 1));
    }

    [Fact]
    public void Drops_are_slower_than_rises()
    {
        Assert.True(FanDutyRamp.DefaultDownPerSecond < FanDutyRamp.DefaultUpPerSecond);
    }

    [Fact]
    public void Rate_scales_with_elapsed_time()
    {
        var r = new FanDutyRamp();
        r.Step(100, 1);
        Assert.Equal(100 + 3 * FanDutyRamp.DefaultUpPerSecond, r.Step(255, 3));
    }

    [Fact]
    public void Non_positive_elapsed_time_holds_the_current_duty()
    {
        var r = new FanDutyRamp();
        r.Step(100, 1);
        Assert.Equal(100, r.Step(255, 0));
    }

    [Fact]
    public void Output_stays_within_0_to_255()
    {
        var r = new FanDutyRamp();
        Assert.Equal(255, r.Step(400, 1));
        var s = new FanDutyRamp();
        Assert.Equal(0, s.Step(-20, 1));
    }

    [Fact]
    public void Reset_makes_the_next_step_a_cold_start()
    {
        var r = new FanDutyRamp();
        r.Step(200, 1);
        r.Reset();
        Assert.Equal(60, r.Step(60, 1));
    }

    [Fact]
    public void A_steady_wobbling_target_produces_a_steady_duty()
    {
        // The live complaint: the curve target flickers ±1 EC step every few seconds. With the
        // hold, a target that wobbles around a level must not make the output hunt downward.
        var r = new FanDutyRamp();
        r.Step(150, 1);
        int[] wobble = [155, 150, 155, 150, 155, 150, 155, 150];
        int last = 0;
        foreach (var t in wobble) last = r.Step(t, 1);
        Assert.Equal(155, last);
    }
}
