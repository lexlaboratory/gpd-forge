// GPD Forge — semver-ish version comparison tests (pure). GPL-3.0-or-later.
using GpdForge.Update;
using Xunit;

namespace GpdForge.Core.Tests;

public class VersionCompareTests
{
    [Fact]
    public void A_higher_patch_is_newer()
    {
        Assert.True(VersionCompare.IsNewer("0.1.1", "0.1.0"));
        Assert.False(VersionCompare.IsNewer("0.1.0", "0.1.1"));
    }

    [Fact]
    public void A_higher_minor_beats_a_higher_patch_on_the_older_minor()
    {
        Assert.True(VersionCompare.IsNewer("0.2.0", "0.1.9"));
        Assert.False(VersionCompare.IsNewer("0.1.9", "0.2.0"));
    }

    [Fact]
    public void A_higher_major_beats_everything_below_it()
    {
        Assert.True(VersionCompare.IsNewer("1.0.0", "0.9.9"));
    }

    [Fact]
    public void Identical_versions_are_not_newer()
    {
        Assert.False(VersionCompare.IsNewer("0.1.0", "0.1.0"));
    }

    [Theory]
    [InlineData("v0.2.0", "0.1.0")]
    [InlineData("V0.2.0", "0.1.0")]
    [InlineData("0.2.0", "v0.1.0")]
    [InlineData("v0.2.0", "v0.1.0")]
    public void Tolerates_a_leading_v_on_either_side(string latest, string current)
    {
        Assert.True(VersionCompare.IsNewer(latest, current));
    }

    [Fact]
    public void Missing_trailing_parts_are_treated_as_zero()
    {
        Assert.False(VersionCompare.IsNewer("1.2", "1.2.0"));   // equal
        Assert.True(VersionCompare.IsNewer("1.3", "1.2.9"));    // 1.3.0 > 1.2.9
        Assert.False(VersionCompare.IsNewer("1", "1.0.0"));     // equal
    }

    [Fact]
    public void A_fourth_component_is_compared_when_present()
    {
        Assert.True(VersionCompare.IsNewer("1.2.0.5", "1.2.0.4"));
        Assert.False(VersionCompare.IsNewer("1.2.0.4", "1.2.0.5"));
    }

    [Fact]
    public void A_prerelease_or_build_suffix_is_ignored_for_the_numeric_comparison()
    {
        Assert.True(VersionCompare.IsNewer("1.0.0-beta.1", "0.9.0"));
        Assert.False(VersionCompare.IsNewer("1.0.0-beta.1", "1.0.0"));   // equal once the suffix is stripped
        Assert.True(VersionCompare.IsNewer("1.0.0+build.5", "0.9.9"));
    }

    [Theory]
    [InlineData(null, "0.1.0")]
    [InlineData("0.1.0", null)]
    [InlineData(null, null)]
    [InlineData("", "0.1.0")]
    [InlineData("not-a-version", "0.1.0")]
    [InlineData("0.1.0", "not-a-version")]
    [InlineData("1.2.3.4.5", "0.1.0")]     // too many parts
    [InlineData("1.a.0", "0.1.0")]         // non-numeric part
    [InlineData("-1.0.0", "0.1.0")]        // negative
    public void Unparseable_or_missing_input_is_never_reported_as_newer(string? latest, string? current)
    {
        Assert.False(VersionCompare.IsNewer(latest, current));
    }
}
