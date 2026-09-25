// GPD Forge — POST /tdp racing POST /mode. GPL-3.0-or-later.
//
// Audit round 2 (2026-09-24): POST /tdp read the mode, recorded the override and queued for the write
// gate as separate steps. A mode switch landing in between wrote its preset first, and then the queued
// manual write — built from the OLD mode — landed on top of it and was kept by the 30 s reassert while
// GET /tdp said there was no override. These pin that the manual write stands down instead.
using GpdForge.Api;
using GpdForge.Broker;
using GpdForge.Profiles;
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

public class ManualTdpWriteTests
{
    private sealed record Rig(FakeSilicon Silicon, SerializedTdpController Tdp, TdpState State,
        ModeState Mode, TdpIntent Intent, ProfileApplier Applier);

    private static Rig Build()
    {
        var silicon = new FakeSilicon();
        var state = new TdpState();
        var tdp = new SerializedTdpController(new AuditingTdpController(
            new ClosedLoopTdpController(silicon, new NoWait()), new HardwareAuditLog(), state, "test"), state);
        var intent = new TdpIntent();
        return new Rig(silicon, tdp, state, new ModeState(), intent,
            new ProfileApplier(tdp, new SwitchableDetector(), intent: intent, state: state));
    }

    [Fact]
    public async Task A_manual_write_with_nothing_in_the_way_is_written_and_remembered()
    {
        var rig = Build();

        var r = await ManualTdpWrite.ApplyAsync(rig.Tdp, rig.Mode, rig.Intent, 12, CancellationToken.None);

        Assert.NotNull(r);
        Assert.Equal(TdpIntent.ManualProfile(12, "windows"), rig.State.Last!.Value.Requested);
        Assert.Equal(TdpOwner.Manual, rig.State.Last!.Value.Owner);
        Assert.Equal(12, rig.Intent.Manual("windows")!.Value.StapmW);
    }

    [Fact]
    public async Task A_mode_switch_that_lands_while_the_manual_write_queues_wins()
    {
        var rig = Build();
        var ct = CancellationToken.None;
        await rig.Applier.ApplyAsync("windows", ct);

        // Something already holds the gate (a closed loop mid-flight), so POST /tdp queues behind it.
        var release = new TaskCompletionSource();
        rig.Silicon.HoldApply = release.Task;
        var blocker = rig.Tdp.ApplyAsync(ModeProfiles.For("windows")!.Value, TdpOwner.Mode, ct);
        var manual = ManualTdpWrite.ApplyAsync(rig.Tdp, rig.Mode, rig.Intent, 10, ct);
        Assert.False(manual.IsCompleted);

        // POST /mode arrives now: the new mode, the override ended, its preset queued too.
        rig.Mode.Active = "gaming";
        var modeApply = rig.Applier.ApplyAsync("gaming", ct);

        rig.Silicon.HoldApply = null;
        release.SetResult();
        await blocker;

        Assert.Null(await manual);   // stood down: nothing written from the old mode
        await modeApply;
        var gaming = ModeProfiles.For("gaming")!.Value;
        Assert.Equal(gaming, rig.State.Last!.Value.Requested);
        Assert.Equal(TdpOwner.Mode, rig.State.Last!.Value.Owner);
        Assert.Null(rig.Intent.Manual("gaming"));
        Assert.Equal(new TdpReadout(gaming.StapmW, gaming.FastW), rig.Silicon.Limits);
    }

    [Fact]
    public async Task A_later_manual_write_supersedes_an_earlier_one_still_queued()
    {
        var rig = Build();
        var ct = CancellationToken.None;
        var release = new TaskCompletionSource();
        rig.Silicon.HoldApply = release.Task;
        var blocker = rig.Tdp.ApplyAsync(ModeProfiles.For("windows")!.Value, TdpOwner.Mode, ct);

        var first = ManualTdpWrite.ApplyAsync(rig.Tdp, rig.Mode, rig.Intent, 10, ct);
        var second = ManualTdpWrite.ApplyAsync(rig.Tdp, rig.Mode, rig.Intent, 14, ct);

        rig.Silicon.HoldApply = null;
        release.SetResult();
        await blocker;

        Assert.Null(await first);
        Assert.NotNull(await second);
        Assert.Equal(14, rig.State.Last!.Value.Requested.StapmW);
    }
}
