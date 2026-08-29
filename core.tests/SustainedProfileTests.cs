// GPD Forge - the AI mode's profile is a sustained ceiling, and stays one. GPL-3.0-or-later.
//
// ProfileShaper had existed, been unit-tested, and been called from exactly one place: GET /ai, where
// its result was rendered and thrown away. The applied profile came straight from the preset map. The
// default AI preset happened to be flat, so nothing looked wrong — but a single POST to /profiles/ai
// put boost headroom back, and the shaper was not in the path to stop it.
//
// These tests pin the guarantee rather than the default literal, so it survives someone editing the
// map, the endpoint, or the preset.
using GpdForge.Ai;
using GpdForge.Profiles;
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

[Collection("ModeProfiles")]
public class SustainedProfileTests
{
    /// <summary>Restores the preset map so these tests cannot leak into any other test's view of it.</summary>
    private static void WithPreset(string mode, Action body)
    {
        var original = ModeProfiles.Map[mode];
        try { body(); } finally { ModeProfiles.Map[mode] = original; }
    }

    [Fact]
    public void Editing_the_ai_preset_cannot_reintroduce_boost()
    {
        WithPreset("ai", () =>
        {
            var stored = ModeProfiles.Set("ai", new TdpProfile(StapmW: 25, FastW: 33, SlowW: 28, TctlC: 90));

            Assert.Equal(25, stored.StapmW);
            Assert.Equal(25, stored.FastW);
            Assert.Equal(25, stored.SlowW);
        });
    }

    [Fact]
    public void The_applied_ai_profile_is_flat_even_if_the_map_holds_boost()
    {
        // Defence in depth: Set flattens on the way in, but a profile written straight into the map
        // (a future default, a migration, a test) must not escape the guarantee either.
        WithPreset("ai", () =>
        {
            ModeProfiles.Map["ai"] = new TdpProfile(StapmW: 20, FastW: 40, SlowW: 35, TctlC: 88);

            var applied = ModeProfiles.For("ai")!.Value;

            Assert.Equal(applied.StapmW, applied.FastW);
            Assert.Equal(applied.StapmW, applied.SlowW);
        });
    }

    [Fact]
    public void Shaping_keeps_the_users_sustained_ceiling_and_only_removes_the_headroom()
    {
        // The user still decides how much sustained power an inference run gets; shaping only takes
        // away the burst above it. Flattening to some fixed wattage would be overriding them.
        WithPreset("ai", () =>
        {
            ModeProfiles.Set("ai", new TdpProfile(18, 33, 28, 85));

            var applied = ModeProfiles.For("ai")!.Value;

            Assert.Equal(18, applied.StapmW);
            Assert.Equal(85, applied.TctlC);
        });
    }

    [Fact]
    public void Gaming_keeps_its_boost_headroom()
    {
        // Shaping is right for a continuously CPU-bound job and wrong for a bursty frame. If this
        // ever fails, the sustained rule has leaked into modes it would only make slower.
        var gaming = ModeProfiles.For("gaming")!.Value;

        Assert.True(gaming.FastW > gaming.StapmW);
    }

    [Fact]
    public void Every_mode_that_is_not_the_sustained_one_is_returned_untouched()
    {
        foreach (var (mode, stored) in ModeProfiles.Map)
        {
            if (mode == ModeProfiles.SustainedMode) continue;
            Assert.Equal(stored, ModeProfiles.For(mode)!.Value);
        }
    }

    [Fact]
    public void The_sustained_mode_is_named_once_and_used_everywhere()
    {
        // Guard against the constant drifting away from the map key it selects — which would silently
        // switch shaping off for every caller at once.
        Assert.True(ModeProfiles.Map.ContainsKey(ModeProfiles.SustainedMode));
    }

    [Fact]
    public void An_out_of_band_edit_is_still_clamped_before_it_is_shaped()
    {
        WithPreset("ai", () =>
        {
            var stored = ModeProfiles.Set("ai", new TdpProfile(999, 999, 999, 999));

            Assert.Equal(ProfileShaper.MaxW, stored.StapmW);
            Assert.Equal(ProfileShaper.MaxTctlC, stored.TctlC);
            Assert.Equal(stored.StapmW, stored.FastW);
        });
    }
}
