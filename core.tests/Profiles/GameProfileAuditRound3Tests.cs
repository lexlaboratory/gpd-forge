// GPD Forge — F1 audit round 3: whose cap comes back when a game leaves. GPL-3.0-or-later.
//
// The same rig as GameProfileTests. PreviousCap took the daemon's last request whenever there was
// one, and only read the driver when nothing had ever been asked. But the agent re-applies a request
// only when its VALUE changes, so a cap the user set in Adrenalin after it is never corrected back —
// the driver holds the user's, the request holds yesterday's. Measured on the device 2026-09-25:
// GET /gpu/desired {requested:true, frameCapFps:60, requestedAtUtc:2026-09-24T23:21Z} against GET /gpu
// FRTC enabled at 45 with a report from 2026-09-25T13:22Z. Leaving a 30 FPS game then wrote 60.
using GpdForge.Gpu;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed partial class GameProfileTests
{
    private static GpuAgentReport CapReport(int userCap, DateTimeOffset at) =>
        new(true, "Ready", "1.0", "ok",
            new GpuSettingsSnapshot(null, null, null, null,
                new GpuFeatureState(Supported: true, Enabled: true, Value: userCap, Min: 15, Max: 1000)),
            at);

    [Fact]
    public async Task Leaving_restores_the_users_newer_adrenalin_cap_not_an_old_request()
    {
        var rig = Build("steam", EldenRing with { FrameCapFps = 30 });
        var now = DateTimeOffset.UtcNow;
        rig.Gpu.RequestFrameCap(60, now.AddHours(-14));   // carried out long ago...
        rig.Agent.Report(CapReport(45, now));              // ...and since changed by the user in Adrenalin
        await TickAsync(rig, 3);

        rig.Fg.Proc = "eldenring";
        await TickAsync(rig, 3);
        Assert.Equal(30, rig.Gpu.FrameCapFps);

        rig.Fg.Proc = "notepad";
        await TickAsync(rig, 3);

        Assert.True(rig.Gpu.Requested);
        Assert.Equal(45, rig.Gpu.FrameCapFps);
    }

    [Fact]
    public async Task Leaving_restores_a_request_the_agent_has_not_carried_out_yet()
    {
        // Asked a moment ago (the user's pick in the panel); the agent's report predates the tick that
        // applies it, so the driver's 45 is the cap the request is replacing, not a newer one.
        var rig = Build("steam", EldenRing with { FrameCapFps = 30 });
        var now = DateTimeOffset.UtcNow;
        rig.Agent.Report(CapReport(45, now.AddSeconds(-1)));
        rig.Gpu.RequestFrameCap(60, now);
        await TickAsync(rig, 3);

        rig.Fg.Proc = "eldenring";
        await TickAsync(rig, 3);
        rig.Fg.Proc = "notepad";
        await TickAsync(rig, 3);

        Assert.Equal(60, rig.Gpu.FrameCapFps);
    }

    [Theory]
    [InlineData(0, false)]    // the same instant: the report was read before the request existed
    [InlineData(3, false)]    // one tick: the agent posts its reading BEFORE it reconciles
    [InlineData(6, true)]     // two ticks: a reading taken after the request was applied
    [InlineData(-5, false)]
    public void A_request_is_superseded_only_by_a_report_taken_after_the_agent_could_apply_it(int seconds, bool superseded)
    {
        var at = DateTimeOffset.UtcNow;
        Assert.Equal(superseded, GpuDesiredState.SupersededBy(at, at.AddSeconds(seconds)));
    }
}
