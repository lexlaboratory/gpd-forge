// GPD Forge — time-based smoothing tests for the fan curve's temperature input. GPL-3.0-or-later.
using GpdForge.Fan;
using Xunit;

namespace GpdForge.Core.Tests;

public class TempSmootherTests
{
    [Fact]
    public void First_reading_is_returned_as_is()
    {
        var s = new TempSmoother();
        Assert.Equal(70, s.Add(70, dtSeconds: 1));
    }

    [Fact]
    public void Smooths_out_a_single_tick_noise_spike()
    {
        // The shape measured live on 2026-09-24: Tctl sitting around 70-75°C with one tick at 86°C.
        var s = new TempSmoother();
        for (int i = 0; i < 20; i++) s.Add(72, 1);
        double smoothed = s.Add(86, 1);
        Assert.InRange(smoothed, 72, 78);
    }

    [Fact]
    public void A_sustained_rise_is_reflected_within_a_few_rise_time_constants()
    {
        var s = new TempSmoother();
        for (int i = 0; i < 20; i++) s.Add(60, 1);
        double t = 0;
        for (int i = 0; i < 4 * (int)TempSmoother.DefaultRiseTauSeconds; i++) t = s.Add(85, 1);
        Assert.True(t > 84, $"a sustained 25°C rise should be ~fully tracked after 4τ, got {t}");
    }

    [Fact]
    public void Cooling_is_followed_more_slowly_than_heating()
    {
        var up = new TempSmoother();
        var down = new TempSmoother();
        for (int i = 0; i < 30; i++) { up.Add(60, 1); down.Add(80, 1); }
        double afterRise = up.Add(80, 3) - 60;     // how far it moved toward +20
        double afterFall = 80 - down.Add(60, 3);   // how far it moved toward -20
        Assert.True(afterRise > afterFall, $"rise moved {afterRise}, fall moved {afterFall}");
    }

    [Fact]
    public void A_long_tick_moves_further_than_a_short_one()
    {
        // The whole point of weighting by elapsed time: a loop stalled behind a slow ryzenadj apply
        // must not turn one stale sample into several seconds' worth of noise, or the reverse.
        var shortTick = new TempSmoother();
        var longTick = new TempSmoother();
        shortTick.Add(60, 1); longTick.Add(60, 1);
        Assert.True(longTick.Add(80, 8) > shortTick.Add(80, 1));
    }

    [Fact]
    public void Non_positive_elapsed_time_does_not_move_the_average()
    {
        var s = new TempSmoother();
        s.Add(60, 1);
        Assert.Equal(60, s.Add(90, 0));
        Assert.Equal(60, s.Add(90, -5));
    }

    [Fact]
    public void Reset_clears_history_so_the_next_reading_is_adopted_immediately()
    {
        var s = new TempSmoother();
        s.Add(90, 1); s.Add(90, 1);
        s.Reset();
        Assert.Equal(40, s.Add(40, 1));
    }
}
