// GPD Forge — per-game energy, the gaming / gaming-battery A/B and the per-game battery budget (plan F6).
// GPL-3.0-or-later.
using GpdForge.Sessions;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests.Sessions;

public class SessionEnergyTests
{
    private static GameSession S(string mode, double minutes, double? fps, double? low, double? energyWh, string? source,
        double? packageW = null, string app = "EldenRing.exe") =>
        new(Guid.NewGuid(), app, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(minutes), minutes * 60, (int)(minutes * 60), 0,
            fps, low, fps, null, null, packageW, source == "battery", null, null, null, [],
            EnergyWh: energyWh, EnergySource: source, Mode: mode);

    [Fact]
    public void Energy_rate_prefers_the_whole_system_drain_over_the_package()
    {
        // 30 min at 9 Wh = 18 Wh/h on battery; the package-only session (7 W) is not the battery's cost.
        var rate = SessionMath.EnergyRate([S("gaming", 30, 60, 30, 9, "battery"), S("gaming", 60, 60, 30, 7, "package")]);
        Assert.Equal(18, rate.WhPerHour);
        Assert.Equal("battery", rate.Source);
    }

    [Fact]
    public void Energy_rate_falls_back_to_the_package_and_is_null_without_energy()
    {
        var pkg = SessionMath.EnergyRate([S("gaming", 60, 60, 30, 12, "package"), S("gaming", 60, 60, 30, 8, "package")]);
        Assert.Equal(10, pkg.WhPerHour);
        Assert.Equal("package", pkg.Source);

        var none = SessionMath.EnergyRate([S("gaming", 60, 60, 30, null, null)]);
        Assert.Null(none.WhPerHour);
        Assert.Null(none.Source);
    }

    [Fact]
    public void Modes_compare_on_one_basis_battery_when_both_sides_ran_on_battery()
    {
        var modes = SessionMath.CompareModes([
            S("gaming", 60, 60, 40, 20, "battery", packageW: 15),
            S("gaming-battery", 60, 45, 35, 12, "battery", packageW: 8),
            S("windows", 60, 30, 20, 5, "battery"),
        ]);
        Assert.Equal(["gaming", "gaming-battery"], modes.Select(m => m.Mode));
        var g = modes[0]; var b = modes[1];
        Assert.Equal((60d, 40d, 20d, "battery", 3d), (g.FpsAvg!.Value, g.Fps1PctLow!.Value, g.WhPerHour!.Value, g.EnergySource!, g.FpsPerWatt!.Value));
        Assert.Equal((45d, 12d, 3.75), (b.FpsAvg!.Value, b.WhPerHour!.Value, b.FpsPerWatt!.Value));
        Assert.Equal(1, b.Sessions);
        Assert.Equal(3600, b.TotalSeconds);
    }

    [Fact]
    public void Modes_fall_back_to_package_power_when_one_side_only_ran_plugged_in()
    {
        // gaming ran on AC (package only), gaming-battery on battery: comparing the system drain of one
        // with the package of the other would flatter the plugged-in mode, so both use the package.
        var modes = SessionMath.CompareModes([
            S("gaming", 60, 60, 40, 15, "package", packageW: 15),
            S("gaming-battery", 60, 45, 35, 12, "battery", packageW: 8),
        ]);
        Assert.All(modes, m => Assert.Equal("package", m.EnergySource));
        Assert.Equal([15d, 8d], modes.Select(m => m.WhPerHour!.Value));
        Assert.Equal(5.63, modes[1].FpsPerWatt);
    }

    [Fact]
    public void Modes_without_any_energy_reading_keep_nulls()
    {
        var m = Assert.Single(SessionMath.CompareModes([S("gaming-battery", 30, 45, null, null, null)]));
        Assert.Null(m.WhPerHour);
        Assert.Null(m.FpsPerWatt);
        Assert.Null(m.EnergySource);
        Assert.Equal(45, m.FpsAvg);
    }

    [Fact]
    public void Per_game_rollup_carries_wh_per_hour_and_the_mode_comparison()
    {
        var game = Assert.Single(SessionMath.PerGame([
            S("gaming", 60, 60, 40, 20, "battery", packageW: 15),
            S("gaming-battery", 60, 45, 35, 12, "battery", packageW: 8),
        ]));
        Assert.Equal(16, game.WhPerHour);
        Assert.Equal("battery", game.EnergySource);
        Assert.Equal(2, game.Modes!.Count);
    }

    [Fact]
    public void Battery_rate_uses_only_battery_sessions_and_prefers_the_current_mode()
    {
        var sessions = new[]
        {
            S("gaming", 30, 60, 40, 10, "battery"),          // 20 Wh/h
            S("gaming-battery", 10, 45, 35, 2, "battery"),   // 12 Wh/h
            S("gaming", 120, 60, 40, 40, "package"),
        };
        var inMode = SessionMath.BatteryRate(sessions, "gaming");
        Assert.Equal((20d, "gaming"), (inMode!.Value.WhPerHour, inMode.Value.Mode!));

        // Ten minutes in gaming-battery is enough evidence for that mode.
        Assert.Equal(12, SessionMath.BatteryRate(sessions, "gaming-battery")!.Value.WhPerHour);

        // No battery play in this mode: every battery session of the game, mode unnamed.
        var any = SessionMath.BatteryRate(sessions, "battery")!.Value;
        Assert.Null(any.Mode);
        Assert.Equal(18, any.WhPerHour);   // 12 Wh over 40 min
    }

    [Fact]
    public void Battery_rate_needs_five_minutes_of_battery_play()
    {
        Assert.Null(SessionMath.BatteryRate([S("gaming", 4, 60, 40, 1, "battery")], "gaming"));
        Assert.Null(SessionMath.BatteryRate([S("gaming", 60, 60, 40, 15, "package")], "gaming"));
    }

    [Fact]
    public void Game_budget_divides_what_is_left_by_what_the_game_drains()
    {
        var b = BatteryEstimator.ForGame("eldenring", remainingWh: 30, whPerHour: 22.5, mode: "gaming");
        Assert.Equal(80, b.Minutes);   // "~1 h 20 m in this game"
        Assert.Equal("eldenring", b.App);
        Assert.Equal("gaming", b.Mode);
        Assert.Null(BatteryEstimator.ForGame("x", 30, 0, null).Minutes);
    }
}
