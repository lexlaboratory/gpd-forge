// GPD Forge — per-game overrides through the focus loop: apply, restore, and who wins. GPL-3.0-or-later.
//
// F1 (2026-09-25). A rule's overrides are layered through the owners that already govern each setting
// (TdpIntent, GpuDesiredState, FanState), so these tests drive the real FocusProfileLoop and
// ProfileApplier over a recording TDP controller and check what each owner holds afterwards.
using GpdForge.Api;
using GpdForge.Fan;
using GpdForge.Gpu;
using GpdForge.Guardian;
using GpdForge.Profiles;
using GpdForge.Tdp;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed partial class GameProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpdforge-game-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly RuleOverrides EldenRing =
        new(StapmW: 22, FrameCapFps: 60, FanMode: "Aggressive", Gpu: new GpuOverrides(AntiLag: true), Freeze: ["discord"]);

    private sealed class FakeForeground(string? proc) : IForegroundApp
    {
        public string? Proc { get; set; } = proc;
        public string? Current() => Proc;
    }

    private sealed class RecordingTdp : ITdpController
    {
        public List<(TdpProfile Profile, string Owner)> Writes { get; } = [];
        public Task<TdpApplyResult> ApplyAsync(TdpProfile profile, string owner, CancellationToken ct)
        {
            Writes.Add((profile, owner));
            return Task.FromResult(new TdpApplyResult(profile, new TdpReadout(profile.StapmW, profile.FastW), true, 1));
        }
    }

    private sealed class NoRivals : IPowerControllerDetector
    {
        public bool OthersRunning(out string[] names) { names = []; return false; }
    }

    private sealed record Rig(
        FocusProfileLoop Loop, FakeForeground Fg, RecordingTdp Tdp, ModeState Mode, TdpIntent Intent,
        AppRuleStore Rules, AppRule Rule, FanState Fan, FanOverride FanOverride, GpuDesiredState Gpu,
        ActiveGameProfileState Active, AutoFpsState AutoFps, ProfileApplier Applier, GpuAgentState Agent,
        GameProfileApplier Games);

    /// <param name="gpuGate">The GPU-profiles gate; open by default so the cap and Radeon paths run.</param>
    private Rig Build(string? foreground, RuleOverrides? overrides = null, GuardianService? guardian = null,
        string fanMode = "Balanced", bool noOverrides = false, bool gpuGate = true,
        IGpdFanController? fanController = null, IPowerControllerDetector? detector = null,
        CapRestoreStore? capStore = null, GameFreezer? freezer = null)
    {
        var fg = new FakeForeground(foreground);
        var tdp = new RecordingTdp();
        var intent = new TdpIntent();
        var mode = new ModeState();
        var rules = new AppRuleStore(Path.Combine(_dir, "rules"));   // seeded: steam -> gaming, etc.
        var rule = rules.Add("eldenring", "gaming", true, noOverrides ? null : overrides ?? EldenRing);
        var fan = new FanState { Mode = fanMode };
        var fanOverride = new FanOverride();
        var gpu = new GpuDesiredState();
        var active = new ActiveGameProfileState();
        var autoFps = new AutoFpsState();
        var agent = new GpuAgentState();
        var games = new GameProfileApplier(intent, fan, fanOverride, gpu, agent, autoFps, active,
            fanController: fanController, gpuGateOpen: () => gpuGate, capStore: capStore, freezer: freezer);
        var applier = new ProfileApplier(tdp, detector ?? new NoRivals(), intent: intent, guardian: guardian);
        var telemetry = new FixedTelemetrySource(TelemetrySnapshot.Unmeasured with { AcConnected = true, AcUnknown = false });
        var loop = new FocusProfileLoop(fg, telemetry, mode, applier, rules, isRunning: _ => true, games: games, intent: intent);
        return new Rig(loop, fg, tdp, mode, intent, rules, rule, fan, fanOverride, gpu, active, autoFps, applier, agent, games);
    }

    private static async Task TickAsync(Rig rig, int times)
    {
        for (int i = 0; i < times; i++) await rig.Loop.TickAsync(CancellationToken.None);
    }

    private static TdpProfile Flat(int w, string mode) => TdpIntent.GameProfile(w, mode);

    // --- apply ---------------------------------------------------------------------------------

    [Fact]
    public async Task Entering_a_ruled_game_from_another_mode_writes_its_watts_once_as_the_game_profile()
    {
        var rig = Build("eldenring");

        await TickAsync(rig, 3);

        Assert.Equal("gaming", rig.Mode.Active);
        // ONE write, straight to the game's value: not the gaming preset followed by 22 W.
        var write = Assert.Single(rig.Tdp.Writes);
        Assert.Equal(Flat(22, "gaming"), write.Profile);
        Assert.Equal(TdpOwner.GameProfile, write.Owner);
        Assert.Equal(Flat(22, "gaming"), rig.Intent.Resolve("gaming"));

        Assert.Equal("Aggressive", rig.Fan.Mode);
        Assert.Equal(60, rig.Gpu.FrameCapFps);
        Assert.True(rig.Gpu.Requested);
        Assert.True(rig.Gpu.AntiLag);
        Assert.Null(rig.Gpu.Chill);

        var p = Assert.IsType<ActiveGameProfile>(rig.Active.Current);
        Assert.Equal("eldenring", p.Game);
        Assert.Equal(rig.Rule.Id, p.RuleId);
        Assert.Equal("gaming", p.Mode);
        Assert.Equal(new AppliedOverrides(22, 60, "Aggressive", true, null), p.Applied);
        Assert.Empty(p.Skipped);
        Assert.Equal(["discord"], p.Freeze);   // stored, reported, not acted on (F5)
    }

    [Fact]
    public async Task Moving_between_two_games_of_the_same_mode_still_applies_the_profile()
    {
        // Steam and Elden Ring are both `gaming`: the mode engine switches nothing, the rule must.
        var rig = Build("steam");
        await TickAsync(rig, 3);
        Assert.Equal("gaming", rig.Mode.Active);
        Assert.Null(rig.Active.Current?.Applied.StapmW);
        rig.Tdp.Writes.Clear();

        rig.Fg.Proc = "eldenring";
        await TickAsync(rig, 2);
        Assert.Empty(rig.Tdp.Writes);   // hysteresis: not yet
        await TickAsync(rig, 1);

        var write = Assert.Single(rig.Tdp.Writes);
        Assert.Equal((Flat(22, "gaming"), TdpOwner.GameProfile), write);
    }

    [Fact]
    public async Task A_rule_without_overrides_records_no_profile_and_touches_nothing_else()
    {
        // F1 audit round 1: it used to record an empty profile, so every mode-only rule (the seeded
        // steam, yuzu...) announced "Profile steam applied: mode settings" — and the mock said inactive.
        var rig = Build("eldenring", noOverrides: true);

        await TickAsync(rig, 3);

        Assert.Equal((ModeProfiles.For("gaming")!.Value, TdpOwner.Mode), Assert.Single(rig.Tdp.Writes));
        Assert.Equal("Balanced", rig.Fan.Mode);
        Assert.False(rig.Gpu.Requested);
        Assert.Null(rig.Active.Current);

        rig.Tdp.Writes.Clear();
        await TickAsync(rig, 3);   // and settling on it does not re-run anything tick after tick
        Assert.Empty(rig.Tdp.Writes);
    }

    [Fact]
    public async Task A_cap_of_zero_turns_the_driver_cap_off()
    {
        var rig = Build("eldenring", new RuleOverrides(FrameCapFps: 0));
        await TickAsync(rig, 3);

        Assert.True(rig.Gpu.Requested);
        Assert.Null(rig.Gpu.FrameCapFps);
        Assert.Equal(0, rig.Active.Current!.Applied.FrameCapFps);
    }

    [Fact]
    public async Task A_cap_below_the_auto_fps_target_is_skipped_and_says_why()
    {
        // The one pathological pairing FrameRateGovernance exists to refuse must not arrive by the back
        // door of a game profile either.
        var rig = Build("eldenring", new RuleOverrides(StapmW: 22, FrameCapFps: 30));
        rig.AutoFps.Enabled = true;
        rig.AutoFps.TargetFps = 60;

        await TickAsync(rig, 3);

        Assert.False(rig.Gpu.Requested);
        var p = rig.Active.Current!;
        Assert.Null(p.Applied.FrameCapFps);
        Assert.Equal(22, p.Applied.StapmW);   // the rest still applies
        var skipped = Assert.Single(p.Skipped);
        Assert.Equal("frameCapFps", skipped.Field);
        Assert.Contains("auto-FPS", skipped.Reason);
    }

    // --- restore -------------------------------------------------------------------------------

    [Fact]
    public async Task Leaving_to_the_desktop_restores_everything_and_writes_the_new_modes_preset()
    {
        var rig = Build("eldenring");
        await TickAsync(rig, 3);
        rig.Tdp.Writes.Clear();

        rig.Fg.Proc = "notepad";   // an ordinary app (explorer is a non-game window and would hold the game)
        await TickAsync(rig, 3);

        Assert.Equal("windows", rig.Mode.Active);
        Assert.Equal((ModeProfiles.For("windows")!.Value, TdpOwner.Mode), Assert.Single(rig.Tdp.Writes));
        Assert.Null(rig.Intent.Game("gaming"));
        Assert.Equal("Balanced", rig.Fan.Mode);
        Assert.Null(rig.Gpu.FrameCapFps);        // nothing was capped before the game: off again
        Assert.Null(rig.Gpu.AntiLag);            // the mode's Radeon profile again
        Assert.Null(rig.Active.Current);
    }

    [Fact]
    public async Task Leaving_to_another_game_of_the_same_mode_writes_the_preset_back()
    {
        var rig = Build("eldenring");
        await TickAsync(rig, 3);
        rig.Tdp.Writes.Clear();

        rig.Fg.Proc = "steam";
        await TickAsync(rig, 3);

        Assert.Equal("gaming", rig.Mode.Active);
        Assert.Equal((ModeProfiles.For("gaming")!.Value, TdpOwner.Mode), Assert.Single(rig.Tdp.Writes));
        Assert.Equal("Balanced", rig.Fan.Mode);
    }

    [Fact]
    public async Task A_brief_alt_tab_restores_nothing()
    {
        var rig = Build("eldenring");
        await TickAsync(rig, 3);
        rig.Tdp.Writes.Clear();

        rig.Fg.Proc = "notepad";
        await TickAsync(rig, 2);
        rig.Fg.Proc = "eldenring";
        await TickAsync(rig, 3);

        Assert.Empty(rig.Tdp.Writes);
        Assert.Equal("Aggressive", rig.Fan.Mode);
        Assert.NotNull(rig.Active.Current);
    }

    [Fact]
    public async Task The_previous_cap_is_what_comes_back()
    {
        var rig = Build("eldenring");
        rig.Gpu.RequestFrameCap(45, DateTimeOffset.UtcNow);   // what the user had asked for before
        await TickAsync(rig, 3);
        Assert.Equal(60, rig.Gpu.FrameCapFps);

        rig.Fg.Proc = "notepad";   // an ordinary app (explorer is a non-game window and would hold the game)
        await TickAsync(rig, 3);

        Assert.Equal(45, rig.Gpu.FrameCapFps);
    }

    [Fact]
    public async Task A_cap_the_user_set_mid_game_is_not_undone_on_exit()
    {
        var rig = Build("eldenring");
        await TickAsync(rig, 3);
        rig.Gpu.RequestFrameCap(40, DateTimeOffset.UtcNow);   // POST /gpu/frame-cap while playing

        rig.Fg.Proc = "notepad";   // an ordinary app (explorer is a non-game window and would hold the game)
        await TickAsync(rig, 3);

        Assert.Equal(40, rig.Gpu.FrameCapFps);
    }

    [Fact]
    public async Task A_mode_the_user_picks_over_the_game_ends_the_profile_without_another_write()
    {
        var rig = Build("eldenring");
        await TickAsync(rig, 3);

        // POST /mode windows while the game is still in front.
        rig.Mode.Active = "windows";
        await rig.Applier.ApplyAsync("windows", CancellationToken.None);
        rig.Tdp.Writes.Clear();
        await TickAsync(rig, 1);

        Assert.Empty(rig.Tdp.Writes);              // the user's own apply already wrote windows
        Assert.Equal("windows", rig.Mode.Active);  // and the engine does not fight a hand-picked mode
        Assert.Null(rig.Active.Current);
        Assert.Equal("Balanced", rig.Fan.Mode);
    }

    [Fact]
    public async Task Editing_the_rule_mid_game_re_applies_it()
    {
        var rig = Build("eldenring");
        await TickAsync(rig, 3);
        rig.Tdp.Writes.Clear();

        rig.Rules.Update(rig.Rule.Id, "eldenring", "gaming", true, EldenRing with { StapmW = 18, FanMode = "Quiet" });
        await TickAsync(rig, 1);

        Assert.Equal((Flat(18, "gaming"), TdpOwner.GameProfile), Assert.Single(rig.Tdp.Writes));
        Assert.Equal("Quiet", rig.Fan.Mode);

        rig.Fg.Proc = "notepad";   // an ordinary app (explorer is a non-game window and would hold the game)
        await TickAsync(rig, 3);
        Assert.Equal("Balanced", rig.Fan.Mode);   // the ORIGINAL fan mode, not the first game value
    }

    [Fact]
    public async Task Disabling_the_rule_mid_game_ends_the_profile()
    {
        var rig = Build("eldenring");
        await TickAsync(rig, 3);

        rig.Rules.Update(rig.Rule.Id, "eldenring", "gaming", enabled: false);
        await TickAsync(rig, 1);

        Assert.Null(rig.Active.Current);
        Assert.Equal("Balanced", rig.Fan.Mode);
    }

    // --- fan ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_game_fan_mode_is_never_what_gets_saved()
    {
        var rig = Build("eldenring");
        await TickAsync(rig, 3);
        Assert.Equal("Aggressive", rig.Fan.Mode);

        // What POST /fan with only a duty, and the settings export, would save right now.
        Assert.Equal("Balanced", rig.FanOverride.PersistableMode(rig.Fan));

        // And the store itself is untouched by the whole enter/leave cycle.
        var store = new FanPreferenceStore(Path.Combine(_dir, "fan"));
        rig.Fg.Proc = "notepad";   // an ordinary app (explorer is a non-game window and would hold the game)
        await TickAsync(rig, 3);
        Assert.False(File.Exists(Path.Combine(_dir, "fan", "fan.json")));
        Assert.Equal(FanPreference.Default, store.Read());
    }

    [Fact]
    public async Task A_fan_mode_the_user_picks_mid_game_stays_after_it()
    {
        var rig = Build("eldenring");
        await TickAsync(rig, 3);

        rig.FanOverride.Release();          // POST /fan { mode: "Quiet" }
        rig.Fan.Mode = "Quiet";
        Assert.Equal("Quiet", rig.FanOverride.PersistableMode(rig.Fan));

        rig.Fg.Proc = "notepad";   // an ordinary app (explorer is a non-game window and would hold the game)
        await TickAsync(rig, 3);
        Assert.Equal("Quiet", rig.Fan.Mode);
    }

    // --- the guardian and the reassert --------------------------------------------------------

    private static GuardianService Throttling()
    {
        double now = 0;
        var g = new GuardianService(() => now);
        for (int i = 0; i < 30; i++)
        {
            g.Observe(new TelemetrySnapshot(97, 0, 0, 0, 0, 0, 0, 0, 80, 0, true, true));
            now += 1;
        }
        Assert.True(g.Throttling);
        return g;
    }

    [Fact]
    public async Task While_the_guardian_throttles_the_game_profile_writes_nothing_and_its_ceiling_holds()
    {
        var guardian = Throttling();
        var rig = Build("steam", guardian: guardian);
        await TickAsync(rig, 3);            // gaming, held by the guardian
        Assert.Empty(rig.Tdp.Writes);

        rig.Fg.Proc = "eldenring";
        await TickAsync(rig, 3);

        Assert.Empty(rig.Tdp.Writes);
        // The intent carries the game, so the guardian's ceiling is computed under it — and the
        // throttle-clear restore (ForgeWorker: intent.Resolve) brings back the game's 22 W, not the preset.
        var intent = rig.Intent.Resolve("gaming")!.Value;
        Assert.Equal(Flat(22, "gaming"), intent);
        var ceiling = GuardianThrottle.Profile(guardian.ThrottledToW!.Value, intent, 96);
        Assert.True(ceiling.StapmW <= guardian.ThrottledToW && ceiling.FastW <= intent.FastW);
    }

    [Fact]
    public async Task A_plain_mode_apply_is_also_held_under_a_throttle()
    {
        var tdp = new RecordingTdp();
        var applier = new ProfileApplier(tdp, new NoRivals(), intent: new TdpIntent(), guardian: Throttling());

        Assert.Equal(ApplyOutcome.HeldByGuardian, await applier.ApplyAsync("gaming", CancellationToken.None));
        Assert.Empty(tdp.Writes);
    }

    private static (FakeSilicon Silicon, TdpState State, TdpIntent Intent, ProfileApplier Applier, TdpReasserter Reasserter, SwitchableDetector Detector) Stack()
    {
        var silicon = new FakeSilicon();
        var state = new TdpState();
        var rule = new TdpReadbackRule();
        var tdp = new SerializedTdpController(new GpdForge.Broker.AuditingTdpController(
            new ClosedLoopTdpController(silicon, new NoWait(), rule: rule), new GpdForge.Broker.HardwareAuditLog(), state, "test"), state);
        var detector = new SwitchableDetector();
        var intent = new TdpIntent();
        var mode = new ModeState { Active = "gaming" };
        var reasserter = new TdpReasserter(silicon, tdp, state, detector, intent, mode, new ManualTimeProvider(), rule: rule);
        return (silicon, state, intent, new ProfileApplier(tdp, detector, intent: intent, state: state), reasserter, detector);
    }

    [Fact]
    public async Task The_reassert_keeps_the_game_profile_not_the_preset()
    {
        var (silicon, state, intent, applier, reasserter, _) = Stack();
        intent.SetGame("gaming", Flat(22, "gaming"));
        await applier.ApplyAsync("gaming", CancellationToken.None);
        Assert.Equal(TdpOwner.GameProfile, state.Last!.Value.Owner);

        silicon.Limits = new TdpReadout(25, 33);   // the firmware put the preset-ish limits back

        Assert.Equal(ReassertOutcome.Reasserted, await reasserter.ReassertAsync(CancellationToken.None));
        Assert.Equal(new TdpReadout(22, 22), silicon.Limits);
    }

    [Fact]
    public async Task After_a_rival_yield_the_reassert_applies_the_game_profile_once_it_is_gone()
    {
        var (silicon, _, intent, applier, reasserter, detector) = Stack();
        intent.SetGame("gaming", Flat(22, "gaming"));
        detector.Rival = true;
        Assert.Equal(ApplyOutcome.SkippedConflict, await applier.ApplyAsync("gaming", CancellationToken.None));

        detector.Rival = false;
        silicon.Limits = new TdpReadout(15, 20);

        Assert.Equal(ReassertOutcome.Reasserted, await reasserter.ReassertAsync(CancellationToken.None));
        Assert.Equal(new TdpReadout(22, 22), silicon.Limits);
    }
}
