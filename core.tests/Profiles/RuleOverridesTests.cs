// GPD Forge — per-game overrides on a rule: validation, load-time repair and the wire parser.
// GPL-3.0-or-later.
using System.Text.Json;
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class RuleOverridesTests
{
    [Fact]
    public void A_full_set_of_sane_overrides_is_accepted()
    {
        var o = new RuleOverrides(22, 60, "Aggressive", new GpuOverrides(AntiLag: true), ["discord", "chrome"]);
        Assert.Null(RuleOverridesPolicy.Validate(o));
        Assert.Null(RuleOverridesPolicy.Validate(null));
        Assert.Null(RuleOverridesPolicy.Validate(new RuleOverrides(FrameCapFps: 0)));   // 0 = cap off
    }

    [Theory]
    [InlineData(4)]
    [InlineData(41)]
    [InlineData(-1)]
    public void Stapm_outside_the_mode_preset_band_is_refused(int w)
    {
        var e = RuleOverridesPolicy.Validate(new RuleOverrides(StapmW: w));
        Assert.Equal("bad_stapm", e?.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public void A_frame_cap_that_is_not_off_or_a_frame_rate_is_refused(int fps) =>
        Assert.Equal("bad_frame_cap", RuleOverridesPolicy.Validate(new RuleOverrides(FrameCapFps: fps))?.Code);

    [Theory]
    [InlineData("Manual")]      // a duty is a hand setting, not a profile: no per-game manual fan
    [InlineData("Turbo")]
    [InlineData("aggressive")]  // the fan endpoint is case-sensitive too
    public void Only_the_automatic_fan_modes_are_accepted(string mode) =>
        Assert.Equal("bad_fan_mode", RuleOverridesPolicy.Validate(new RuleOverrides(FanMode: mode))?.Code);

    [Fact]
    public void Chill_and_anti_lag_together_is_refused_because_the_driver_refuses_it() =>
        Assert.Equal("bad_gpu", RuleOverridesPolicy.Validate(
            new RuleOverrides(Gpu: new GpuOverrides(AntiLag: true, Chill: true)))?.Code);

    [Fact]
    public void Freeze_entries_must_be_process_names_and_the_list_is_bounded()
    {
        Assert.Equal("bad_freeze", RuleOverridesPolicy.Validate(new RuleOverrides(Freeze: [@"C:\x\y.exe"]))?.Code);
        Assert.Equal("bad_freeze", RuleOverridesPolicy.Validate(new RuleOverrides(Freeze: ["  "]))?.Code);
        var tooMany = Enumerable.Range(0, RuleOverridesPolicy.MaxFreeze + 1).Select(i => $"p{i}").ToArray();
        Assert.Equal("bad_freeze", RuleOverridesPolicy.Validate(new RuleOverrides(Freeze: tooMany))?.Code);
    }

    [Fact]
    public void Normalize_canonicalises_freeze_and_collapses_an_empty_set_to_null()
    {
        var n = RuleOverridesPolicy.Normalize(new RuleOverrides(Freeze: ["Discord.exe", "discord", " chrome "]));
        Assert.Equal(["discord", "chrome"], n!.Freeze!);
        Assert.Null(RuleOverridesPolicy.Normalize(new RuleOverrides(Gpu: new GpuOverrides(), Freeze: [])));
    }

    [Fact]
    public void Sanitize_repairs_a_hand_edited_file_instead_of_dropping_the_rule()
    {
        var s = RuleOverridesPolicy.Sanitize(new RuleOverrides(
            StapmW: 99, FrameCapFps: -5, FanMode: "Turbo",
            Gpu: new GpuOverrides(AntiLag: true, Chill: true), Freeze: ["ok", @"bad\path"]));

        Assert.Equal(40, s!.StapmW);          // clamped to the preset ceiling, like ModeProfiles.Set
        Assert.Null(s.FrameCapFps);           // nonsense means "mode default", never a guess
        Assert.Null(s.FanMode);
        Assert.Null(s.Gpu);                   // an impossible pair is dropped whole
        Assert.Equal(["ok"], s.Freeze!);
    }

    [Fact]
    public void Records_with_equal_freeze_lists_are_equal()
    {
        // The focus loop compares the rule it applied with the rule the store holds now to notice an
        // edit mid-game; list equality by reference would re-apply on every reload.
        Assert.Equal(new RuleOverrides(Freeze: ["a"]), new RuleOverrides(Freeze: new List<string> { "a" }));
        Assert.NotEqual(new RuleOverrides(Freeze: ["a"]), new RuleOverrides(Freeze: ["b"]));
    }

    // --- the wire parser ---------------------------------------------------------------------

    private static RuleOverridesJson.Result Parse(string json) =>
        RuleOverridesJson.Parse(JsonDocument.Parse(json).RootElement.GetProperty("overrides"));

    [Fact]
    public void An_absent_key_is_not_the_same_as_null()
    {
        Assert.False(RuleOverridesJson.Parse(default).Present);
        var cleared = Parse("""{ "overrides": null }""");
        Assert.True(cleared.Present);
        Assert.Null(cleared.Value);
        Assert.Null(cleared.Error);
    }

    [Fact]
    public void A_complete_object_parses_and_unknown_fields_are_ignored()
    {
        var r = Parse("""
        { "overrides": { "stapmW": 22, "frameCapFps": 60, "fanMode": "Aggressive",
                         "gpu": { "antiLag": true, "chill": null }, "freeze": ["Discord.exe"],
                         "rsr": true } }
        """);
        Assert.Null(r.Error);
        Assert.Equal(new RuleOverrides(22, 60, "Aggressive", new GpuOverrides(true, null), ["discord"]), r.Value);
    }

    [Theory]
    [InlineData("""{ "overrides": { "stapmW": "22" } }""", "bad_stapm")]
    [InlineData("""{ "overrides": { "stapmW": 22.5 } }""", "bad_stapm")]
    [InlineData("""{ "overrides": { "frameCapFps": true } }""", "bad_frame_cap")]
    [InlineData("""{ "overrides": { "fanMode": 3 } }""", "bad_fan_mode")]
    [InlineData("""{ "overrides": { "gpu": { "chill": "yes" } } }""", "bad_gpu")]
    [InlineData("""{ "overrides": { "gpu": [] } }""", "bad_gpu")]
    [InlineData("""{ "overrides": { "freeze": "discord" } }""", "bad_freeze")]
    [InlineData("""{ "overrides": { "freeze": [1] } }""", "bad_freeze")]
    [InlineData("""{ "overrides": [] }""", "bad_overrides")]
    [InlineData("""{ "overrides": { "stapmW": 50 } }""", "bad_stapm")]
    public void A_wrong_type_or_range_names_the_field_that_is_wrong(string json, string code)
    {
        var r = Parse(json);
        Assert.True(r.Present);
        Assert.Equal(code, r.Error?.Code);
    }
}
