// GPD Forge — moving-average tests for the fan curve's temperature input. GPL-3.0-or-later.
using GpdForge.Fan;
using Xunit;

namespace GpdForge.Core.Tests;

public class TempSmootherTests
{
    [Fact]
    public void First_reading_is_returned_as_is()
    {
        var s = new TempSmoother();
        Assert.Equal(70, s.Add(70));
    }

    [Fact]
    public void Averages_while_the_window_is_still_filling()
    {
        var s = new TempSmoother(window: 4);
        Assert.Equal(70, s.Add(70));
        Assert.Equal(75, s.Add(80));           // (70+80)/2
        Assert.Equal(80, s.Add(90));           // (70+80+90)/3
    }

    [Fact]
    public void Drops_the_oldest_reading_once_the_window_is_full()
    {
        var s = new TempSmoother(window: 3);
        s.Add(60); s.Add(60); s.Add(60);
        // Window now full at 60,60,60. A fourth reading pushes the first 60 out.
        Assert.Equal((60.0 + 60 + 90) / 3, s.Add(90));
    }

    [Fact]
    public void Smooths_out_a_single_tick_noise_spike()
    {
        // The exact shape reported live: a steady-ish ~70°C load with one noisy tick at 90°C.
        var s = new TempSmoother(window: 4);
        s.Add(70); s.Add(71); s.Add(69);
        double smoothed = s.Add(90);
        Assert.True(smoothed < 90, $"a single spike should be damped by the average, got {smoothed}");
        Assert.Equal((70.0 + 71 + 69 + 90) / 4, smoothed);
    }

    [Fact]
    public void A_sustained_rise_is_still_fully_reflected_within_one_window()
    {
        var s = new TempSmoother(window: 4);
        for (int i = 0; i < 4; i++) s.Add(85);
        Assert.Equal(85, s.Add(85));
    }

    [Fact]
    public void Window_is_clamped_to_at_least_one()
    {
        var s = new TempSmoother(window: 0);
        s.Add(50);
        Assert.Equal(80, s.Add(80)); // window of 1 -> no averaging, just the latest reading
    }

    [Fact]
    public void Reset_clears_history_so_the_next_reading_is_adopted_immediately()
    {
        var s = new TempSmoother(window: 4);
        s.Add(90); s.Add(90); s.Add(90);
        s.Reset();
        Assert.Equal(40, s.Add(40));
    }
}
