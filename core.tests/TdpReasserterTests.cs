// GPD Forge — the 30 s TDP reassert: read back, and write only what moved. GPL-3.0-or-later.
//
// MotionAssistant keeps its limits by re-applying them blind every 30 s. That is two SMU writes a
// minute whether or not anything changed, and it cannot say whether the firmware ever took them.
// GPD Forge's closed loop verifies each write once, but until 2026-09-24 nothing noticed a limit that
// moved LATER — the EC/firmware resetting STAPM after a dock event, or another tool's write. These
// tests pin the replacement: read `ryzenadj --info` every 30 s, compare against what GPD Forge last
// wrote, and write only on a difference, never on a failed read and never over a rival controller.
using GpdForge.Api;
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
    /// <summary>When set, writes land on all four limits (slow and Tctl too), as a real PM table
    /// reports them. Off by default so the two-limit readouts the older tests compare stay exact.</summary>
    public bool FullTable { get; set; }
    /// <summary>Called inside a read, after it is counted — lets a test race a write against it.</summary>
    public Action? DuringRead { get; set; }

    public TdpReadout Limits { get { lock (_gate) return _limits; } set { lock (_gate) _limits = value; } }

    /// <summary>When set, a write lands on the limits and then waits here — a closed loop caught
    /// mid-flight, which is how a test holds one write open while another path runs.</summary>
    public Task? HoldApply { get; set; }

    public async Task ApplyAsync(TdpProfile profile, CancellationToken ct)
    {
        lock (_gate)
        {
            Writes++;
            _limits = FullTable
                ? new TdpReadout(profile.StapmW, profile.FastW, profile.SlowW, profile.TctlC)
                : new TdpReadout(profile.StapmW, profile.FastW);
        }
        if (HoldApply is Task hold) await hold;
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
    /// <summary>Called on each check — lets a test start a write between the reassert's ownership
    /// read and its turn at the write gate.</summary>
    public Action? OnCheck { get; set; }
    public bool OthersRunning(out string[] names)
    {
        OnCheck?.Invoke();
        names = Rival ? ["MotionAssistant"] : [];
        return Rival;
    }
}

public class TdpReasserterTests
{
    private static readonly TdpProfile Windows = new(15, 20, 17, 92);

    private sealed record Rig(
        FakeSilicon Silicon, SerializedTdpController Tdp, TdpState State, SwitchableDetector Detector,
        ManualTimeProvider Clock, TdpReasserter Reasserter, ModeState Mode, TdpIntent Intent,
        ProfileApplier Applier);

    /// <summary>The production stack over a fake SMU: write gate → audit decorator → closed loop →
    /// backend, so the owner and the TdpState record come from the real code rather than from the test.</summary>
    private static Rig Build()
    {
        var silicon = new FakeSilicon();
        var state = new TdpState();
        var tdp = new SerializedTdpController(new AuditingTdpController(
            new ClosedLoopTdpController(silicon, new NoWait()), new HardwareAuditLog(), state, "test"), state);
        var detector = new SwitchableDetector();
        var clock = new ManualTimeProvider();
        var mode = new ModeState();
        var intent = new TdpIntent();
        return new Rig(silicon, tdp, state, detector, clock,
            new TdpReasserter(silicon, tdp, state, detector, intent, mode, clock), mode, intent,
            new ProfileApplier(tdp, detector, intent: intent, state: state));
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
    public async Task A_write_that_arrives_during_the_read_waits_for_it_and_lands_last()
    {
        // Audit round 2 (2026-09-24): the read-compare-write is ONE step under the write gate. Before,
        // `ryzenadj --info` ran outside it, so a POST /tdp arriving mid-read launched its own ryzenadj
        // while the read was still talking to the SMU mailbox — two processes interleaving messages on
        // registers with no cross-process lock. Now the write queues behind the check and lands after it.
        var rig = Build();
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, CancellationToken.None);
        var manual = new TdpProfile(10, 10, 10, 92);
        rig.Silicon.Limits = new TdpReadout(25, 30);   // the firmware moved it
        int writesBefore = rig.Silicon.Writes;
        int writesSeenDuringRead = -1;
        Task? pending = null;
        rig.Silicon.DuringRead = () =>
        {
            rig.Silicon.DuringRead = null;
            pending = rig.Tdp.ApplyAsync(manual, TdpOwner.Manual, CancellationToken.None);
            writesSeenDuringRead = rig.Silicon.Writes;
        };

        Assert.Equal(ReassertOutcome.Reasserted, await rig.Reasserter.ReassertAsync(CancellationToken.None));
        Assert.Equal(writesBefore, writesSeenDuringRead);   // queued at the gate, not writing beside the read
        await pending!;

        Assert.Equal(TdpOwner.Manual, rig.State.Last!.Value.Owner);
        Assert.Equal(manual, rig.State.Last!.Value.Requested);
        Assert.Equal(new TdpReadout(10, 10), rig.Silicon.Limits);
    }

