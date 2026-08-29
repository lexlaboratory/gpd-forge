// GPD Forge — session tracker (open/close state machine + aggregation). GPL-3.0-or-later.
using GpdForge.Sessions;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class SessionTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static SessionTick Frame(int second, string app, double fps, double low = 0, double temp = 70, double watts = 20)
        => new(T0.AddSeconds(second), app, fps, low > 0 ? low : fps, temp, watts, 80, AcConnected: true);

    private static SessionTick Idle(int second)
        => new(T0.AddSeconds(second), null, null, null, 45, 5, 80, AcConnected: true);

    [Fact]
    public void Nothing_presenting_never_opens_a_session()
    {
        var tracker = new SessionTracker();
        for (int i = 0; i < 600; i++) Assert.Null(tracker.Observe(Idle(i)));
        Assert.False(tracker.HasOpenSession);
        Assert.Null(tracker.Flush(T0.AddHours(1)));
    }

    [Fact]
    public void A_tick_with_an_app_but_no_fps_reading_never_opens_a_session()
    {
        // PresentMon blocked / gate closed: no reading at all. Inventing a zero-fps session here
        // would be exactly the dishonesty this feature must not commit.
        var tracker = new SessionTracker();
        for (int i = 0; i < 300; i++)
            Assert.Null(tracker.Observe(new SessionTick(T0.AddSeconds(i), "game.exe", null, null, 70, 20, 80, true)));
        Assert.False(tracker.HasOpenSession);
    }

    [Fact]
    public void Session_closes_after_the_idle_timeout_and_aggregates()
    {
        var tracker = new SessionTracker();
        for (int i = 0; i < 300; i++) Assert.Null(tracker.Observe(Frame(i, "game.exe", 60)));
        Assert.True(tracker.HasOpenSession);

        GameSession? closed = null;
        for (int i = 300; i <= 400 && closed is null; i++) closed = tracker.Observe(Idle(i));

        Assert.NotNull(closed);
        Assert.Equal("game.exe", closed!.App);
        Assert.Equal(60, closed.FpsAvg);
        Assert.Equal(299, closed.DurationSeconds);
        Assert.False(tracker.HasOpenSession);
    }

    [Fact]
    public void Idle_shorter_than_the_timeout_does_not_split_the_session()
    {
        var tracker = new SessionTracker();
        for (int i = 0; i < 120; i++) tracker.Observe(Frame(i, "game.exe", 60));
        for (int i = 120; i < 150; i++) Assert.Null(tracker.Observe(Idle(i))); // 30 s loading screen
        for (int i = 150; i < 300; i++) Assert.Null(tracker.Observe(Frame(i, "game.exe", 60)));

        Assert.True(tracker.HasOpenSession);
        var closed = tracker.Flush(T0.AddSeconds(300));
        Assert.NotNull(closed);
        Assert.Equal(299, closed!.DurationSeconds);
    }

    [Fact]
    public void Switching_app_closes_the_first_session_and_opens_the_next()
    {
        var tracker = new SessionTracker();
        for (int i = 0; i < 200; i++) tracker.Observe(Frame(i, "a.exe", 60));
        var closed = tracker.Observe(Frame(200, "b.exe", 30));

        Assert.NotNull(closed);
        Assert.Equal("a.exe", closed!.App);
        Assert.True(tracker.HasOpenSession);
        Assert.Equal("b.exe", tracker.CurrentApp);
    }

    [Fact]
    public void Sessions_shorter_than_the_minimum_are_discarded()
    {
        var tracker = new SessionTracker();
        for (int i = 0; i < 10; i++) tracker.Observe(Frame(i, "launcher.exe", 60)); // splash screen
        Assert.Null(tracker.Flush(T0.AddSeconds(10)));
    }

    [Fact]
    public void One_percent_low_is_the_worst_of_the_window_not_the_mean()
    {
        var tracker = new SessionTracker();
        for (int i = 0; i < 99; i++) tracker.Observe(Frame(i, "game.exe", 60, low: 58));
        tracker.Observe(Frame(99, "game.exe", 60, low: 12)); // one hitch

        var closed = tracker.Flush(T0.AddSeconds(100));
        Assert.NotNull(closed);
        Assert.Equal(12, closed!.Fps1PctLow);
        Assert.True(closed.FpsAvg > 59);
    }

    [Fact]
    public void Ticks_without_an_fps_reading_are_counted_not_averaged_as_zero()
    {
        var tracker = new SessionTracker();
        for (int i = 0; i < 100; i++) tracker.Observe(Frame(i, "game.exe", 60));
        // Same app still presenting, but the probe produced no aggregate this second.
        for (int i = 100; i < 140; i++)
            tracker.Observe(new SessionTick(T0.AddSeconds(i), "game.exe", null, null, 70, 20, 80, true));
        for (int i = 140; i < 200; i++) tracker.Observe(Frame(i, "game.exe", 60));

        var closed = tracker.Flush(T0.AddSeconds(200));
        Assert.NotNull(closed);
        Assert.Equal(60, closed!.FpsAvg); // the gap did not drag the mean down
        Assert.Equal(40, closed.SamplesWithoutFps);
        Assert.Equal(200, closed.Samples);
    }

    [Fact]
    public void Unavailable_sensors_are_reported_as_null_never_as_zero()
    {
        var tracker = new SessionTracker();
        for (int i = 0; i < 200; i++)
            tracker.Observe(new SessionTick(T0.AddSeconds(i), "game.exe", 45, 30, null, null, null, false));

        var closed = tracker.Flush(T0.AddSeconds(200));
        Assert.NotNull(closed);
        Assert.Null(closed!.CpuTempAvgC);
        Assert.Null(closed.CpuTempMaxC);
        Assert.Null(closed.PackageAvgW);
        Assert.Null(closed.BatteryUsedPct);
    }

    [Fact]
    public void Battery_drain_is_recorded_only_when_the_whole_session_was_on_battery()
    {
        var onBattery = new SessionTracker();
        for (int i = 0; i < 200; i++)
            onBattery.Observe(new SessionTick(T0.AddSeconds(i), "game.exe", 45, 40, 70, 18, i < 100 ? 90 : 85, false));
        var drained = onBattery.Flush(T0.AddSeconds(200));
        Assert.NotNull(drained);
        Assert.True(drained!.OnBattery);
        Assert.Equal(90, drained.BatteryStartPct);
        Assert.Equal(85, drained.BatteryEndPct);
        Assert.Equal(5, drained.BatteryUsedPct);

        var pluggedIn = new SessionTracker();
        for (int i = 0; i < 200; i++)
            pluggedIn.Observe(new SessionTick(T0.AddSeconds(i), "game.exe", 45, 40, 70, 18, 90, i > 100));
        var mixed = pluggedIn.Flush(T0.AddSeconds(200));
        Assert.NotNull(mixed);
        Assert.False(mixed!.OnBattery);
        Assert.Null(mixed.BatteryUsedPct);
    }

    [Fact]
    public void Temperature_reports_mean_and_peak()
    {
        var tracker = new SessionTracker();
        for (int i = 0; i < 200; i++) tracker.Observe(Frame(i, "game.exe", 60, temp: i < 100 ? 60 : 80));
        var closed = tracker.Flush(T0.AddSeconds(200));
        Assert.NotNull(closed);
        Assert.Equal(70, closed!.CpuTempAvgC);
        Assert.Equal(80, closed.CpuTempMaxC);
    }

    [Fact]
    public void Trend_series_is_bounded_and_ordered_oldest_first()
    {
        var tracker = new SessionTracker();
        for (int i = 0; i < 4000; i++) tracker.Observe(Frame(i, "game.exe", 30 + (i % 30)));
        var closed = tracker.Flush(T0.AddSeconds(4000));
        Assert.NotNull(closed);
        Assert.InRange(closed!.FpsTrend.Count, 1, SessionPolicy.Default.TrendPoints);
    }

    [Fact]
    public void A_clock_that_jumps_backwards_does_not_produce_a_negative_duration()
    {
        var tracker = new SessionTracker();
        for (int i = 0; i < 200; i++) tracker.Observe(Frame(i, "game.exe", 60));
        var closed = tracker.Flush(T0.AddSeconds(-500)); // NTP correction / resume from standby
        Assert.NotNull(closed);
        Assert.True(closed!.DurationSeconds >= 0);
    }

    [Fact]
    public void Rejects_a_nonsensical_policy()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SessionTracker(SessionPolicy.Default with { IdleTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SessionTracker(SessionPolicy.Default with { TrendPoints = 0 }));
    }

    [Fact]
    public void From_snapshot_maps_missing_readings_to_null()
    {
        var empty = new TelemetrySnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, false);
        var tick = SessionTick.From(empty, null, T0);
        Assert.Null(tick.App);
        Assert.Null(tick.Fps);
        Assert.Null(tick.CpuTempC);
        Assert.Null(tick.PackageW);
        Assert.Null(tick.BatteryPct);

        var live = new TelemetrySnapshot(72.5, 68, 18.2, 3400, 4200, 0, 59.7, 41.2, 88, 12.5, false, true);
        var withFps = SessionTick.From(live, new FpsSample(59.7, 41.2, "game.exe"), T0);
        Assert.Equal("game.exe", withFps.App);
        Assert.Equal(59.7, withFps.Fps);
        Assert.Equal(41.2, withFps.Fps1PctLow);
        Assert.Equal(72.5, withFps.CpuTempC);
        Assert.Equal(88, withFps.BatteryPct);
    }

    [Fact]
    public void From_snapshot_ignores_a_sample_with_no_process_name()
    {
        // A frame-rate reading we cannot attribute to an app cannot open a session for "unknown".
        var live = new TelemetrySnapshot(70, 0, 15, 3000, 0, 0, 60, 50, 90, 0, true, true);
        var tick = SessionTick.From(live, new FpsSample(60, 50, null), T0);
        Assert.Null(tick.App);
        Assert.Null(tick.Fps);
    }
}
