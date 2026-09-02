// GPD Forge — Agents/AI mode tests: anti-standby ref-count, sustained profile shaping, VRAM
// advisory, and the job-flow wiring. GPL-3.0-or-later.
using GpdForge.Ai;
using GpdForge.Api;
using Xunit;

namespace GpdForge.Core.Tests;

public class AntiStandbyServiceTests
{
    private sealed class FakeSink : IExecutionStateSink
    {
        public int Engaged { get; private set; }
        public int Released { get; private set; }
        public void Engage() => Engaged++;
        public void Release() => Released++;
    }

    [Fact]
    public void Starts_disengaged_with_zero_holders()
    {
        var svc = new AntiStandbyService(new FakeSink());
        Assert.Equal(0, svc.HolderCount);
        Assert.False(svc.Active);
    }

    [Fact]
    public void First_start_engages_the_sink_0_to_1()
    {
        var sink = new FakeSink();
        var svc = new AntiStandbyService(sink);

        int n = svc.Start();

        Assert.Equal(1, n);
        Assert.Equal(1, svc.HolderCount);
        Assert.True(svc.Active);
        Assert.Equal(1, sink.Engaged);
        Assert.Equal(0, sink.Released);
    }

    [Fact]
    public void Concurrent_holds_only_engage_once()
    {
        var sink = new FakeSink();
        var svc = new AntiStandbyService(sink);

        svc.Start(); svc.Start(); svc.Start();

        Assert.Equal(3, svc.HolderCount);
        Assert.Equal(1, sink.Engaged);   // still just the one 0->1 transition
    }

    [Fact]
    public void Last_stop_releases_the_sink_1_to_0()
    {
        var sink = new FakeSink();
        var svc = new AntiStandbyService(sink);
        svc.Start(); svc.Start();

        svc.Stop();
        Assert.Equal(1, svc.HolderCount);
        Assert.Equal(0, sink.Released);   // one holder remains -> not released yet

        svc.Stop();
        Assert.Equal(0, svc.HolderCount);
        Assert.False(svc.Active);
        Assert.Equal(1, sink.Released);   // released exactly once, on the last release
    }

    [Fact]
    public void Stop_below_zero_is_a_harmless_noop()
    {
        var sink = new FakeSink();
        var svc = new AntiStandbyService(sink);

        int n = svc.Stop();

        Assert.Equal(0, n);
        Assert.Equal(0, sink.Released);
        Assert.Equal(0, sink.Engaged);
    }

    [Fact]
    public void Reengages_after_dropping_to_zero_and_starting_again()
    {
        var sink = new FakeSink();
        var svc = new AntiStandbyService(sink);
        svc.Start();
        svc.Stop();

        svc.Start();

        Assert.Equal(2, sink.Engaged);
        Assert.Equal(1, sink.Released);
    }
}

/// <summary>
/// The regression suite for the thread-affinity bug (2026-08-29). SetThreadExecutionState records
/// its request against the CALLING THREAD, so an Engage taken on one pool thread and a Release
/// issued from another leaves the machine awake forever with a ref count that reads zero. These
/// tests pin the contract that fixes it: whatever thread a caller uses, the sink underneath is only
/// ever touched from one single long-lived thread.
///
/// ⚠️ These tests drive <see cref="OwnerThreadExecutionStateSink"/> with a FAKE inner sink, so by
/// themselves they prove the pump works — not that the shipping sink uses it. A reviewer caught that
/// gap on 2026-08-29: reverting <see cref="Win32ExecutionStateSink"/> to a direct P/Invoke, while
/// leaving this public class in place, kept all 786 tests green. This repo has twice shipped a defect
/// behind tests that only proved their own stub, so the binding is asserted separately in
/// <see cref="Win32ExecutionStateSinkBindingTests"/>. Do not delete that as redundant — it is the only
/// thing standing between a refactor and the overnight battery drain.
/// Zero real P/Invoke here — the inner sink is a fake that only records thread ids.
/// </summary>
public class OwnerThreadExecutionStateSinkTests
{
    /// <summary>Records which managed thread each call arrived on, and blocks until it has seen enough.</summary>
    private sealed class ThreadRecordingSink : IExecutionStateSink
    {
        private readonly object _gate = new();
        private readonly List<(string Call, int ThreadId)> _calls = [];
        public bool FailEngage { get; set; }

