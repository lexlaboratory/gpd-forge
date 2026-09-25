// GPD Forge — F1 audit round 1: what the game profile may and may not claim, and whose TDP survives.
// GPL-3.0-or-later.
//
// The other half of GameProfileTests (same rig). Each test pins one finding of the 2026-09-25 audit.
using GpdForge.Fan;
using GpdForge.Gpu;
using GpdForge.Profiles;
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed partial class GameProfileTests
{
    private static TdpProfile Manual(int w, string mode) => TdpIntent.ManualProfile(w, mode);

    private static GpuAgentReport Report(bool available = true, int? userCap = null, string detail = "ok") =>
        new(available, available ? "Ready" : "Unavailable", "1.0", detail,
            new GpuSettingsSnapshot(null, null, null, null,
                new GpuFeatureState(Supported: true, Enabled: userCap is not null, Value: userCap ?? 60, Min: 15, Max: 1000)),
            DateTimeOffset.UtcNow);

    // --- the manual override lives until the MODE changes --------------------------------------

    [Fact]
    public async Task A_manual_value_set_mid_game_survives_the_game_leaving_in_the_same_mode()
    {
        var rig = Build("eldenring");
        await TickAsync(rig, 3);
        rig.Intent.SetManual("gaming", Manual(12, "gaming"));   // the overlay's stepper, mid-game
        rig.Tdp.Writes.Clear();

        rig.Fg.Proc = "steam";   // also gaming: only the game layer moves
        await TickAsync(rig, 3);

        Assert.Equal("gaming", rig.Mode.Active);
        Assert.Equal(Manual(12, "gaming"), rig.Intent.Manual("gaming"));
        Assert.Null(rig.Intent.Game("gaming"));
        Assert.Empty(rig.Tdp.Writes);   // 12 W is what the device holds: nothing to write
    }

    [Fact]
    public async Task A_manual_value_survives_an_edit_of_the_rule_mid_game()
    {
        var rig = Build("eldenring");
        await TickAsync(rig, 3);
        rig.Intent.SetManual("gaming", Manual(12, "gaming"));
        rig.Tdp.Writes.Clear();

        // Saving only a fan change from the Games sheet: End + Begin, both of which move the TDP layer.
        rig.Rules.Update(rig.Rule.Id, "eldenring", "gaming", true, EldenRing with { FanMode = "Quiet" });
        await TickAsync(rig, 1);

        Assert.Equal("Quiet", rig.Fan.Mode);
        Assert.Equal(Manual(12, "gaming"), rig.Intent.Manual("gaming"));
        Assert.Empty(rig.Tdp.Writes);
    }

    [Fact]
    public async Task A_manual_value_set_before_the_game_came_to_the_front_in_the_same_mode_is_kept()
    {
        var rig = Build("steam");
        await TickAsync(rig, 3);
        rig.Intent.SetManual("gaming", Manual(14, "gaming"));
        rig.Tdp.Writes.Clear();

        rig.Fg.Proc = "eldenring";
        await TickAsync(rig, 3);

        Assert.Empty(rig.Tdp.Writes);
        Assert.Equal(Manual(14, "gaming"), rig.Intent.Resolve("gaming"));
        Assert.Equal(Flat(22, "gaming"), rig.Intent.Game("gaming"));   // layered, under the manual value
    }

    [Fact]
    public async Task A_mode_change_still_ends_the_manual_value()
    {
        var rig = Build("eldenring");
        await TickAsync(rig, 3);
        rig.Intent.SetManual("gaming", Manual(12, "gaming"));

        rig.Fg.Proc = "notepad";
        await TickAsync(rig, 3);

        Assert.Equal("windows", rig.Mode.Active);
        Assert.Null(rig.Intent.Manual("gaming"));
    }

    // --- nothing is recorded as applied that nothing can apply -------------------------------

    [Fact]
    public async Task With_the_gpu_gate_closed_the_cap_and_radeon_settings_are_skipped_and_say_why()
    {
        var rig = Build("eldenring", gpuGate: false);
        await TickAsync(rig, 3);

        Assert.False(rig.Gpu.Requested);
        Assert.Null(rig.Gpu.AntiLag);
        var p = rig.Active.Current!;
        Assert.Equal(new AppliedOverrides(22, null, "Aggressive", null, null), p.Applied);
        Assert.Equal(["frameCapFps", "gpu"], p.Skipped.Select(s => s.Field));
        Assert.All(p.Skipped, s => Assert.Equal(GameProfileApplier.GpuGateOffReason, s.Reason));
    }

    [Fact]
    public async Task An_agent_reporting_radeon_control_unavailable_skips_them_with_its_reason()
    {
        var rig = Build("eldenring");
        rig.Agent.Report(Report(available: false, detail: "ADLX is not installed"));
        await TickAsync(rig, 3);

        Assert.False(rig.Gpu.Requested);
        Assert.All(rig.Active.Current!.Skipped, s => Assert.Contains("ADLX is not installed", s.Reason));
        Assert.Equal(2, rig.Active.Current!.Skipped.Count);
    }

    [Fact]
    public async Task With_fan_control_off_the_fan_is_skipped_and_left_alone()
    {
        var rig = Build("eldenring", fanController: new NoOpGpdFanController());
        await TickAsync(rig, 3);

        Assert.Equal("Balanced", rig.Fan.Mode);
        Assert.False(rig.FanOverride.Active);
        var p = rig.Active.Current!;
        Assert.Null(p.Applied.FanMode);
        Assert.Equal(new SkippedOverride("fanMode", GameProfileApplier.FanOffReason), Assert.Single(p.Skipped));
    }

    // --- the cap that comes back ---------------------------------------------------------------

    [Fact]
    public async Task Leaving_does_not_restore_a_cap_below_an_auto_fps_target_switched_on_mid_game()
    {
        var rig = Build("eldenring");
        rig.Gpu.RequestFrameCap(30, DateTimeOffset.UtcNow);   // the user's own cap before the game
        await TickAsync(rig, 3);
        Assert.Equal(60, rig.Gpu.FrameCapFps);

        rig.AutoFps.Enabled = true;   // POST /auto-fps checks against the GAME's 60 and allows 45
        rig.AutoFps.TargetFps = 45;
        rig.Fg.Proc = "notepad";
        await TickAsync(rig, 3);

        Assert.True(rig.Gpu.Requested);
        Assert.Null(rig.Gpu.FrameCapFps);   // off, which can never sit under a target
    }

    [Fact]
    public async Task A_cap_before_the_game_that_the_silent_agent_reports_later_is_the_one_restored()
    {
        var rig = Build("eldenring");   // the agent has not reported: the game auto-started at logon
        await TickAsync(rig, 3);
        Assert.Equal(60, rig.Gpu.FrameCapFps);

        rig.Agent.Report(Report(userCap: 50));   // its first report: the driver still holds the user's 50
        await TickAsync(rig, 1);
        rig.Fg.Proc = "notepad";
        await TickAsync(rig, 3);

        Assert.Equal(50, rig.Gpu.FrameCapFps);
    }

    [Fact]
    public async Task A_cap_before_the_game_that_was_never_read_is_not_forced_off_the_request_is_withdrawn()
    {
        var rig = Build("eldenring");
        await TickAsync(rig, 3);

        rig.Fg.Proc = "notepad";   // the agent never reported during the game, so it applied nothing
        await TickAsync(rig, 3);

        Assert.False(rig.Gpu.Requested);   // the agent leaves the user's own Adrenalin cap alone
        Assert.Null(rig.Gpu.FrameCapFps);
    }

    // --- the TDP write's outcome reaches the record ---------------------------------------------

    [Fact]
    public async Task A_yield_to_another_power_controller_is_recorded_with_its_name()
    {
        var detector = new SwitchableDetector { Rival = true };
        var rig = Build("eldenring", detector: detector);
        await TickAsync(rig, 3);

        Assert.Empty(rig.Tdp.Writes);
        var hold = rig.Active.Current!.TdpHold;
        Assert.Equal(ApplyOutcome.SkippedConflict, hold?.Outcome);
        Assert.Equal(["MotionAssistant"], hold!.Rivals);
    }

    [Fact]
    public async Task A_guardian_hold_is_recorded()
    {
        var rig = Build("eldenring", guardian: Throttling());
        await TickAsync(rig, 3);

        Assert.Equal(ApplyOutcome.HeldByGuardian, rig.Active.Current!.TdpHold?.Outcome);
    }

    [Fact]
    public async Task A_verified_write_records_no_hold()
    {
        var rig = Build("eldenring");
        await TickAsync(rig, 3);
        Assert.Null(rig.Active.Current!.TdpHold);
    }
}
