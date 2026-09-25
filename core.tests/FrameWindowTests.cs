// GPD Forge — frame-window aggregation tests. GPL-3.0-or-later.
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class FrameWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TwoSeconds = TimeSpan.FromSeconds(2);

    [Fact]
    public void Reports_nothing_when_empty()
    {
        var w = new FrameWindow(TwoSeconds);
        Assert.False(w.TryAggregate(T0, "game.exe", TwoSeconds, out _));
        Assert.Empty(w.Activity(T0, TwoSeconds));
    }

    [Fact]
    public void Reports_nothing_on_a_single_frame()
    {
        var w = new FrameWindow(TwoSeconds);
        w.Add("game.exe", 16.67, T0);
        Assert.False(w.TryAggregate(T0, "game.exe", TwoSeconds, out _));
    }

    [Fact]
    public void Averages_a_steady_60fps_stream()
    {
        var w = new FrameWindow(TwoSeconds);
        for (int i = 0; i < 100; i++) w.Add("game.exe", 16.67, T0.AddMilliseconds(i * 16.67));

        Assert.True(w.TryAggregate(T0.AddSeconds(1), "game.exe", TwoSeconds, out var s));
        Assert.Equal(60.0, s.Fps, 1);
        Assert.Equal("game.exe", s.Process);
    }

    [Fact]
    public void One_percent_low_tracks_the_worst_frames_not_the_mean()
    {
        // 99 good frames at 60fps + one 100ms stall: the mean barely moves, the 1% low collapses.
        var w = new FrameWindow(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 99; i++) w.Add("game.exe", 16.67, T0.AddMilliseconds(i * 16.67));
        w.Add("game.exe", 100.0, T0.AddMilliseconds(99 * 16.67));

        Assert.True(w.TryAggregate(T0.AddSeconds(2), "game.exe", TimeSpan.FromSeconds(5), out var s));
        Assert.True(s.Fps > 50, $"mean should stay high, was {s.Fps}");
        Assert.Equal(10.0, s.Fps1PctLow, 1); // 1000 / 100ms
        Assert.True(s.Fps1PctLow < s.Fps);
    }

    [Fact]
    public void One_percent_low_falls_back_to_the_worst_frame_on_small_samples()
    {
        Assert.Equal(20.0, FrameWindow.OnePercentLowMs([10.0, 20.0, 15.0]), 3);
    }

    [Fact]
    public void One_percent_low_of_nothing_is_zero()
    {
        Assert.Equal(0.0, FrameWindow.OnePercentLowMs([]));
    }

    [Fact]
    public void Evicts_frames_older_than_the_window()
    {
        var w = new FrameWindow(TwoSeconds);
        for (int i = 0; i < 10; i++) w.Add("game.exe", 16.67, T0.AddMilliseconds(i * 10));

        // ...and nothing since. Ten seconds later the window is empty: the game stopped rendering,
        // which must read as "no FPS", not as a stale 60.
        Assert.False(w.TryAggregate(T0.AddSeconds(10), "game.exe", TwoSeconds, out _));
    }

    [Fact]
    public void Aggregates_only_the_named_application()
    {
        // dwm presents more often than the game; neither its count nor its faster frames may leak
        // into the game's reading. Which app to name is FrameTarget's decision, not the window's.
        var w = new FrameWindow(TwoSeconds);
        for (int i = 0; i < 50; i++) w.Add("game.exe", 16.67, T0.AddMilliseconds(i * 16.67));
        for (int i = 0; i < 120; i++) w.Add("dwm.exe", 8.0, T0.AddMilliseconds(i * 8));

        Assert.True(w.TryAggregate(T0.AddSeconds(1), "game.exe", TwoSeconds, out var s));
        Assert.Equal("game.exe", s.Process);
        Assert.Equal(60.0, s.Fps, 1);
    }

    [Fact]
    public void A_shorter_span_reads_only_the_recent_part_of_a_longer_window()
    {
        // The probe keeps 10 s for the frame-time buffer but reports FPS over the last 2 s: a game
        // that dropped from 120 to 30 fps 3 s ago must read 30 now, not the 10 s blend.
        var w = new FrameWindow(TimeSpan.FromSeconds(10));
        var t = T0;
        for (int i = 0; i < 960; i++) { t = t.AddMilliseconds(1000.0 / 120); w.Add("game.exe", 1000.0 / 120, t); }
        for (int i = 0; i < 90; i++) { t = t.AddMilliseconds(1000.0 / 30); w.Add("game.exe", 1000.0 / 30, t); }

        Assert.True(w.TryAggregate(t, "game.exe", TwoSeconds, out var s));
        Assert.Equal(30.0, s.Fps, 1);
    }

    [Fact]
    public void Activity_counts_frames_and_the_latest_frame_per_app()
    {
        var w = new FrameWindow(TwoSeconds);
        w.Add("launcher.exe", 500, T0);
        w.Add("launcher.exe", 500, T0.AddMilliseconds(500));
        w.Add("game.exe", 7, T0.AddMilliseconds(900));

        var activity = w.Activity(T0.AddSeconds(1), TwoSeconds).ToDictionary(a => a.Application);
        Assert.Equal(2, activity["launcher.exe"].Frames);
        Assert.Equal(T0.AddMilliseconds(500), activity["launcher.exe"].LastFrameAt);
        Assert.Equal(1, activity["game.exe"].Frames);
    }

    [Fact]
    public void Frame_times_come_back_in_time_order_even_when_they_arrived_out_of_it()
    {
        // The real 2.5.1 capture emits some rows out of time order; the buffer F2 builds on must be
        // chronological or a "stutter" would be detected where there was none.
        var w = new FrameWindow(TimeSpan.FromSeconds(10));
        w.Add("game.exe", 3, T0.AddMilliseconds(30));
        w.Add("game.exe", 1, T0.AddMilliseconds(10));
        w.Add("game.exe", 2, T0.AddMilliseconds(20));

        Assert.Equal([1.0, 2.0, 3.0], w.FrameTimes(T0.AddSeconds(1), "game.exe", TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Frames_behind_the_span_are_excluded_even_when_out_of_order()
    {
        // An old row behind a newer head is not evicted by the queue, so the span filter must catch it.
        var w = new FrameWindow(TimeSpan.FromSeconds(10));
        w.Add("game.exe", 5, T0.AddSeconds(9));
        w.Add("game.exe", 99, T0);             // 9 s older than the head

        Assert.Equal([5.0], w.FrameTimes(T0.AddSeconds(9.5), "game.exe", TwoSeconds));
    }

    [Fact]
    public void Capacity_bounds_the_buffer()
    {
        var w = new FrameWindow(TimeSpan.FromSeconds(10), capacity: 100);
        for (int i = 0; i < 500; i++) w.Add("game.exe", 1, T0.AddMilliseconds(i));

        Assert.Equal(100, w.FrameTimes(T0.AddSeconds(1), "game.exe", TimeSpan.FromSeconds(10)).Length);
    }
}