        public IReadOnlyList<(string Call, int ThreadId)> Calls { get { lock (_gate) return [.. _calls]; } }

        public void Engage()
        {
            Record("engage");
            if (FailEngage) throw new InvalidOperationException("SetThreadExecutionState returned 0.");
        }

        public void Release() => Record("release");

        private void Record(string call)
        {
            lock (_gate)
            {
                _calls.Add((call, Environment.CurrentManagedThreadId));
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>Waits for at least <paramref name="count"/> calls. Returns false on timeout.</summary>
        public bool WaitForCalls(int count, int millis = 5000)
        {
            var deadline = Environment.TickCount64 + millis;
            lock (_gate)
            {
                while (_calls.Count < count)
                {
                    var left = (int)(deadline - Environment.TickCount64);
                    if (left <= 0) return false;
                    Monitor.Wait(_gate, left);
                }
                return true;
            }
        }
    }

    /// <summary>Runs an action on a brand-new thread and waits for it, so the caller thread differs.</summary>
    private static int RunOnFreshThread(Action action)
    {
        int id = 0;
        var t = new Thread(() => { id = Environment.CurrentManagedThreadId; action(); }) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(5)), "helper thread did not finish");
        return id;
    }

    [Fact]
    public void Engage_and_release_land_on_the_same_thread_even_from_different_callers()
    {
        var inner = new ThreadRecordingSink();
        using var sink = new OwnerThreadExecutionStateSink(inner, "test-owner");

        int engagerThread = RunOnFreshThread(sink.Engage);
        Assert.True(inner.WaitForCalls(1), "engage never reached the inner sink");

        int releaserThread = RunOnFreshThread(sink.Release);
        Assert.True(inner.WaitForCalls(2), "release never reached the inner sink");

        var calls = inner.Calls;
        Assert.Equal(new[] { "engage", "release" }, calls.Select(c => c.Call).ToArray());

        // The whole point: one owner thread took the request and the same one dropped it...
        Assert.Equal(calls[0].ThreadId, calls[1].ThreadId);
        Assert.Equal(sink.OwnerThreadId, calls[0].ThreadId);

        // ...and it is emphatically NOT the caller's thread, which is what a direct P/Invoke would give.
        // (engagerThread vs releaserThread is deliberately NOT compared: both threads are dead by
        // now and managed thread ids get recycled. The owner thread is alive, so its id cannot be.)
        Assert.NotEqual(engagerThread, calls[0].ThreadId);
        Assert.NotEqual(releaserThread, calls[1].ThreadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, calls[0].ThreadId);
    }

