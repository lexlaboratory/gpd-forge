// GPD Forge — the 30 s TDP reassert: read back, and write only what moved. GPL-3.0-or-later.
//
// MotionAssistant keeps its limits by re-applying them blind every 30 s. That is two SMU writes a
// minute whether or not anything changed, and it cannot say whether the firmware ever took them.
// GPD Forge's closed loop verifies each write once, but until 2026-09-24 nothing noticed a limit that
// moved LATER — the EC/firmware resetting STAPM after a dock event, or another tool's write. These
// tests pin the replacement: read `ryzenadj --info` every 30 s, compare against what GPD Forge last
// wrote, and write only on a difference, never on a failed read and never over a rival controller.
using GpdForge.Broker;
using GpdForge.Profiles;
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

/// <summary>
/// A stand-in for the SMU: writes land in <see cref="Limits"/>, reads report them back. A test can
/// move the limits behind the daemon's back (the firmware revert) or make the read fail.
/// </summary>
public sealed class FakeSilicon : ITdpBackend
{
    private readonly Lock _gate = new();
    private TdpReadout _limits = new(null, null);
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public bool Unreadable { get; set; }
    public bool ThrowOnRead { get; set; }
    /// <summary>Called inside a read, after it is counted — lets a test race a write against it.</summary>
    public Action? DuringRead { get; set; }

    public TdpReadout Limits { get { lock (_gate) return _limits; } set { lock (_gate) _limits = value; } }

    public Task ApplyAsync(TdpProfile profile, CancellationToken ct)
    {
        lock (_gate) { Writes++; _limits = new TdpReadout(profile.StapmW, profile.FastW); }
        return Task.CompletedTask;
    }

    public Task<TdpReadout> ReadAsync(CancellationToken ct)
    {
        lock (_gate) Reads++;
        if (ThrowOnRead) throw new System.ComponentModel.Win32Exception(2, "ryzenadj.exe not found");
        DuringRead?.Invoke();
        return Task.FromResult(Unreadable ? new TdpReadout(null, null) : Limits);
    }
}

public sealed class NoWait : IDelay
{
    public Task WaitAsync(TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
}

public sealed class SwitchableDetector : IPowerControllerDetector
{
    public bool Rival { get; set; }
    public bool OthersRunning(out string[] names)
    {
        names = Rival ? ["MotionAssistant"] : [];
        return Rival;
    }
}

public class TdpReasserterTests
{
    private static readonly TdpProfile Windows = new(15, 20, 17, 92);

    private sealed record Rig(
        FakeSilicon Silicon, ITdpController Tdp, TdpState State, SwitchableDetector Detector,
        ManualTimeProvider Clock, TdpReasserter Reasserter);

    /// <summary>The production stack over a fake SMU: audit decorator → closed loop → backend, so the
    /// owner and the TdpState record come from the real code rather than from the test.</summary>
    private static Rig Build()
    {
        var silicon = new FakeSilicon();
        var state = new TdpState();
        var tdp = new AuditingTdpController(
            new ClosedLoopTdpController(silicon, new NoWait()), new HardwareAuditLog(), state, "test");
        var detector = new SwitchableDetector();
        var clock = new ManualTimeProvider();
        return new Rig(silicon, tdp, state, detector, clock,
            new TdpReasserter(silicon, tdp, state, detector, clock));
    }

    [Fact]
    public async Task With_nothing_written_there_is_nothing_to_reassert_and_nothing_is_read()
    {
        // A daemon that yielded at startup owns no limit. Adopting whatever the hardware says, or
        // writing the mode preset "because nothing else did", would be taking over from a rival.
        var rig = Build();

        Assert.Equal(ReassertOutcome.NothingOwned, await rig.Reasserter.ReassertAsync(CancellationToken.None));
        Assert.Equal(0, rig.Silicon.Reads);
        Assert.Equal(0, rig.Silicon.Writes);
    }

