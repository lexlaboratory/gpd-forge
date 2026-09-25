// GPD Forge — frame-pacing metric tests (plan F2). GPL-3.0-or-later.
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class FramePacingTests
{
    private static double[] Repeat(double ms, int count) => Enumerable.Repeat(ms, count).ToArray();

    [Fact]
    public void Says_nothing_about_fewer_than_two_frames()
    {
        Assert.Null(FramePacing.Compute([]));
        Assert.Null(FramePacing.Compute([16.7]));
    }

    [Fact]
    public void Ignores_frames_that_are_not_durations()
    {
        // A zero, negative or NaN frame time is a PresentMon artefact, not a frame; counting it would
        // put an infinite FPS into the mean.
        var m = FramePacing.Compute([16.0, 0, -3, double.NaN, 16.0, 16.0])!;
        Assert.Equal(3, m.Frames);
        Assert.Equal(62.5, m.FpsAvg);
    }

    [Fact]
    public void Even_pacing_has_no_spread_and_no_stutter()
    {
        var m = FramePacing.Compute(Repeat(1000.0 / 60, 600))!;
        Assert.Equal(60, m.FpsAvg);
        Assert.Equal(60, m.Fps1PctLow);
        Assert.Equal(60, m.Fps01PctLow);
        Assert.Equal(0, m.FrameTimeStdDevMs);
        Assert.Equal(0, m.Stutters);
        Assert.Equal(0, m.StuttersPerMin);
        Assert.Equal(10, m.SpanSeconds);
    }

    [Fact]
    public void A_hundred_and_forty_four_fps_on_a_sixty_hertz_panel_is_not_stutter()
    {
        // The frames the game presents faster than the panel shows are torn or dropped by the display,
        // not delayed: at ~7 ms each, nothing comes near the 25 ms floor even with jitter.
        var times = Enumerable.Range(0, 1440).Select(i => i % 2 == 0 ? 6.0 : 7.8).ToArray();
        var m = FramePacing.Compute(times)!;
        Assert.Equal(0, m.Stutters);
        Assert.InRange(m.FpsAvg, 144, 145);
        Assert.InRange(m.FrameTimeStdDevMs, 0.85, 0.95);
    }

    [Fact]
    public void Counts_spikes_above_twice_the_median_and_the_floor_as_stutters()
    {
        var times = Repeat(1000.0 / 60, 600);
        times[100] = 50;
        times[300] = 60;
        times[500] = 45;
        var m = FramePacing.Compute(times)!;
        Assert.Equal(3, m.Stutters);
        // 597 frames at 16.67 ms + 155 ms ≈ 10.1 s → 3 stutters in 0.168 min.
        Assert.Equal(17.8, m.StuttersPerMin);
        Assert.True(m.FrameTimeStdDevMs > 0);
    }

    [Fact]
    public void A_spike_under_the_floor_is_not_a_stutter_even_when_it_doubles_the_median()
    {
        // At 125 FPS a 20 ms frame is 2.5x the median but still faster than a 50 FPS frame; nobody
        // feels that as a hitch.
        var times = Repeat(8.0, 500);
        times[250] = 20;
        Assert.Equal(0, FramePacing.Compute(times)!.Stutters);
    }

    [Fact]
    public void A_steady_low_frame_rate_is_not_a_stutter()
    {
        // 30 FPS is above the 25 ms floor on every frame; it is slow, not uneven.
        var m = FramePacing.Compute(Repeat(1000.0 / 30, 300))!;
        Assert.Equal(0, m.Stutters);
        Assert.Equal(30, m.FpsAvg);
    }

    [Fact]
    public void The_point_one_percent_low_isolates_the_single_worst_frame_of_a_thousand()
    {
        var times = Repeat(10.0, 1000);
        times[700] = 100;
        var m = FramePacing.Compute(times)!;
        Assert.Equal(10, m.Fps01PctLow);             // the worst 1 frame: 100 ms
        Assert.Equal(52.6, m.Fps1PctLow);            // the worst 10: (100 + 9 × 10) / 10 = 19 ms
        Assert.Equal(1000.0 / 19.0, 1000.0 / FrameWindow.OnePercentLowMs(times), 6);
    }

    [Fact]
    public void Rolling_median_follows_a_change_of_pace()
    {
        // A scene change from 60 to 30 FPS is not a run of stutters: the median moves with it.
        var times = Repeat(1000.0 / 60, 300).Concat(Repeat(1000.0 / 30, 300)).ToArray();
        Assert.Equal(0, FramePacing.Compute(times)!.Stutters);
    }

    [Fact]
    public void Latest_caps_the_series_to_the_newest_frames()
    {
        double[] times = [1, 2, 3, 4, 5];
        Assert.Equal([4.0, 5.0], FramePacing.Latest(times, 2));
        Assert.Equal(times, FramePacing.Latest(times, 10));
    }
}