    [Fact]
    public void Many_concurrent_callers_all_land_on_the_one_owner_thread()
    {
        var inner = new ThreadRecordingSink();
        using var sink = new OwnerThreadExecutionStateSink(inner, "test-owner");

        // 16 threads hammering on/off concurrently. Whatever interleaving wins, every observed call
        // must have happened on the owner thread.
        var threads = Enumerable.Range(0, 16).Select(i => new Thread(() =>
        {
            for (int n = 0; n < 25; n++) { sink.Engage(); sink.Release(); }
        }) { IsBackground = true }).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromSeconds(10)));

        sink.Engage();
        Assert.True(inner.WaitForCalls(1));

        Assert.NotEmpty(inner.Calls);
        Assert.All(inner.Calls, c => Assert.Equal(sink.OwnerThreadId, c.ThreadId));
    }

    [Fact]
    public void Release_before_any_engage_never_touches_the_inner_sink()
    {
        var inner = new ThreadRecordingSink();
        using var sink = new OwnerThreadExecutionStateSink(inner, "test-owner");

        sink.Release();
        sink.Release();

        // Nothing was ever in force, so there is nothing to drop. Issuing a bare ES_CONTINUOUS here
        // is exactly the call that used to clear the wrong thread's (empty) requirement.
        Assert.False(inner.WaitForCalls(1, millis: 250));
        Assert.Empty(inner.Calls);
        Assert.False(sink.Engaged);
    }

    [Fact]
    public void Repeated_engage_collapses_to_one_call()
    {
        var inner = new ThreadRecordingSink();
        using var sink = new OwnerThreadExecutionStateSink(inner, "test-owner");

        sink.Engage();
        Assert.True(inner.WaitForCalls(1));
        sink.Engage();
        sink.Engage();

        Assert.False(inner.WaitForCalls(2, millis: 250));
        Assert.Single(inner.Calls);
        Assert.True(sink.Engaged);
    }

    [Fact]
    public void Dispose_drops_a_held_request_instead_of_orphaning_it()
    {
        var inner = new ThreadRecordingSink();
        var sink = new OwnerThreadExecutionStateSink(inner, "test-owner");
        sink.Engage();
        Assert.True(inner.WaitForCalls(1));

        sink.Dispose();

        // Dispose joins the owner thread, so by the time it returns the release has already happened
        // on that same thread — no wait needed and no orphaned hold.
        var calls = inner.Calls;
        Assert.Equal(new[] { "engage", "release" }, calls.Select(c => c.Call).ToArray());
        Assert.Equal(calls[0].ThreadId, calls[1].ThreadId);
        Assert.False(sink.Engaged);
    }

    [Fact]
    public void Dispose_with_nothing_held_releases_nothing()
    {
        var inner = new ThreadRecordingSink();
        var sink = new OwnerThreadExecutionStateSink(inner, "test-owner");

        sink.Dispose();

        Assert.Empty(inner.Calls);
    }

    [Fact]
    public void Calls_after_dispose_are_ignored_rather_than_taking_an_undroppable_hold()
    {
        var inner = new ThreadRecordingSink();
        var sink = new OwnerThreadExecutionStateSink(inner, "test-owner");
        sink.Dispose();

        sink.Engage();
        sink.Release();
        sink.Dispose();   // idempotent

        Assert.False(inner.WaitForCalls(1, millis: 250));
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public void A_failed_engage_is_not_reported_as_a_hold()
    {
        var inner = new ThreadRecordingSink { FailEngage = true };
        using var sink = new OwnerThreadExecutionStateSink(inner, "test-owner");

        sink.Engage();
        Assert.True(inner.WaitForCalls(1));

        // The OS refused. Saying "engaged" here is the dishonesty that lets a hold that was never
        // taken read as taken.
        Assert.False(sink.Engaged);

        // And it must not spin retrying the call that just failed.
        Assert.False(inner.WaitForCalls(2, millis: 500));
        Assert.Single(inner.Calls);
    }

    [Fact]
    public void The_owner_thread_is_a_background_thread_and_survives_between_holds()
    {
        var inner = new ThreadRecordingSink();
        using var sink = new OwnerThreadExecutionStateSink(inner, "test-owner");

        sink.Engage();
        Assert.True(inner.WaitForCalls(1));
        sink.Release();
        Assert.True(inner.WaitForCalls(2));
        sink.Engage();
        Assert.True(inner.WaitForCalls(3));

        // Same thread across a full drop to zero and back — a pool thread could have been retired,
        // taking the kernel's execution-state request with it.
        Assert.All(inner.Calls, c => Assert.Equal(sink.OwnerThreadId, c.ThreadId));
    }

    /// <summary>
    /// A structural guard: the real sink must own a disposable owner thread. A revert to the old
    /// "P/Invoke straight from the caller" sink has nothing to dispose and trips this.
    /// </summary>
    [Fact]
    public void The_real_sink_is_disposable_so_shutdown_can_drop_the_request()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(Win32ExecutionStateSink)));
    }
}

public class ProfileShaperTests
{
    [Fact]
    public void Produces_a_flat_profile_with_no_boost_above_stapm()
    {
        var p = ProfileShaper.Shape(20, 90);
        Assert.Equal(20, p.StapmW);
        Assert.Equal(20, p.FastW);
        Assert.Equal(20, p.SlowW);
        Assert.Equal(90, p.TctlC);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1000)]
    public void Clamps_watts_into_the_safe_band(int targetW)
    {
        var p = ProfileShaper.Shape(targetW, 90);
        Assert.InRange(p.StapmW, ProfileShaper.MinW, ProfileShaper.MaxW);
        Assert.Equal(p.StapmW, p.FastW);
        Assert.Equal(p.StapmW, p.SlowW);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    public void Clamps_tctl_into_the_safe_band(int tctlC)
    {
        var p = ProfileShaper.Shape(20, tctlC);
        Assert.InRange(p.TctlC, ProfileShaper.MinTctlC, ProfileShaper.MaxTctlC);
    }

    [Fact]
    public void Is_always_flat_regardless_of_input()
    {
        for (int w = -10; w <= 60; w += 7)
        {
            var p = ProfileShaper.Shape(w, 90);
            Assert.Equal(p.StapmW, p.FastW);
            Assert.Equal(p.StapmW, p.SlowW);
        }
    }
}

