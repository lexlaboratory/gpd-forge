// GPD Forge — per-session frame pacing, energy, mode and frame cap (plan F2). GPL-3.0-or-later.
using GpdForge.Sessions;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class SessionPacingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 20, 0, 0, TimeSpan.Zero);

    private static SessionTick Tick(int second, double? low01 = 40, double? stutters = 0, double? packageW = 18,
        double? dischargeW = null, bool ac = true, string? mode = "gaming", int? cap = 60)
        => new(T0.AddSeconds(second), "game.exe", 60, 55, 70, packageW, 80, ac,
            Fps01PctLow: low01, StuttersPerMin: stutters, DischargeW: dischargeW, Mode: mode, FrameCapFps: cap);

    private static GameSession Play(IEnumerable<SessionTick> ticks)
    {
        var tracker = new SessionTracker();
        foreach (var t in ticks) Assert.Null(tracker.Observe(t));
        return tracker.Flush(T0.AddHours(2))!;
    }

    [Fact]
    public void Records_the_worst_point_one_percent_low_and_the_mean_stutter_rate()
    {
        // 0.1 % low: the worst reading, conservatively (the same percentile-of-windows rule as the 1 % low).
        // Stutters/min: every per-second reading is a rate over the last 10 s, so they average; summing
        // them would count one hitch ten times.
        var s = Play(Enumerable.Range(0, 120).Select(i => Tick(i, low01: i == 50 ? 12 : 40, stutters: i < 60 ? 6 : 0)));
        Assert.Equal(12, s.Fps01PctLow);
        Assert.Equal(3, s.StuttersPerMin);
    }

    [Fact]
    public void Pacing_is_null_when_it_was_never_measured()
    {
        var s = Play(Enumerable.Range(0, 120).Select(i => Tick(i, low01: null, stutters: null, packageW: null, mode: null, cap: null)));
        Assert.Null(s.Fps01PctLow);
        Assert.Null(s.StuttersPerMin);
        Assert.Null(s.EnergyWh);
        Assert.Null(s.EnergySource);
        Assert.Null(s.Mode);
        Assert.Null(s.FrameCapFps);
    }

    [Fact]
    public void Integrates_package_power_on_ac()
    {
        // 18 W for 3600 one-second steps = 18 Wh.
        var s = Play(Enumerable.Range(0, 3601).Select(i => Tick(i)));
        Assert.Equal(18, s.EnergyWh);
        Assert.Equal("package", s.EnergySource);
    }

    [Fact]
    public void Integrates_the_battery_drain_when_the_whole_session_ran_on_battery()
    {
        // The drain is what the battery actually paid (screen, RAM, fan included); the package is only
        // the APU. On battery the drain is the figure that predicts how long a game lasts.
        var s = Play(Enumerable.Range(0, 1801).Select(i => Tick(i, packageW: 15, dischargeW: 24, ac: false)));
        Assert.Equal(12, s.EnergyWh);
        Assert.Equal("battery", s.EnergySource);
    }

    [Fact]
    public void A_gap_in_the_ticks_is_not_integrated_as_if_the_power_had_held()
    {
        // Sleep/resume or a stalled sampler: the 10-minute hole must not be billed at the last reading.
        var ticks = Enumerable.Range(0, 61).Select(i => Tick(i)).Concat(Enumerable.Range(0, 30).Select(i => Tick(59 + 50 + i)));
        var s = Play(ticks);
        // 60 s + a capped 5 s step + 29 s, at 18 W.
        Assert.Equal(Math.Round(18.0 * (60 + 5 + 29) / 3600, 2), s.EnergyWh);
    }

    [Fact]
    public void Mode_and_frame_cap_are_the_ones_the_session_spent_most_of_its_time_in()
    {
        var s = Play(Enumerable.Range(0, 120).Select(i => i < 30 ? Tick(i, mode: "windows", cap: null) : Tick(i, mode: "gaming", cap: 45)));
        Assert.Equal("gaming", s.Mode);
        Assert.Equal(45, s.FrameCapFps);
    }

    [Fact]
    public void No_frame_cap_wins_when_the_session_mostly_ran_uncapped()
    {
        var s = Play(Enumerable.Range(0, 120).Select(i => Tick(i, cap: i < 30 ? 60 : null)));
        Assert.Null(s.FrameCapFps);
    }

    [Fact]
    public void Tick_carries_pacing_mode_and_cap_and_drops_a_drain_read_on_ac()
    {
        var snap = new TelemetrySnapshot(70, 60, 18, 2000, 3000, 40, 60, 55, 80, 21, AcConnected: true, TdpVerified: null);
        var pacing = new FramePacingMetrics(600, 10, 60, 55, 30, 1.2, 1, 6);
        var tick = SessionTick.From(snap, new FpsSample(60, 55, "game.exe"), T0, pacing, "gaming", 60);
        Assert.Equal(30, tick.Fps01PctLow);
        Assert.Equal(6, tick.StuttersPerMin);
        Assert.Equal("gaming", tick.Mode);
        Assert.Equal(60, tick.FrameCapFps);
        Assert.Null(tick.DischargeW);

        var onBattery = SessionTick.From(snap with { AcConnected = false }, new FpsSample(60, 55, "game.exe"), T0);
        Assert.Equal(21, onBattery.DischargeW);
        Assert.Null(onBattery.Fps01PctLow);
    }
}
