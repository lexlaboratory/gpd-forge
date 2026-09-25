// GPD Forge — the three layers a game profile writes into, each on its own. GPL-3.0-or-later.
using GpdForge.Api;
using GpdForge.Fan;
using GpdForge.Gpu;
using GpdForge.Profiles;
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class GameTdpLayerTests
{
    [Fact]
    public void Resolve_is_manual_then_game_then_preset()
    {
        var intent = new TdpIntent();
        var preset = ModeProfiles.For("gaming")!.Value;
        Assert.Equal(preset, intent.Resolve("gaming"));

        var game = TdpIntent.GameProfile(22, "gaming");
        intent.SetGame("gaming", game);
        Assert.Equal(game, intent.Resolve("gaming"));

        var manual = TdpIntent.ManualProfile(12, "gaming");
        intent.SetManual("gaming", manual);
        Assert.Equal(manual, intent.Resolve("gaming"));   // the overlay's stepper mid-game wins

        intent.ClearManual();
        Assert.Equal(game, intent.Resolve("gaming"));
    }

    [Fact]
    public void The_game_layer_belongs_to_its_mode()
    {
        var intent = new TdpIntent();
        intent.SetGame("gaming", TdpIntent.GameProfile(22, "gaming"));

        Assert.Null(intent.Game("windows"));
        Assert.Equal(ModeProfiles.For("windows"), intent.Resolve("windows"));
    }

    [Fact]
    public void A_game_profile_is_flat_at_the_modes_thermal_limit()
    {
        var p = TdpIntent.GameProfile(22, "gaming");
        Assert.Equal(new TdpProfile(22, 22, 22, ModeProfiles.For("gaming")!.Value.TctlC), p);
    }

    [Fact]
    public async Task A_mode_apply_keeps_the_game_layer_and_writes_it_under_its_own_owner()
    {
        var owners = new List<string>();
        var tdp = new DelegateTdp((_, owner) => owners.Add(owner));
        var intent = new TdpIntent();
        intent.SetGame("gaming", TdpIntent.GameProfile(22, "gaming"));
        var applier = new ProfileApplier(tdp, new NoRival(), intent: intent);

        await applier.ApplyAsync("gaming", CancellationToken.None);   // e.g. re-picking gaming mid-game
        await applier.ApplyAsync("windows", CancellationToken.None);

        Assert.Equal([TdpOwner.GameProfile, TdpOwner.Mode], owners);
        Assert.NotNull(intent.Game("gaming"));
    }

    private sealed class DelegateTdp(Action<TdpProfile, string> onApply) : ITdpController
    {
        public Task<TdpApplyResult> ApplyAsync(TdpProfile profile, string owner, CancellationToken ct)
        {
            onApply(profile, owner);
            return Task.FromResult(new TdpApplyResult(profile, new TdpReadout(profile.StapmW, profile.FastW), true, 1));
        }
    }

    private sealed class NoRival : IPowerControllerDetector
    {
        public bool OthersRunning(out string[] names) { names = []; return false; }
    }
}

public sealed class FanOverrideTests
{
    [Fact]
    public void Restore_puts_back_the_mode_from_before_the_game()
    {
        var fan = new FanState { Mode = "Quiet" };
        var o = new FanOverride();

        o.Apply(fan, "Aggressive");
        Assert.True(o.Active);
        Assert.Equal("Aggressive", fan.Mode);
        Assert.Equal("Quiet", o.PersistableMode(fan));

        o.Restore(fan);
        Assert.False(o.Active);
        Assert.Equal("Quiet", fan.Mode);
    }

    [Fact]
    public void A_mode_changed_behind_its_back_is_not_undone()
    {
        var fan = new FanState { Mode = "Quiet" };
        var o = new FanOverride();
        o.Apply(fan, "Aggressive");

        fan.Mode = "Balanced";   // anything that set the fan without going through Release
        Assert.Equal("Balanced", o.PersistableMode(fan));
        o.Restore(fan);

        Assert.Equal("Balanced", fan.Mode);
    }

    [Fact]
    public void Released_means_forgotten()
    {
        var fan = new FanState { Mode = "Quiet" };
        var o = new FanOverride();
        o.Apply(fan, "Aggressive");

        o.Release();
        o.Restore(fan);

        Assert.Equal("Aggressive", fan.Mode);
        Assert.Equal("Aggressive", o.PersistableMode(fan));
    }
}

public sealed class GpuFeatureOverrideTests
{
    private static readonly GpuProfile Gaming = GpuModeProfiles.For("gaming")!;
    private static readonly GpuProfile Battery = GpuModeProfiles.For("battery")!;

    [Fact]
    public void No_game_opinion_leaves_the_mode_profile_alone()
    {
        Assert.Same(Gaming, GpuFeatureOverride.Merge(Gaming, null, null));
        Assert.Null(GpuFeatureOverride.Merge(null, null, null));
    }

    [Fact]
    public void Chill_for_a_game_in_gaming_replaces_anti_lag_instead_of_conflicting()
    {
        var p = GpuFeatureOverride.Merge(Gaming, null, chill: true)!;
        Assert.True(p.Chill);
        Assert.False(p.AntiLag);
        Assert.Null(p.Conflict);
    }

    [Fact]
    public void Anti_lag_for_a_game_in_battery_turns_chill_off()
    {
        var p = GpuFeatureOverride.Merge(Battery, antiLag: true, null)!;
        Assert.True(p.AntiLag);
        Assert.False(p.Chill);
        Assert.Null(p.Conflict);
    }

    [Fact]
    public void Turning_a_mode_feature_off_for_a_game_works_without_a_mode_profile_too()
    {
        Assert.False(GpuFeatureOverride.Merge(Gaming, antiLag: false, null)!.AntiLag);
        var fromNothing = GpuFeatureOverride.Merge(null, antiLag: true, null)!;
        Assert.True(fromNothing.AntiLag);
        Assert.False(fromNothing.Chill);
    }

    [Fact]
    public void The_agent_key_changes_when_a_game_arrives_or_leaves_and_not_otherwise()
    {
        var plain = GpuFeatureOverride.Key("gaming", null, null);
        Assert.Equal(plain, GpuFeatureOverride.Key("gaming", null, null));
        Assert.NotEqual(plain, GpuFeatureOverride.Key("gaming", null, true));
        Assert.NotEqual(GpuFeatureOverride.Key("gaming", false, null), GpuFeatureOverride.Key("gaming", null, null));
    }

    // F1 audit round 1 (2026-09-25): one non-2xx from /gpu/desired produced "gaming|mode|mode", which
    // differs from a game's "gaming|off|on" — so the agent wrote the mode's profile over the game's and
    // the next good read wrote it back. Unreadable is "skip this tick", not "no game opinion".
    [Fact]
    public void An_unreadable_desired_state_reconciles_nothing_this_tick()
    {
        Assert.Null(GpuFeatureOverride.ReconcileKey("gaming", desiredRead: false, null, null));
        Assert.Null(GpuFeatureOverride.ReconcileKey(null, desiredRead: true, null, null));
        Assert.Equal(GpuFeatureOverride.Key("gaming", null, null), GpuFeatureOverride.ReconcileKey("gaming", true, null, null));
        Assert.Equal(GpuFeatureOverride.Key("gaming", false, true), GpuFeatureOverride.ReconcileKey("gaming", true, false, true));
    }
}
