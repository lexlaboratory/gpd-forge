// GPD Forge — per-app rule validation/normalization tests. GPL-3.0-or-later.
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class AppRulePolicyTests
{
    private static AppRule Rule(string match, string mode = "gaming")
        => new(Guid.NewGuid(), AppRulePolicy.Normalize(match), mode, true);

    [Theory]
    [InlineData("  Steam.EXE ", "steam")]
    [InlineData("LM Studio", "lm studio")]
    [InlineData("ollama", "ollama")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData(".exe", "")]
    public void Normalize_trims_lowercases_and_drops_the_exe_suffix(string? raw, string expected)
        => Assert.Equal(expected, AppRulePolicy.Normalize(raw));

    [Theory]
    [InlineData("steam", "Steam.exe", true)]
    [InlineData("steam", "steamwebhelper", true)]
    [InlineData("steam", "notepad.exe", false)]
    [InlineData("steam", null, false)]
    [InlineData("steam", "", false)]
    public void Matches_is_a_case_insensitive_substring_of_the_process(string match, string? process, bool expected)
        => Assert.Equal(expected, AppRulePolicy.Matches(match, process));

    [Theory]
    [InlineData("battery", true)]
    [InlineData("windows", true)]
    [InlineData("gaming", true)]
    [InlineData("ai", true)]
    [InlineData("AI", false)]
    [InlineData("standby", false)]
    [InlineData("turbo", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidMode_accepts_only_the_selectable_modes(string? mode, bool expected)
        => Assert.Equal(expected, AppRulePolicy.IsValidMode(mode));

    [Fact]
    public void Validate_accepts_a_well_formed_rule() =>
        Assert.Null(AppRulePolicy.Validate("Steam.exe", "gaming", []));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".exe")]
    public void Validate_rejects_an_empty_process_match(string? match)
        => Assert.NotNull(AppRulePolicy.Validate(match, "gaming", []));

    [Fact]
    public void Validate_rejects_an_over_long_match()
        => Assert.NotNull(AppRulePolicy.Validate(new string('a', AppRulePolicy.MaxMatchLength + 1), "gaming", []));

    [Fact]
    public void Validate_rejects_an_unknown_mode()
        => Assert.NotNull(AppRulePolicy.Validate("steam", "turbo", []));

    [Fact]
    public void Validate_rejects_a_duplicate_ignoring_case_and_extension()
    {
        AppRule[] existing = [Rule("steam")];
        Assert.NotNull(AppRulePolicy.Validate("STEAM.exe", "ai", existing));
    }

    [Fact]
    public void Validate_lets_a_rule_keep_its_own_match_while_being_edited()
    {
        var existing = Rule("steam");
        Assert.Null(AppRulePolicy.Validate("steam", "ai", [existing], excluding: existing.Id));
    }
}
