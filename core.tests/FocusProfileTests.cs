// GPD Forge - focus-profile engine tests. GPL-3.0-or-later.
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

public class ModeRulesTests
{
    [Theory]
    [InlineData("ollama.exe", "ai")]
    [InlineData("LM Studio", "ai")]
    [InlineData("steam", "gaming")]
    [InlineData("retroarch.exe", "gaming")]
    [InlineData("notepad", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ModeFor_maps_known_apps(string? process, string? expected)
    {
        Assert.Equal(expected, ModeRules.Default().ModeFor(process));
    }
}

public class FocusProfileEngineTests
{
    [Theory]
    [InlineData("ollama.exe", true, "ai")]
    [InlineData("steam", false, "gaming")]
    [InlineData("explorer", true, "windows")]   // unknown + AC
    [InlineData("explorer", false, "battery")]  // unknown + battery
    public void Resolve_uses_rules_then_power_default(string proc, bool ac, string expected)
    {
        Assert.Equal(expected, new FocusProfileEngine().Resolve(proc, ac));
    }

    [Fact]
    public void Switch_requires_stability_ticks()
    {
        var e = new FocusProfileEngine("windows", ModeRules.Default(), stabilityTicks: 3);
        Assert.Null(e.Tick("ollama", true));   // 1
        Assert.Null(e.Tick("ollama", true));   // 2
        Assert.Equal("ai", e.Tick("ollama", true)); // 3 -> switch
        Assert.Equal("ai", e.Active);
        Assert.Null(e.Tick("ollama", true));   // already active
    }

    [Fact]
    public void Brief_alttab_does_not_flip_the_mode()
    {
        var e = new FocusProfileEngine("windows", null, stabilityTicks: 3);
        Assert.Null(e.Tick("ollama", true));   // candidate ai = 1
        Assert.Null(e.Tick("notepad", true));  // resolves to active 'windows' -> candidate reset
        Assert.Null(e.Tick("ollama", true));   // ai = 1 again
        Assert.Null(e.Tick("ollama", true));   // ai = 2
        Assert.Equal("ai", e.Tick("ollama", true)); // ai = 3 -> switch
    }

    [Fact]
    public void Switches_back_to_battery_on_unknown_app_on_dc()
    {
        var e = new FocusProfileEngine("ai", null, stabilityTicks: 1);
        Assert.Equal("battery", e.Tick("explorer", acConnected: false));
    }
}
