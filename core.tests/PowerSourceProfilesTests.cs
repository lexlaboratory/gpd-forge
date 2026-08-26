// GPD Forge — per-power-source auto mode-switch evaluator tests. GPL-3.0-or-later.
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

public class PowerSourceProfilesTests
{
    private static readonly PowerSourceConfig Enabled = new(Enabled: true, OnBatteryMode: "battery", OnAcMode: "windows");

    [Fact]
    public void Default_config_is_disabled()
    {
        Assert.False(new PowerSourceConfig().Enabled);
        Assert.Equal("battery", new PowerSourceConfig().OnBatteryMode);
        Assert.Equal("windows", new PowerSourceConfig().OnAcMode);
    }

    [Fact]
    public void Disabled_never_switches_on_AC_or_battery()
    {
        var cfg = Enabled with { Enabled = false };
        Assert.Null(PowerSourceProfiles.Resolve(acConnected: true, cfg, currentMode: "gaming"));
        Assert.Null(PowerSourceProfiles.Resolve(acConnected: false, cfg, currentMode: "gaming"));
    }

    [Fact]
    public void On_AC_resolves_to_the_configured_AC_mode()
    {
        Assert.Equal("windows", PowerSourceProfiles.Resolve(acConnected: true, Enabled, currentMode: "gaming"));
    }

    [Fact]
    public void On_battery_resolves_to_the_configured_battery_mode()
    {
        Assert.Equal("battery", PowerSourceProfiles.Resolve(acConnected: false, Enabled, currentMode: "gaming"));
    }

    [Fact]
    public void Already_on_the_desired_mode_is_a_noop()
    {
        Assert.Null(PowerSourceProfiles.Resolve(acConnected: true, Enabled, currentMode: "windows"));
        Assert.Null(PowerSourceProfiles.Resolve(acConnected: false, Enabled, currentMode: "battery"));
    }

    [Fact]
    public void Custom_modes_are_honored()
    {
        var cfg = new PowerSourceConfig(Enabled: true, OnBatteryMode: "standby", OnAcMode: "ai");
        Assert.Equal("ai", PowerSourceProfiles.Resolve(true, cfg, "gaming"));
        Assert.Equal("standby", PowerSourceProfiles.Resolve(false, cfg, "gaming"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_target_mode_never_switches(string blank)
    {
        var cfg = Enabled with { OnAcMode = blank };
        Assert.Null(PowerSourceProfiles.Resolve(acConnected: true, cfg, currentMode: "gaming"));
    }
}
