// GPD Forge — F1 audit round 4: a game's cap must not outlive the daemon. GPL-3.0-or-later.
//
// The same rig as GameProfileTests. The cap to restore lived only in memory, and nothing ended the
// profile when the host stopped. The agent's FRTC write is a persisted DRIVER setting, so a reboot, a
// service restart or an update with a capped game in front left the game's cap on the driver for every
// app — and the next game read it back as the user's own, so the user's cap was never recovered. The
// pending restore is now kept on disk from Begin to End and replayed at startup.
using GpdForge.Api;
using GpdForge.Fan;
using GpdForge.Gpu;
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed partial class GameProfileTests
{
    private string StateDir => Path.Combine(_dir, "state");

    /// <summary>A daemon starting afresh after a crash: new in-memory state, the same data directory.</summary>
    private (GameProfileApplier Games, GpuDesiredState Gpu, AutoFpsState AutoFps) Restarted()
    {
        var gpu = new GpuDesiredState();
        var autoFps = new AutoFpsState();
        var games = new GameProfileApplier(new TdpIntent(), new FanState(), new FanOverride(), gpu, new GpuAgentState(),
            autoFps, new ActiveGameProfileState(), gpuGateOpen: () => true, capStore: new CapRestoreStore(StateDir));
        return (games, gpu, autoFps);
    }

    [Fact]
    public async Task A_restart_mid_game_puts_back_the_users_cap_from_before_the_game()
    {
        var rig = Build("steam", EldenRing with { FrameCapFps = 30 }, capStore: new CapRestoreStore(StateDir));
        rig.Agent.Report(CapReport(45, DateTimeOffset.UtcNow));   // the user's own Adrenalin cap
        await TickAsync(rig, 3);
        rig.Fg.Proc = "eldenring";
        await TickAsync(rig, 3);
        Assert.Equal(30, rig.Gpu.FrameCapFps);

        // The service stops with the game in front: no End. The driver keeps the game's 30.
        var (games, gpu, _) = Restarted();
        Assert.False(gpu.Requested);
        games.RecoverPendingCap();

        Assert.True(gpu.Requested);
        Assert.Equal(45, gpu.FrameCapFps);
        Assert.Null(new CapRestoreStore(StateDir).Read());   // replayed once, not at every start
    }

    [Fact]
    public async Task A_restart_mid_game_turns_the_cap_off_when_the_user_had_none()
    {
        var rig = Build("steam", EldenRing with { FrameCapFps = 30 }, capStore: new CapRestoreStore(StateDir));
        rig.Agent.Report(new GpuAgentReport(true, "Ready", "1.0", "ok",
            new GpuSettingsSnapshot(null, null, null, null,
                new GpuFeatureState(Supported: true, Enabled: false, Value: 60, Min: 15, Max: 1000)),
            DateTimeOffset.UtcNow));
        await TickAsync(rig, 3);
        rig.Fg.Proc = "eldenring";
        await TickAsync(rig, 3);

        var (games, gpu, _) = Restarted();
        games.RecoverPendingCap();

        Assert.True(gpu.Requested);
        Assert.Null(gpu.FrameCapFps);
    }

    [Fact]
    public async Task A_restart_after_a_game_whose_previous_cap_was_never_read_leaves_the_driver_alone()
    {
        // The agent never reported during the game, so it never applied the game's cap either: the
        // driver still holds the user's own, and "off" would erase it (the End path's rule, round 1).
        var rig = Build("steam", EldenRing with { FrameCapFps = 30 }, capStore: new CapRestoreStore(StateDir));
        rig.Fg.Proc = "eldenring";
        await TickAsync(rig, 3);
        Assert.NotNull(new CapRestoreStore(StateDir).Read());

        var (games, gpu, _) = Restarted();
        games.RecoverPendingCap();

        Assert.False(gpu.Requested);
        Assert.Null(new CapRestoreStore(StateDir).Read());
    }

    [Fact]
    public async Task Leaving_the_game_normally_leaves_nothing_to_replay()
    {
        var rig = Build("steam", EldenRing with { FrameCapFps = 30 }, capStore: new CapRestoreStore(StateDir));
        rig.Agent.Report(CapReport(45, DateTimeOffset.UtcNow));
        rig.Fg.Proc = "eldenring";
        await TickAsync(rig, 3);
        rig.Fg.Proc = "notepad";
        await TickAsync(rig, 3);

        Assert.Null(new CapRestoreStore(StateDir).Read());
    }

    [Fact]
    public async Task A_cap_the_user_picked_mid_game_is_not_undone_by_a_restart()
    {
        var rig = Build("steam", EldenRing with { FrameCapFps = 30 }, capStore: new CapRestoreStore(StateDir));
        rig.Agent.Report(CapReport(45, DateTimeOffset.UtcNow));
        rig.Fg.Proc = "eldenring";
        await TickAsync(rig, 3);
        rig.Gpu.RequestFrameCap(40, DateTimeOffset.UtcNow);   // the panel, mid-game: the cap is theirs now
        await TickAsync(rig, 1);

        var (games, gpu, _) = Restarted();
        games.RecoverPendingCap();

        Assert.False(gpu.Requested);
    }

    [Fact]
    public async Task A_request_made_since_the_start_wins_over_the_replay()
    {
        var rig = Build("steam", EldenRing with { FrameCapFps = 30 }, capStore: new CapRestoreStore(StateDir));
        rig.Agent.Report(CapReport(45, DateTimeOffset.UtcNow));
        rig.Fg.Proc = "eldenring";
        await TickAsync(rig, 3);

        var (games, gpu, _) = Restarted();
        gpu.RequestFrameCap(50, DateTimeOffset.UtcNow);
        games.RecoverPendingCap();

        Assert.Equal(50, gpu.FrameCapFps);
        Assert.Null(new CapRestoreStore(StateDir).Read());
    }

    [Fact]
    public async Task The_replay_keeps_the_auto_fps_rule()
    {
        var rig = Build("steam", EldenRing with { FrameCapFps = 60 }, capStore: new CapRestoreStore(StateDir));
        rig.Agent.Report(CapReport(45, DateTimeOffset.UtcNow));
        rig.Fg.Proc = "eldenring";
        await TickAsync(rig, 3);

        var (games, gpu, autoFps) = Restarted();
        autoFps.Enabled = true;
        autoFps.TargetFps = 55;   // 45 under a 55 target is the pairing that runs the machine hot
        games.RecoverPendingCap();

        Assert.True(gpu.Requested);
        Assert.Null(gpu.FrameCapFps);
    }

    [Fact]
    public async Task A_clean_shutdown_ends_the_profile_but_keeps_the_record_for_the_next_start()
    {
        // The daemon is going away, so the agent may never read the restore End asks for: the record
        // stays, and the next start replays it (the same value twice is harmless).
        var rig = Build("steam", EldenRing with { FrameCapFps = 30 }, capStore: new CapRestoreStore(StateDir));
        rig.Agent.Report(CapReport(45, DateTimeOffset.UtcNow));
        rig.Fg.Proc = "eldenring";
        await TickAsync(rig, 3);
        var games = rig.Games;

        games.Shutdown(rig.Mode.Active);

        Assert.Null(games.Rule);
        Assert.Null(rig.Active.Current);
        Assert.Equal(45, rig.Gpu.FrameCapFps);
        Assert.Equal(45, new CapRestoreStore(StateDir).Read()?.Cap);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"cap":-5,"unknown":false}""")]
    [InlineData("""{"cap":5000,"unknown":false}""")]
    public void A_corrupt_or_implausible_record_is_never_replayed(string body)
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(Path.Combine(StateDir, CapRestoreStore.FileName), body);
        Assert.Null(new CapRestoreStore(StateDir).Read());
    }
}