    [Fact]
    public async Task A_limit_that_still_holds_is_read_and_left_alone()
    {
        var rig = Build();
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, CancellationToken.None);
        int writes = rig.Silicon.Writes, reads = rig.Silicon.Reads;

        var outcome = await rig.Reasserter.ReassertAsync(CancellationToken.None);

        Assert.Equal(ReassertOutcome.Holding, outcome);
        Assert.Equal(reads + 1, rig.Silicon.Reads);   // one `ryzenadj --info`
        Assert.Equal(writes, rig.Silicon.Writes);     // and no write: never a blind re-apply
        Assert.Equal(TdpOwner.Mode, rig.State.Last!.Value.Owner);
    }

    [Fact]
    public async Task A_limit_that_moved_is_written_back_and_recorded_as_a_reassert()
    {
        var rig = Build();
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Manual, CancellationToken.None);
        rig.Silicon.Limits = new TdpReadout(25, 30);   // the firmware put its own limit back

        var outcome = await rig.Reasserter.ReassertAsync(CancellationToken.None);

        Assert.Equal(ReassertOutcome.Reasserted, outcome);
        Assert.Equal(new TdpReadout(15, 20), rig.Silicon.Limits);
        var last = rig.State.Last!.Value;
        Assert.Equal(TdpOwner.Reassert, last.Owner);
        Assert.Equal(Windows, last.Requested);   // the SAME profile, not the preset of anything
        Assert.True(last.Verified);
    }

    [Fact]
    public async Task A_one_watt_difference_is_inside_the_closed_loops_tolerance_and_holds()
    {
        // The same rule the write was verified by. A stricter one here would re-write forever a
        // limit the closed loop had already accepted.
        var rig = Build();
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, CancellationToken.None);
        rig.Silicon.Limits = new TdpReadout(16, 19);
        int writes = rig.Silicon.Writes;

        Assert.Equal(ReassertOutcome.Holding, await rig.Reasserter.ReassertAsync(CancellationToken.None));
        Assert.Equal(writes, rig.Silicon.Writes);
    }

    [Fact]
    public async Task A_failed_read_is_not_a_difference_and_writes_nothing()
    {
        // "Could not read it" is not "it moved". Writing on a null readback would be exactly the
        // blind re-apply this replaces — on the one occasion there is least reason to trust it.
        var rig = Build();
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, CancellationToken.None);
        rig.Silicon.Unreadable = true;
        int writes = rig.Silicon.Writes;

        Assert.Equal(ReassertOutcome.Unreadable, await rig.Reasserter.ReassertAsync(CancellationToken.None));
        Assert.Equal(writes, rig.Silicon.Writes);
    }

    [Fact]
    public async Task A_readback_that_throws_is_unreadable_and_does_not_escape()
    {
        // The reassert runs inside ForgeWorker's tick, next to the thermal guardian. A ryzenadj that
        // cannot launch every 30 s must cost the reassert, never the loop.
        var rig = Build();
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, CancellationToken.None);
        rig.Silicon.ThrowOnRead = true;
        int writes = rig.Silicon.Writes;

        Assert.Equal(ReassertOutcome.Unreadable, await rig.Reasserter.ReassertAsync(CancellationToken.None));
        Assert.Equal(writes, rig.Silicon.Writes);
    }

    [Fact]
    public async Task A_half_read_is_also_unreadable()
    {
        var rig = Build();
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, CancellationToken.None);
        rig.Silicon.Limits = new TdpReadout(25, null);
        int writes = rig.Silicon.Writes;

        Assert.Equal(ReassertOutcome.Unreadable, await rig.Reasserter.ReassertAsync(CancellationToken.None));
        Assert.Equal(writes, rig.Silicon.Writes);
    }

    [Fact]
    public async Task While_a_rival_controller_runs_nothing_is_read_or_written()
    {
        // Two controllers applying TDP collapse the device (field-confirmed; see
        // PowerControllerDetector.cs). MotionAssistant re-applies its own limits every 30 s; a
        // reassert here would be the second controller.
        var rig = Build();
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, CancellationToken.None);
        rig.Silicon.Limits = new TdpReadout(25, 30);
        rig.Detector.Rival = true;
        int writes = rig.Silicon.Writes, reads = rig.Silicon.Reads;

        Assert.Equal(ReassertOutcome.Yielded, await rig.Reasserter.ReassertAsync(CancellationToken.None));
        Assert.Equal(reads, rig.Silicon.Reads);
        Assert.Equal(writes, rig.Silicon.Writes);
    }

    [Fact]
    public async Task A_write_that_lands_during_the_read_wins_over_the_stale_profile()
    {
        // The read takes a ryzenadj process launch. If POST /tdp lands meanwhile, what was "owned"
        // when the read started is no longer what the user wants, and writing it would undo them.
        var rig = Build();
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, CancellationToken.None);
        var manual = new TdpProfile(10, 10, 10, 92);
        rig.Silicon.Limits = new TdpReadout(25, 30);
        rig.Silicon.DuringRead = () =>
        {
            rig.Silicon.DuringRead = null;
            rig.Tdp.ApplyAsync(manual, TdpOwner.Manual, CancellationToken.None).GetAwaiter().GetResult();
            rig.Silicon.Limits = new TdpReadout(25, 30);   // and the firmware moves it again
        };

        Assert.Equal(ReassertOutcome.Superseded, await rig.Reasserter.ReassertAsync(CancellationToken.None));
        Assert.Equal(TdpOwner.Manual, rig.State.Last!.Value.Owner);
        Assert.Equal(manual, rig.State.Last!.Value.Requested);
    }

    [Fact]
    public async Task A_limit_the_firmware_will_not_take_is_reported_as_not_held()
    {
        var rig = Build();
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, CancellationToken.None);
        var stubborn = new StubbornSilicon(new TdpReadout(25, 30));
        var state = rig.State;
        var tdp = new AuditingTdpController(new ClosedLoopTdpController(stubborn, new NoWait()),
            new HardwareAuditLog(), state, "test");
        var reasserter = new TdpReasserter(stubborn, tdp, state, rig.Detector, rig.Clock);

        Assert.Equal(ReassertOutcome.NotHeld, await reasserter.ReassertAsync(CancellationToken.None));
        Assert.Equal(TdpOwner.Reassert, state.Last!.Value.Owner);
        Assert.False(state.Last!.Value.Verified);
    }

    private sealed class StubbornSilicon(TdpReadout stuckAt) : ITdpBackend
    {
        public Task ApplyAsync(TdpProfile profile, CancellationToken ct) => Task.CompletedTask;
        public Task<TdpReadout> ReadAsync(CancellationToken ct) => Task.FromResult(stuckAt);
    }

    // ---------------------------------------------------------------------------------------------
    // Cadence
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_check_runs_every_thirty_seconds_and_not_between()
    {
        var rig = Build();
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, CancellationToken.None);
        int reads = rig.Silicon.Reads;

        rig.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Null(await rig.Reasserter.ReassertIfDueAsync(CancellationToken.None));
        Assert.Equal(reads, rig.Silicon.Reads);

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ReassertOutcome.Holding, await rig.Reasserter.ReassertIfDueAsync(CancellationToken.None));
        Assert.Equal(reads + 1, rig.Silicon.Reads);

        // Re-armed from the check, not from the start: the next one is another 30 s away.
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(await rig.Reasserter.ReassertIfDueAsync(CancellationToken.None));
        rig.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.NotNull(await rig.Reasserter.ReassertIfDueAsync(CancellationToken.None));
        Assert.Equal(reads + 2, rig.Silicon.Reads);
    }

    [Fact]
    public void The_interval_is_thirty_seconds()
    {
        // The cadence MotionAssistant uses, so a limit it would have held is held as well — with a
        // read instead of a write in the common case.
        Assert.Equal(TimeSpan.FromSeconds(30), TdpReasserter.DefaultInterval);
    }
}