public class VramAdvisorTests
{
    [Fact]
    public void Reports_unavailable_when_there_is_nothing_to_read()
    {
        var v = VramAdvisor.FromAdapterRam(null, null);
        Assert.False(v.Available);
        Assert.Equal(0, v.ReportedBytes);
        Assert.Contains("BIOS", v.Advisory);
    }

    [Fact]
    public void Reports_unavailable_for_a_non_positive_reading()
    {
        Assert.False(VramAdvisor.FromAdapterRam(0, "AMD Radeon 890M").Available);
        Assert.False(VramAdvisor.FromAdapterRam(-1, "AMD Radeon 890M").Available);
    }

    [Fact]
    public void Converts_bytes_to_megabytes_and_keeps_the_adapter_name()
    {
        var v = VramAdvisor.FromAdapterRam(4L * 1024 * 1024 * 1024, "AMD Radeon 890M");
        Assert.True(v.Available);
        Assert.Equal(4096, v.ReportedMb);
        Assert.Equal("AMD Radeon 890M", v.AdapterName);
    }

    [Fact]
    public void Advisory_always_mentions_bios_and_reboot_when_available()
    {
        var v = VramAdvisor.FromAdapterRam(1024L * 1024 * 1024, "AMD Radeon 890M");
        Assert.Contains("BIOS", v.Advisory);
        Assert.Contains("reboot", v.Advisory, StringComparison.OrdinalIgnoreCase);
    }

    // --- the bit-reinterpretation helper on the real (WMI-backed) reader ---

    [Fact]
    public void ReadAdapterRamBytes_passes_through_a_boxed_uint()
    {
        object boxed = 2147483648u; // 2 GiB, top bit set
        Assert.Equal(2147483648L, WmiVramReader.ReadAdapterRamBytes(boxed));
    }

    [Fact]
    public void ReadAdapterRamBytes_reinterprets_a_negative_boxed_int_as_unsigned()
    {
        // -1073741824 as Int32 == 0xC0000000 == 3 GiB reinterpreted as uint32.
        object boxed = -1073741824;
        Assert.Equal(3221225472L, WmiVramReader.ReadAdapterRamBytes(boxed));
    }

    [Fact]
    public void ReadAdapterRamBytes_passes_through_a_small_positive_int()
    {
        object boxed = 536870912; // 512 MiB, fits in a positive int32
        Assert.Equal(536870912L, WmiVramReader.ReadAdapterRamBytes(boxed));
    }

    [Fact]
    public void ReadAdapterRamBytes_is_null_for_null_or_unrecognized_types()
    {
        Assert.Null(WmiVramReader.ReadAdapterRamBytes(null));
        Assert.Null(WmiVramReader.ReadAdapterRamBytes("not a number"));
    }
}

/// <summary>Wires anti-standby into the job queue: a running job holds the keep-awake lock; a
/// blocked job does not (nothing to keep the machine awake for); finishing a job releases its
/// hold.</summary>
public class JobsStateAntiStandbyTests
{
    private sealed class FakeSink : IExecutionStateSink
    {
        public int Engaged { get; private set; }
        public int Released { get; private set; }
        public void Engage() => Engaged++;
        public void Release() => Released++;
    }

    [Fact]
    public void Adding_a_running_job_does_NOT_engage_anti_standby()
    {
        // Inverted on 2026-09-02, and the old assertion is why this is worth spelling out: this test
        // used to require the opposite, so it PINNED THE BUG. JobsState.Add engaged a hold, and the
        // only release is Finish, which nothing calls — so one POST /jobs kept Windows awake for the
        // rest of the service's uptime and silently defeated the Standby Doctor.
        //
        // Observed on the live daemon before the change: holders 0 -> 1, still 1 after twenty
        // seconds, and POST /ai/anti-standby {enable:false} could not clear it.
        //
        // A hold is only taken where a release is guaranteed. Today that is the manual toggle.
        var sink = new FakeSink();
        var anti = new AntiStandbyService(sink);
        var jobs = new JobsState(anti);

        jobs.Add("infer batch", null, "running");

        Assert.Equal(0, anti.HolderCount);
        Assert.Equal(0, sink.Engaged);
    }