    [Fact]
    public async Task A_limit_the_firmware_will_not_take_is_reported_as_not_held()
    {
        var rig = Build();
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, CancellationToken.None);
        var stubborn = new StubbornSilicon(new TdpReadout(25, 30));
        var state = rig.State;
        var tdp = new SerializedTdpController(new AuditingTdpController(
            new ClosedLoopTdpController(stubborn, new NoWait()), new HardwareAuditLog(), state, "test"), state);
        var reasserter = new TdpReasserter(stubborn, tdp, state, rig.Detector, rig.Intent, rig.Mode, rig.Clock);

        Assert.Equal(ReassertOutcome.NotHeld, await reasserter.ReassertAsync(CancellationToken.None));
        Assert.Equal(TdpOwner.Reassert, state.Last!.Value.Owner);
        Assert.False(state.Last!.Value.Verified);
    }

    // ---------------------------------------------------------------------------------------------
    // Ownership after a mode switch that yielded (audit, 2026-09-24)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task After_a_mode_switch_that_yielded_the_previous_modes_profile_is_never_put_back()
    {
        // windows is written; MotionAssistant starts; the user picks gaming, which yields and writes
        // nothing; MotionAssistant exits. The last write is still windows, and re-asserting it would
        // hold 15 W while GET /mode says gaming. What is kept is the mode the user chose.
        var rig = Build();
        var ct = CancellationToken.None;
        Assert.Equal(ApplyOutcome.AppliedVerified, await rig.Applier.ApplyAsync("windows", ct));
        rig.Detector.Rival = true;
        rig.Mode.Active = "gaming";
        Assert.Equal(ApplyOutcome.SkippedConflict, await rig.Applier.ApplyAsync("gaming", ct));
        rig.Silicon.Limits = new TdpReadout(18, 22);   // MotionAssistant's own limits
        rig.Detector.Rival = false;                   // ...and it exits

        var outcome = await rig.Reasserter.ReassertAsync(ct);

        var gaming = ModeProfiles.For("gaming")!.Value;
        Assert.Equal(ReassertOutcome.Reasserted, outcome);
        Assert.Equal(new TdpReadout(gaming.StapmW, gaming.FastW), rig.Silicon.Limits);
        Assert.Equal(gaming, rig.State.Last!.Value.Requested);
        Assert.Equal(TdpOwner.Reassert, rig.State.Last!.Value.Owner);
    }

    [Fact]
    public async Task A_manual_override_the_mode_change_ended_is_not_resurrected()
    {
        // TdpIntent ends the override on every mode apply, yielded or not. The reassert used to bring
        // it back from TdpState.Last once the rival exited.
        var rig = Build();
        var ct = CancellationToken.None;
        await rig.Applier.ApplyAsync("windows", ct);
        var manual = TdpIntent.ManualProfile(10, "windows");
        rig.Intent.SetManual("windows", manual);
        await rig.Tdp.ApplyAsync(manual, TdpOwner.Manual, ct);
        rig.Detector.Rival = true;
        await rig.Applier.ApplyAsync("windows", ct);   // re-picked while GPD Tool runs: yields
        rig.Silicon.Limits = new TdpReadout(22, 28);
        rig.Detector.Rival = false;

        Assert.Equal(ReassertOutcome.Reasserted, await rig.Reasserter.ReassertAsync(ct));

        Assert.Equal(ModeProfiles.For("windows"), rig.State.Last!.Value.Requested);
        Assert.NotEqual(new TdpReadout(10, 10), rig.Silicon.Limits);
    }

    [Fact]
    public async Task After_a_yielded_switch_limits_already_at_the_new_mode_are_left_alone()
    {
        var rig = Build();
        var ct = CancellationToken.None;
        await rig.Applier.ApplyAsync("windows", ct);
        rig.Detector.Rival = true;
        rig.Mode.Active = "gaming";
        await rig.Applier.ApplyAsync("gaming", ct);
        var gaming = ModeProfiles.For("gaming")!.Value;
        rig.Silicon.Limits = new TdpReadout(gaming.StapmW, gaming.FastW);
        rig.Detector.Rival = false;
        int writes = rig.Silicon.Writes;

        Assert.Equal(ReassertOutcome.Holding, await rig.Reasserter.ReassertAsync(ct));
        Assert.Equal(writes, rig.Silicon.Writes);
    }

    // ---------------------------------------------------------------------------------------------
    // Writes still in flight (audit, 2026-09-24): the guard used to see only FINISHED writes
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_write_still_in_its_closed_loop_when_the_check_starts_is_never_overwritten()
    {
        var rig = Build();
        var ct = CancellationToken.None;
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, ct);
        var release = new TaskCompletionSource();
        rig.Silicon.HoldApply = release.Task;
        var manual = new TdpProfile(10, 10, 10, 92);
        var inFlight = rig.Tdp.ApplyAsync(manual, TdpOwner.Manual, ct);   // POST /tdp, mid-loop
        int writes = rig.Silicon.Writes;

        // Answered at once. A check that did not see the write in flight would queue behind it and then
        // write over it; asserted as "not completed" rather than awaited, so that failure is a red test
        // instead of a hang.
        var check = rig.Reasserter.ReassertAsync(ct);
        Assert.True(check.IsCompleted, "the reassert queued to write over a write still in flight");
        Assert.Equal(ReassertOutcome.Superseded, await check);
        Assert.Equal(writes, rig.Silicon.Writes);

        rig.Silicon.HoldApply = null;
        release.SetResult();
        await inFlight;
        Assert.Equal(manual, rig.State.Last!.Value.Requested);
        Assert.Equal(new TdpReadout(10, 10), rig.Silicon.Limits);
    }

    [Fact]
    public async Task The_readback_never_starts_while_a_gated_write_is_talking_to_the_smu()
    {
        // The window the old code left open: the ownership read says "idle", then POST /tdp takes the
        // gate and its closed loop is mid-flight in ryzenadj. The reassert's `--info` used to launch
        // right then. It must wait for the gate, and once the write is done it is superseded — never
        // read beside it, never written over it.
        var rig = Build();
        var ct = CancellationToken.None;
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, ct);
        var manual = new TdpProfile(10, 10, 10, 92);
        var release = new TaskCompletionSource();
        Task? inFlight = null;
        rig.Detector.OnCheck = () =>
        {
            rig.Detector.OnCheck = null;
            rig.Silicon.HoldApply = release.Task;
            inFlight = rig.Tdp.ApplyAsync(manual, TdpOwner.Manual, ct);
        };
        int reads = rig.Silicon.Reads;

        var check = rig.Reasserter.ReassertAsync(ct);
        Assert.False(check.IsCompleted);            // waiting at the gate...
        Assert.Equal(reads, rig.Silicon.Reads);     // ...without having read

        rig.Silicon.HoldApply = null;
        release.SetResult();
        await inFlight!;
        Assert.Equal(ReassertOutcome.Superseded, await check);
        Assert.Equal(reads + 1, rig.Silicon.Reads);   // only the manual write's own verification read

        Assert.Equal(TdpOwner.Manual, rig.State.Last!.Value.Owner);
        Assert.Equal(new TdpReadout(10, 10), rig.Silicon.Limits);
    }

    // ---------------------------------------------------------------------------------------------
    // Slow limit and Tctl (audit round 2, 2026-09-24): the readback compared only STAPM and fast
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_slow_limit_the_firmware_put_back_is_reasserted()
    {
        var rig = Build();
        rig.Silicon.FullTable = true;
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, CancellationToken.None);
        rig.Silicon.Limits = rig.Silicon.Limits with { PptSlowW = 30 };   // only slow moved

        Assert.Equal(ReassertOutcome.Reasserted, await rig.Reasserter.ReassertAsync(CancellationToken.None));
        Assert.Equal(17, rig.Silicon.Limits.PptSlowW);
    }

    [Fact]
    public async Task A_tctl_the_firmware_put_back_is_reasserted()
    {
        var rig = Build();
        rig.Silicon.FullTable = true;
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, CancellationToken.None);
        rig.Silicon.Limits = rig.Silicon.Limits with { TctlC = 100 };

        Assert.Equal(ReassertOutcome.Reasserted, await rig.Reasserter.ReassertAsync(CancellationToken.None));
        Assert.Equal(92, rig.Silicon.Limits.TctlC);
    }

    [Fact]
    public async Task A_missing_slow_or_tctl_reading_is_unknown_not_a_difference()
    {
        // A PM table that does not expose a row reads as null. That is "not measured", and writing on
        // it would be the blind re-apply this class exists to avoid.
        var rig = Build();
        await rig.Tdp.ApplyAsync(Windows, TdpOwner.Mode, CancellationToken.None);   // two-limit table
        int writes = rig.Silicon.Writes;

        Assert.Equal(ReassertOutcome.Holding, await rig.Reasserter.ReassertAsync(CancellationToken.None));
        Assert.Equal(writes, rig.Silicon.Writes);
    }

    [Fact]
    public void Holds_judges_slow_and_tctl_only_when_they_were_read()
    {
        // One rule for the closed loop and the reassert, so "verified" and "holding" cannot disagree
        // and rewrite the same limit every 30 s.
        var want = new TdpProfile(15, 20, 17, 92);
        Assert.True(ClosedLoopTdpController.Holds(new TdpReadout(15, 20, 17, 92), want, 1));
        Assert.True(ClosedLoopTdpController.Holds(new TdpReadout(15, 20, 18, 91), want, 1));   // inside tolerance
        Assert.True(ClosedLoopTdpController.Holds(new TdpReadout(15, 20, null, null), want, 1));
        Assert.False(ClosedLoopTdpController.Holds(new TdpReadout(15, 20, 25, 92), want, 1));
        Assert.False(ClosedLoopTdpController.Holds(new TdpReadout(15, 20, 17, 100), want, 1));
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