    [Fact]
    public void Adding_a_blocked_job_does_not_engage_anti_standby()
    {
        var sink = new FakeSink();
        var anti = new AntiStandbyService(sink);
        var jobs = new JobsState(anti);

        jobs.Add("infer batch", null, "blocked");

        Assert.Equal(0, anti.HolderCount);
        Assert.Equal(0, sink.Engaged);
    }

    [Fact]
    public void Holds_ref_count_independently_and_only_the_edges_touch_the_sink()
    {
        // Was written against JobsState.Add/Finish, which no longer takes holds (see above). The
        // ref-counting itself is correct, valuable and worth keeping under test — so it is tested
        // where it actually lives instead of through a trigger that was removed. Testing a mechanism
        // through a caller that no longer calls it is how a test ends up asserting nothing.
        var sink = new FakeSink();
        var anti = new AntiStandbyService(sink);

        anti.Start();
        anti.Start();

        Assert.Equal(2, anti.HolderCount);
        Assert.Equal(1, sink.Engaged);   // only the 0->1 edge engages the sink

        anti.Stop();
        Assert.Equal(1, anti.HolderCount);
        Assert.Equal(0, sink.Released);  // a holder remains -> not released yet

        anti.Stop();
        Assert.Equal(0, anti.HolderCount);
        Assert.Equal(1, sink.Released);
    }

    [Fact]
    public void Finishing_a_blocked_job_does_not_touch_anti_standby()
    {
        var sink = new FakeSink();
        var anti = new AntiStandbyService(sink);
        var jobs = new JobsState(anti);

        var job = jobs.Add("infer batch", null, "blocked");
        jobs.Finish(job.Id);

        Assert.Equal(0, sink.Engaged);
        Assert.Equal(0, sink.Released);
    }

    [Fact]
    public void Finish_updates_the_job_status_to_done()
    {
        var anti = new AntiStandbyService(new FakeSink());
        var jobs = new JobsState(anti);

        var job = jobs.Add("infer batch", null, "running");
        jobs.Finish(job.Id);

        Assert.Equal("done", jobs.All.Single(j => j.Id == job.Id).Status);
    }

    [Fact]
    public void Finish_of_an_unknown_id_returns_false()
    {
        var jobs = new JobsState(new AntiStandbyService(new FakeSink()));
        Assert.False(jobs.Finish("job-999"));
    }
}

/// <summary>
/// The guard the thread-affinity tests above cannot provide. They prove
/// <see cref="OwnerThreadExecutionStateSink"/> marshals correctly; nothing in them proves the sink the
/// daemon actually registers goes through it. A reviewer demonstrated on 2026-08-29 that reverting
/// <see cref="Win32ExecutionStateSink"/> to a direct P/Invoke on the caller's thread — leaving the
/// pump class present and its tests passing — kept the entire suite green while restoring the bug.
///
/// Structural assertions are usually a smell. This one earns its place: the defect it guards is
/// invisible in unit tests by construction (the failure is a Win32 request left standing on a retired
/// thread pool thread, which no fake can observe), it already shipped once, and its symptom is a
/// handheld that never sleeps while the UI reports zero holds.
/// </summary>
public class Win32ExecutionStateSinkBindingTests
{
    [Fact]
    public void The_shipping_sink_delegates_to_the_owner_thread_pump()
    {
        // If Engage/Release ever P/Invoke directly again, this field goes away and this fails.
        var pump = typeof(Win32ExecutionStateSink)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SingleOrDefault(f => f.FieldType == typeof(OwnerThreadExecutionStateSink));

        Assert.True(
            pump is not null,
            "Win32ExecutionStateSink must hold an OwnerThreadExecutionStateSink. SetThreadExecutionState "
            + "records its request against the CALLING thread, so engaging and releasing from different "
            + "thread-pool threads leaves the machine awake forever with HolderCount reading 0.");
    }

    [Fact]
    public void The_shipping_sink_is_disposable_so_shutdown_drops_the_request()
    {
        // The owner thread outlives every hold by design, so the request can only be dropped on the
        // way out if something joins that thread. Without IDisposable, DI never asks it to.
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(Win32ExecutionStateSink)));
    }
}
