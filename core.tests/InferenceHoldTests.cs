// GPD Forge — tests for holding the machine awake for inference we did not start. GPL-3.0-or-later.
//
// The thing under test is a heuristic whose failure mode is a flat battery, so most of what is here
// is about REFUSING to hold: presence without work, a single busy blip, a process that exited, a
// recycled PID, a stepped clock, a sampler that stops working. The guard test at the bottom of the
// engine section exists because the cheap implementation of this feature ("is ollama running?") is
// the same shape as the bug that drained the machine overnight on 2026-08-28, and a future refactor
// must fail loudly if it drifts back toward it.
//
// A NOTE ON WHAT A TEST HERE IS ALLOWED TO ASSERT. Inf.Options() hard-codes EngageTicks/ReleaseTicks
// so the arithmetic in the streak tests is readable, and that is fine for tests ABOUT the mechanism.
// It is not fine for tests about the shipped POLICY: an assertion that release is faster than engage,
// made against numbers the test itself passed in, is Assert.True(2 < 3) wearing a comment. Those
// assertions go against InferenceHoldOptions.FromEnvironment(), which is what Program.cs builds.
//
// Zero real process access: everything goes through fake ProcessCpuSample lists and the same
// FakeSink used by the existing AntiStandbyService tests.
using GpdForge.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GpdForge.Core.Tests;

/// <summary>Shared fixtures. 8 processors and 1-second ticks make the arithmetic readable: at a 0.15
/// threshold a process must burn >= 1.2 s of CPU per tick to count as working.</summary>
internal static class Inf
{
    public const int Cpus = 8;
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    public static readonly DateTimeOffset T0 = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    /// <summary>CPU burned in one tick by a process that is genuinely generating tokens (~2.4 cores).</summary>
    public static readonly TimeSpan BusyPerTick = TimeSpan.FromSeconds(2.0);

    /// <summary>CPU burned in one tick by a resident-but-idle server doing housekeeping (~1 % of the box).</summary>
    public static readonly TimeSpan IdlePerTick = TimeSpan.FromSeconds(0.08);

    public static DateTimeOffset At(int tick) => T0 + Tick * tick;

    public static InferenceHoldOptions Options(bool enforce = true, TimeSpan? maxHold = null) => new(
        WatchedNames: ["ollama"],
        BusyCpuFraction: 0.15,
        EngageTicks: 3,
        ReleaseTicks: 2,
        TickInterval: Tick,
        MaxHold: maxHold,
        ProcessorCount: Cpus,
        Enforce: enforce);

    public static IReadOnlyList<ProcessCpuSample> One(int pid, string name, TimeSpan cpu)
        => [new ProcessCpuSample(pid, name, cpu)];

    public static IReadOnlyList<ProcessCpuSample> None() => [];
}

internal sealed class FakeSink : IExecutionStateSink
{
    public int Engaged { get; private set; }
    public int Released { get; private set; }
    public void Engage() => Engaged++;
    public void Release() => Released++;
}

public class InferenceHoldEngineTests
{
    /// <summary>Runs `count` ticks starting at `fromTick`, adding `perTick` CPU between them, and
    /// returns the last decision plus the CPU total AS LAST SUBMITTED — so a caller can carry the same
    /// process forward by adding its own next increment.</summary>
    private static (InferenceHoldDecision Last, TimeSpan Cpu, int NextTick) Run(
        InferenceHoldEngine engine, int fromTick, int count, TimeSpan cpu, TimeSpan perTick, int pid = 100)
    {
        InferenceHoldDecision last = new(InferenceHoldAction.None, false, [], "unrun");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) cpu += perTick;
            last = engine.Tick(Inf.At(fromTick + i), Inf.One(pid, "ollama", cpu));
        }
        return (last, cpu, fromTick + count);
    }

    [Fact]
    public void First_tick_has_no_baseline_so_it_can_never_engage()
    {
        var engine = new InferenceHoldEngine(Inf.Options());

        var d = engine.Tick(Inf.At(0), Inf.One(100, "ollama", TimeSpan.FromMinutes(30)));

        Assert.Equal(InferenceHoldAction.None, d.Action);
        Assert.False(d.Holding);
        Assert.Empty(d.Workers);
    }

    [Fact]
    public void Engages_only_after_the_full_engage_streak()
    {
        var engine = new InferenceHoldEngine(Inf.Options());
        var cpu = TimeSpan.Zero;

        // Tick 0 establishes the baseline; ticks 1 and 2 are above threshold but short of the streak.
        for (int t = 0; t <= 2; t++)
        {
            var d = engine.Tick(Inf.At(t), Inf.One(100, "ollama", cpu));
            Assert.Equal(InferenceHoldAction.None, d.Action);
            cpu += Inf.BusyPerTick;
        }

        var engaged = engine.Tick(Inf.At(3), Inf.One(100, "ollama", cpu));

        Assert.Equal(InferenceHoldAction.Engage, engaged.Action);
        Assert.True(engaged.Holding);
        Assert.Equal(100, Assert.Single(engaged.Workers).Pid);
    }

    [Fact]
    public void A_single_busy_tick_between_idle_ticks_never_engages()
    {
        var engine = new InferenceHoldEngine(Inf.Options());
        var cpu = TimeSpan.Zero;

        for (int t = 0; t <= 20; t++)
        {
            var d = engine.Tick(Inf.At(t), Inf.One(100, "ollama", cpu));
            Assert.NotEqual(InferenceHoldAction.Engage, d.Action);
            cpu += t % 3 == 0 ? Inf.BusyPerTick : Inf.IdlePerTick;   // one busy tick, then two idle
        }
    }

    [Fact]
    public void Releases_after_the_release_streak_when_the_work_stops()
    {
        var engine = new InferenceHoldEngine(Inf.Options());
        var (_, cpu, next) = Run(engine, 0, 4, TimeSpan.Zero, Inf.BusyPerTick);
        Assert.True(engine.Holding);

        // First idle tick: below threshold once, still short of ReleaseTicks=2.
        cpu += Inf.IdlePerTick;
        var first = engine.Tick(Inf.At(next), Inf.One(100, "ollama", cpu));
        Assert.Equal(InferenceHoldAction.None, first.Action);
        Assert.True(first.Holding);

        cpu += Inf.IdlePerTick;
        var released = engine.Tick(Inf.At(next + 1), Inf.One(100, "ollama", cpu));

        Assert.Equal(InferenceHoldAction.Release, released.Action);
        Assert.False(released.Holding);
        Assert.Empty(released.Workers);
    }

    [Fact]
    public void Releasing_arms_faster_than_engaging_does()
    {
        // The asymmetry IS the safety property: being slow to engage costs a few seconds of a run's
        // protection, being slow to release costs battery. This used to assert it against Inf.Options(),
        // which passes EngageTicks: 3 / ReleaseTicks: 2 in from this very file — so it proved only that
        // 2 < 3 and would have stayed green with shipped defaults of Engage=1/Release=5. It now asserts
        // against the object Program.cs actually constructs, and then against the behaviour that object
        // produces, so neither the constants nor the engine can drift without this failing.
        var shipped = InferenceHoldOptions.FromEnvironment(_ => null);
        Assert.True(shipped.ReleaseTicks < shipped.EngageTicks);

        var engine = new InferenceHoldEngine(
            shipped with { WatchedNames = ["ollama"], ProcessorCount = Inf.Cpus });

        int t = 0;
        var cpu = TimeSpan.Zero;
        engine.Tick(Inf.At(t++), Inf.One(100, "ollama", cpu));   // baseline tick, measures nothing

        int toEngage = 0;
        while (!engine.Holding)
        {
            cpu += Inf.BusyPerTick;
            engine.Tick(Inf.At(t++), Inf.One(100, "ollama", cpu));
            Assert.True(++toEngage < 100, "engine never engaged on unambiguously busy input");
        }

        int toRelease = 0;
        while (engine.Holding)
        {
            cpu += Inf.IdlePerTick;
            engine.Tick(Inf.At(t++), Inf.One(100, "ollama", cpu));
            Assert.True(++toRelease < 100, "engine never released on unambiguously idle input");
        }

        Assert.Equal(shipped.EngageTicks, toEngage);
        Assert.Equal(shipped.ReleaseTicks, toRelease);
        Assert.True(toRelease < toEngage);
    }

    [Fact]
    public void Release_ticks_can_never_be_configured_slower_than_engage_ticks()
    {
        var engine = new InferenceHoldEngine(Inf.Options() with { EngageTicks = 2, ReleaseTicks = 99 });
        var cpu = TimeSpan.Zero;
        for (int t = 0; t <= 2; t++) { engine.Tick(Inf.At(t), Inf.One(100, "ollama", cpu)); cpu += Inf.BusyPerTick; }
        Assert.True(engine.Holding);
        cpu -= Inf.BusyPerTick;   // back to the total last submitted

        // With ReleaseTicks clamped to EngageTicks (2), two idle ticks must be enough.
        cpu += Inf.IdlePerTick;
        engine.Tick(Inf.At(3), Inf.One(100, "ollama", cpu));
        cpu += Inf.IdlePerTick;
        var d = engine.Tick(Inf.At(4), Inf.One(100, "ollama", cpu));

        Assert.Equal(InferenceHoldAction.Release, d.Action);
    }

    [Fact]
    public void Process_exit_releases_immediately_without_waiting_out_the_release_streak()
    {
        var engine = new InferenceHoldEngine(Inf.Options());
        var (_, _, next) = Run(engine, 0, 4, TimeSpan.Zero, Inf.BusyPerTick);
        Assert.True(engine.Holding);

        var d = engine.Tick(Inf.At(next), Inf.None());

        Assert.Equal(InferenceHoldAction.Release, d.Action);
        Assert.False(d.Holding);
        Assert.Contains("exited", d.Reason);
    }

    [Fact]
    public void A_recycled_pid_with_backwards_cpu_time_is_not_read_as_work()
    {
        var engine = new InferenceHoldEngine(Inf.Options());
        engine.Tick(Inf.At(0), Inf.One(100, "ollama", TimeSpan.FromHours(3)));

        // The PID now belongs to a brand-new process whose CPU total is far lower. A naive delta would
        // be hugely negative; a naive abs() would be hugely positive. Neither is a measurement.
        for (int t = 1; t <= 10; t++)
        {
            var d = engine.Tick(Inf.At(t), Inf.One(100, "ollama", TimeSpan.FromSeconds(t * 0.05)));
            Assert.NotEqual(InferenceHoldAction.Engage, d.Action);
        }
    }

    /// <summary>
    /// The half of PID reuse a backwards-CPU check cannot catch: the successor has burned MORE CPU
    /// than its predecessor, so the delta is positive and looks like ordinary work. Only the process
    /// start time distinguishes them — and the predecessor's earned hold, its Busy flag and its
    /// BusySince must not transfer, or a process spawned seconds ago is reported as the reason the
    /// machine has been awake since noon.
    /// </summary>
    [Fact]
    public void A_reused_pid_does_not_inherit_the_hold_its_predecessor_earned()
    {
        var engine = new InferenceHoldEngine(Inf.Options());
        var first = Inf.T0 - TimeSpan.FromHours(1);
        var cpu = TimeSpan.Zero;
        for (int t = 0; t <= 3; t++)
        {
            engine.Tick(Inf.At(t), [new ProcessCpuSample(100, "ollama", cpu, first)]);
            cpu += Inf.BusyPerTick;
        }
        Assert.True(engine.Holding);

        // Same PID, different process, larger CPU total. The predecessor exited; say so.
        var second = Inf.At(4) - TimeSpan.FromSeconds(2);
        var recycled = TimeSpan.FromHours(5);
        var d = engine.Tick(Inf.At(4), [new ProcessCpuSample(100, "ollama", recycled, second)]);

        Assert.Equal(InferenceHoldAction.Release, d.Action);
        Assert.Empty(d.Workers);
        Assert.Contains("exited", d.Reason);

        // The successor must re-earn the hold from scratch, and be credited from ITS first busy tick.
        for (int t = 5; t <= 6; t++)
        {
            recycled += Inf.BusyPerTick;
            Assert.Equal(InferenceHoldAction.None,
                engine.Tick(Inf.At(t), [new ProcessCpuSample(100, "ollama", recycled, second)]).Action);
        }
        recycled += Inf.BusyPerTick;
        var engaged = engine.Tick(Inf.At(7), [new ProcessCpuSample(100, "ollama", recycled, second)]);

        Assert.Equal(InferenceHoldAction.Engage, engaged.Action);
        Assert.Equal(Inf.At(5), Assert.Single(engaged.Workers).BusySince);
    }

    /// <summary>
    /// The other half: no start times available, so reuse can only be inferred from the CPU total
    /// going backwards. That inference used to suppress the FRACTION and leave Busy/BusySince intact,
    /// which is the same lie by a different route.
    /// </summary>
    [Fact]
    public void A_recycled_pid_inferred_from_backwards_cpu_loses_the_streak_not_just_the_baseline()
    {
        var engine = new InferenceHoldEngine(Inf.Options());
        var (_, _, next) = Run(engine, 0, 4, TimeSpan.Zero, Inf.BusyPerTick);
        Assert.True(engine.Holding);
        Assert.Equal(Inf.At(1), Assert.Single(
            engine.Tick(Inf.At(next), Inf.One(100, "ollama", TimeSpan.FromSeconds(12))).Workers).BusySince);

        // Now the PID is recycled onto a process with almost no CPU history.
        var d = engine.Tick(Inf.At(next + 1), Inf.One(100, "ollama", TimeSpan.FromSeconds(0.1)));
        Assert.Equal(InferenceHoldAction.Release, d.Action);
        Assert.Empty(d.Workers);

        var cpu = TimeSpan.FromSeconds(0.1);
        for (int t = next + 2; t <= next + 3; t++)
        {
            cpu += Inf.BusyPerTick;
            Assert.Equal(InferenceHoldAction.None, engine.Tick(Inf.At(t), Inf.One(100, "ollama", cpu)).Action);
        }
        cpu += Inf.BusyPerTick;
        var engaged = engine.Tick(Inf.At(next + 4), Inf.One(100, "ollama", cpu));

        Assert.Equal(InferenceHoldAction.Engage, engaged.Action);
        Assert.Equal(Inf.At(next + 2), Assert.Single(engaged.Workers).BusySince);
    }

    // ----------------------------------------------------------------------------------------------
    // "Absent" vs "could not be measured". Conflating these produced both a false claim and a real
    // mid-run suspension; see the header of InferenceActivity.cs.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_process_whose_cpu_time_we_are_denied_is_never_called_exited()
    {
        var engine = new InferenceHoldEngine(Inf.Options());
        var (_, _, next) = Run(engine, 0, 4, TimeSpan.Zero, Inf.BusyPerTick);
        Assert.True(engine.Holding);

        // Present in the sample, but with no measurement — the elevated-ollama case.
        var d1 = engine.Tick(Inf.At(next), [new ProcessCpuSample(100, "ollama", null)]);
        Assert.Equal(InferenceHoldAction.None, d1.Action);
        Assert.True(d1.Holding);
        Assert.Equal(100, Assert.Single(d1.Unmeasured).Pid);

        var d2 = engine.Tick(Inf.At(next + 1), [new ProcessCpuSample(100, "ollama", null)]);
        Assert.Equal(InferenceHoldAction.Release, d2.Action);
        Assert.DoesNotContain("exited", d2.Reason);
        Assert.Contains("measure", d2.Reason);
    }

    [Fact]
    public void An_unreadable_process_list_is_never_reported_as_the_process_exiting()
    {
        var engine = new InferenceHoldEngine(Inf.Options());
        var (_, _, next) = Run(engine, 0, 4, TimeSpan.Zero, Inf.BusyPerTick);
        Assert.True(engine.Holding);

        // GetProcessesByName threw: an empty sample here means "we do not know", not "there are none".
        var d1 = engine.Tick(Inf.At(next), Inf.None(), ["ollama"]);
        Assert.Equal(InferenceHoldAction.None, d1.Action);
        Assert.True(d1.Holding);
        Assert.Contains(d1.Unmeasured, u => u.Pid == 100);

        var d2 = engine.Tick(Inf.At(next + 1), Inf.None(), ["ollama"]);
        Assert.Equal(InferenceHoldAction.Release, d2.Action);
        Assert.DoesNotContain("exited", d2.Reason);
        Assert.Contains(d2.Unmeasured, u => u.Pid is null && u.Name == "ollama");
    }

    /// <summary>
    /// The tension, asserted in both directions in one test. Unmeasured must not be called idle
    /// (previous two tests), and it must not be allowed to keep the machine awake either — otherwise
    /// an unelevated daemon watching an elevated ollama never sleeps again.
    /// </summary>
    [Fact]
    public void Being_unable_to_measure_lets_the_machine_sleep_rather_than_holding_forever()
    {
        var engine = new InferenceHoldEngine(Inf.Options());
        var (_, _, next) = Run(engine, 0, 4, TimeSpan.Zero, Inf.BusyPerTick);
        Assert.True(engine.Holding);

        for (int t = next; t < next + 500; t++)
        {
            var d = engine.Tick(Inf.At(t), [new ProcessCpuSample(100, "ollama", null, Inf.T0)]);
            Assert.NotEqual(InferenceHoldAction.Engage, d.Action);
            Assert.NotEmpty(d.Unmeasured);   // and it keeps saying so, every tick
        }

        Assert.False(engine.Holding);
    }

    [Fact]
    public void An_empty_but_READABLE_sample_still_means_the_process_exited()
    {
        // The contrast case for the two tests above: when enumeration worked and returned nothing,
        // "exited" is the truth and must not be softened into "could not measure".
        var engine = new InferenceHoldEngine(Inf.Options());
        var (_, _, next) = Run(engine, 0, 4, TimeSpan.Zero, Inf.BusyPerTick);

        var d = engine.Tick(Inf.At(next), Inf.None(), []);

        Assert.Equal(InferenceHoldAction.Release, d.Action);
        Assert.Contains("exited", d.Reason);
        Assert.Empty(d.Unmeasured);
    }

    [Fact]
    public void A_stepped_clock_is_not_a_measurement_window()
    {
        var engine = new InferenceHoldEngine(Inf.Options());
        var (_, cpu, next) = Run(engine, 0, 4, TimeSpan.Zero, Inf.BusyPerTick);
        Assert.True(engine.Holding);

        // Wall clock jumps backwards (NTP, a timezone write). The hold must neither drop nor be
        // renewed on evidence that does not exist.
        var d = engine.Tick(Inf.At(next) - TimeSpan.FromMinutes(5), Inf.One(100, "ollama", cpu));

        Assert.Equal(InferenceHoldAction.None, d.Action);
        Assert.True(d.Holding);
        Assert.Contains("no measurement window", d.Reason);
    }

    [Fact]
    public void A_newly_appeared_process_needs_a_baseline_before_its_streak_can_start()
    {
        var engine = new InferenceHoldEngine(Inf.Options());
        engine.Tick(Inf.At(0), Inf.None());

        // The process appears at tick 1 already holding hours of CPU time. That total is history, not
        // work done in this window, and must not count toward the streak.
        var cpu = TimeSpan.FromHours(2);
        var d1 = engine.Tick(Inf.At(1), Inf.One(100, "ollama", cpu));
        Assert.Equal(InferenceHoldAction.None, d1.Action);

        // From the baseline onward, three real above-threshold ticks are still required.
        for (int t = 2; t <= 3; t++)
        {
            cpu += Inf.BusyPerTick;
            Assert.Equal(InferenceHoldAction.None, engine.Tick(Inf.At(t), Inf.One(100, "ollama", cpu)).Action);
        }
        cpu += Inf.BusyPerTick;
        Assert.Equal(InferenceHoldAction.Engage, engine.Tick(Inf.At(4), Inf.One(100, "ollama", cpu)).Action);
    }

    [Fact]
    public void The_hold_is_bounded_and_must_be_re_earned_from_scratch()
    {
        var engine = new InferenceHoldEngine(Inf.Options(maxHold: TimeSpan.FromSeconds(10)));
        var (_, cpu, next) = Run(engine, 0, 4, TimeSpan.Zero, Inf.BusyPerTick);   // engaged at tick 3
        Assert.True(engine.Holding);

        // Keep working right through the ceiling.
        InferenceHoldDecision? expiry = null;
        for (int t = next; t <= 13; t++)
        {
            cpu += Inf.BusyPerTick;
            var d = engine.Tick(Inf.At(t), Inf.One(100, "ollama", cpu));
            if (d.Action == InferenceHoldAction.Release) { expiry = d; break; }
        }

        Assert.NotNull(expiry);
        Assert.Contains("bounded", expiry!.Reason);
        Assert.False(engine.Holding);

        // Re-arming takes the full engage streak again, not one tick — otherwise the ceiling is a lie.
        cpu += Inf.BusyPerTick;
        Assert.Equal(InferenceHoldAction.None, engine.Tick(Inf.At(14), Inf.One(100, "ollama", cpu)).Action);
        cpu += Inf.BusyPerTick;
        Assert.Equal(InferenceHoldAction.None, engine.Tick(Inf.At(15), Inf.One(100, "ollama", cpu)).Action);
        cpu += Inf.BusyPerTick;
        Assert.Equal(InferenceHoldAction.Engage, engine.Tick(Inf.At(16), Inf.One(100, "ollama", cpu)).Action);
    }

    [Fact]
    public void Attribution_names_the_process_and_when_its_work_started()
    {
        var engine = new InferenceHoldEngine(Inf.Options());
        var (d, _, _) = Run(engine, 0, 4, TimeSpan.Zero, Inf.BusyPerTick, pid: 4242);

        var w = Assert.Single(d.Workers);
        Assert.Equal(4242, w.Pid);
        Assert.Equal("ollama", w.Name);
        // Credited from the first above-threshold tick (tick 1), not from the tick the streak completed.
        Assert.Equal(Inf.At(1), w.BusySince);
        Assert.NotNull(w.CpuFraction);
        Assert.InRange(w.CpuFraction!.Value, 0.24, 0.26);   // 2.0 s of CPU per 1 s tick across 8 cores
    }

    [Fact]
    public void The_busiest_process_is_reported_first()
    {
        var engine = new InferenceHoldEngine(Inf.Options() with { WatchedNames = ["ollama", "python"] });
        TimeSpan slow = TimeSpan.Zero, fast = TimeSpan.Zero;

        InferenceHoldDecision d = new(InferenceHoldAction.None, false, [], "");
        for (int t = 0; t <= 3; t++)
        {
            d = engine.Tick(Inf.At(t), [new(1, "ollama", slow), new(2, "python", fast)]);
            slow += Inf.BusyPerTick;
            fast += Inf.BusyPerTick * 2;
        }

        Assert.Equal(2, d.Workers.Count);
        Assert.Equal("python", d.Workers[0].Name);
    }

    [Fact]
    public void One_busy_process_keeps_the_hold_while_another_goes_idle()
    {
        var engine = new InferenceHoldEngine(Inf.Options());
        TimeSpan a = TimeSpan.Zero, b = TimeSpan.Zero;
        for (int t = 0; t <= 3; t++)
        {
            engine.Tick(Inf.At(t), [new(1, "ollama", a), new(2, "ollama", b)]);
            a += Inf.BusyPerTick;
            b += Inf.BusyPerTick;
        }
        Assert.True(engine.Holding);

        for (int t = 4; t <= 8; t++)
        {
            var d = engine.Tick(Inf.At(t), [new(1, "ollama", a), new(2, "ollama", b)]);
            a += Inf.BusyPerTick;
            b += Inf.IdlePerTick;      // process 2 stops working, process 1 keeps going
            Assert.NotEqual(InferenceHoldAction.Release, d.Action);
        }
        Assert.True(engine.Holding);
    }

    // ----------------------------------------------------------------------------------------------
    // THE GUARD. Do not weaken this test; weaken the feature instead.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// `ollama serve` and the LM Studio tray process sit resident for days doing nothing. If a refactor
    /// ever lets mere PRESENCE (or a merely non-zero CPU total, which every long-lived process has)
    /// take the hold, the machine stops sleeping overnight and this project has re-shipped the 14.4 W
    /// drain it fixed on 2026-08-28. An hour of simulated presence must produce exactly nothing.
    /// </summary>
    [Fact]
    public void GUARD_presence_of_a_resident_but_idle_process_must_never_take_the_hold()
    {
        var sink = new FakeSink();
        var anti = new AntiStandbyService(sink);
        var state = new InferenceHoldState(Inf.Options());
        var driver = new InferenceHoldDriver(anti, state, Inf.Options());

        // A long-lived server: a big non-zero cumulative CPU total that barely moves, for 3600 ticks.
        var cpu = TimeSpan.FromHours(9);
        for (int t = 0; t < 3600; t++)
        {
            var d = driver.Tick(Inf.At(t), Inf.One(100, "ollama", cpu));
            cpu += Inf.IdlePerTick;
            Assert.NotEqual(InferenceHoldAction.Engage, d.Action);
            Assert.False(d.Holding);
        }

        Assert.False(driver.Held);
        Assert.Equal(0, anti.HolderCount);
        Assert.Equal(0, sink.Engaged);
        Assert.False(state.Current.Holding);
        Assert.Empty(state.Current.Workers);
    }

    /// <summary>The other half of the guard: a process with a completely frozen CPU total (the purest
    /// form of "present but not working") is still not a reason to keep the machine awake.</summary>
    [Fact]
    public void GUARD_a_process_burning_zero_cpu_must_never_take_the_hold()
    {
        var engine = new InferenceHoldEngine(Inf.Options());
        var frozen = TimeSpan.FromMinutes(41);

        for (int t = 0; t < 500; t++)
            Assert.NotEqual(InferenceHoldAction.Engage, engine.Tick(Inf.At(t), Inf.One(100, "ollama", frozen)).Action);

        Assert.False(engine.Holding);
    }
}

public class InferenceHoldDriverTests
{
    private static (InferenceHoldDriver Driver, AntiStandbyService Anti, FakeSink Sink, InferenceHoldState State)
        Build(bool enforce = true)
    {
        var sink = new FakeSink();
        var anti = new AntiStandbyService(sink);
        var opt = Inf.Options(enforce);
        var state = new InferenceHoldState(opt);
        return (new InferenceHoldDriver(anti, state, opt), anti, sink, state);
    }

    private static TimeSpan Busy(InferenceHoldDriver d, int fromTick, int count, TimeSpan cpu, int pid = 100)
    {
        for (int i = 0; i < count; i++)
        {
            d.Tick(Inf.At(fromTick + i), Inf.One(pid, "ollama", cpu));
            cpu += Inf.BusyPerTick;
        }
        return cpu;
    }

    [Fact]
    public void Takes_exactly_one_hold_no_matter_how_long_the_work_runs()
    {
        var (driver, anti, sink, _) = Build();

        Busy(driver, 0, 200, TimeSpan.Zero);

        Assert.True(driver.Held);
        Assert.Equal(1, anti.HolderCount);   // never leaks a second ref count
        Assert.Equal(1, sink.Engaged);
        Assert.Equal(0, sink.Released);
    }

    [Fact]
    public void Releases_its_one_hold_exactly_once_when_the_work_stops()
    {
        var (driver, anti, sink, _) = Build();
        var cpu = Busy(driver, 0, 10, TimeSpan.Zero);

        for (int t = 10; t < 40; t++)
        {
            driver.Tick(Inf.At(t), Inf.One(100, "ollama", cpu));
            cpu += Inf.IdlePerTick;
        }

        Assert.False(driver.Held);
        Assert.Equal(0, anti.HolderCount);
        Assert.Equal(1, sink.Released);      // exactly one release for exactly one hold
    }

    [Fact]
    public void Cycles_cleanly_across_repeated_runs()
    {
        var (driver, anti, sink, _) = Build();
        var cpu = TimeSpan.Zero;
        int t = 0;

        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < 6; i++) { driver.Tick(Inf.At(t++), Inf.One(100, "ollama", cpu)); cpu += Inf.BusyPerTick; }
            for (int i = 0; i < 6; i++) { driver.Tick(Inf.At(t++), Inf.One(100, "ollama", cpu)); cpu += Inf.IdlePerTick; }
        }

        Assert.Equal(3, sink.Engaged);
        Assert.Equal(3, sink.Released);
        Assert.Equal(0, anti.HolderCount);
    }

    [Fact]
    public void Never_over_releases_a_hold_it_does_not_own()
    {
        var (driver, anti, sink, _) = Build();

        // Somebody else — the manual POST /ai/anti-standby toggle — is holding.
        anti.Start();
        Assert.Equal(1, anti.HolderCount);

        // The driver never engaged, so a run of idle ticks and a shutdown must not touch that hold.
        var cpu = TimeSpan.Zero;
        for (int t = 0; t < 30; t++) { driver.Tick(Inf.At(t), Inf.One(100, "ollama", cpu)); cpu += Inf.IdlePerTick; }
        driver.Shutdown();
        driver.Shutdown();

        Assert.Equal(1, anti.HolderCount);   // the manual hold survives intact
        Assert.Equal(0, sink.Released);
    }

    [Fact]
    public void Shutdown_gives_the_hold_back_and_is_idempotent()
    {
        var (driver, anti, sink, _) = Build();
        Busy(driver, 0, 10, TimeSpan.Zero);
        Assert.True(driver.Held);

        driver.Shutdown();
        driver.Shutdown();
        driver.Shutdown();

        Assert.False(driver.Held);
        Assert.Equal(0, anti.HolderCount);
        Assert.Equal(1, sink.Released);
    }

    [Fact]
    public void A_release_arriving_after_shutdown_does_not_double_release()
    {
        var (driver, anti, sink, _) = Build();
        var cpu = Busy(driver, 0, 10, TimeSpan.Zero);
        driver.Shutdown();

        // The engine still thinks it is holding; the driver knows it gave the hold back.
        for (int t = 10; t < 20; t++) { driver.Tick(Inf.At(t), Inf.One(100, "ollama", cpu)); cpu += Inf.IdlePerTick; }

        Assert.Equal(0, anti.HolderCount);
        Assert.Equal(1, sink.Released);
    }

    [Fact]
    public void Observe_only_mode_reports_everything_and_holds_nothing()
    {
        var (driver, anti, sink, state) = Build(enforce: false);

        Busy(driver, 0, 20, TimeSpan.Zero);

        Assert.False(driver.Held);
        Assert.Equal(0, anti.HolderCount);
        Assert.Equal(0, sink.Engaged);

        // ...but the evidence for enabling enforcement is fully published.
        var s = state.Current;
        Assert.False(s.Enforcing);
        Assert.True(s.Holding);                    // "this is what it WOULD be holding for"
        Assert.NotNull(s.HoldingSince);
        Assert.Equal("ollama", Assert.Single(s.Workers).Name);
    }

    [Fact]
    public void Published_state_is_honest_before_anything_has_been_measured()
    {
        var opt = Inf.Options();
        var state = new InferenceHoldState(opt);

        var s = state.Current;
        Assert.Null(s.LastTickAt);        // no tick has happened; we do not invent one
        Assert.Null(s.HoldingSince);
        Assert.Null(s.LastReason);
        Assert.False(s.Holding);
        Assert.Equal(opt.WatchedNames, s.WatchedNames);
    }

    [Fact]
    public void Published_state_names_the_holder_and_the_moment_it_started()
    {
        var (driver, _, _, state) = Build();

        Busy(driver, 0, 10, TimeSpan.Zero, pid: 777);

        var s = state.Current;
        Assert.True(s.Enforcing);
        Assert.True(s.Holding);
        Assert.Equal(Inf.At(3), s.HoldingSince);       // the tick the streak completed
        var w = Assert.Single(s.Workers);
        Assert.Equal(777, w.Pid);
        Assert.Equal(Inf.At(1), w.BusySince);          // the tick the work actually started
        Assert.Equal(Inf.At(9), s.LastTickAt);
    }

    /// <summary>
    /// Scenario A from the review: the daemon is unelevated, ollama.exe is not, so its CPU time is
    /// refused every tick. The old code omitted it entirely and published an empty worker list with
    /// "no sustained inference work" — a claim about a process it had never managed to read.
    /// </summary>
    [Fact]
    public void Published_state_admits_which_processes_it_could_not_measure()
    {
        var (driver, _, _, state) = Build();

        for (int t = 0; t < 5; t++)
            driver.Tick(Inf.At(t), new ProcessSampleResult([new ProcessCpuSample(100, "ollama", null)], []));

        var s = state.Current;
        Assert.False(s.Holding);
        Assert.Empty(s.Workers);
        Assert.Contains(s.Unmeasured, u => u.Pid == 100 && u.Name == "ollama");
        Assert.NotEqual("no sustained inference work", s.LastReason);   // that sentence would be a lie
    }

    [Fact]
    public void Published_state_is_empty_of_excuses_when_everything_was_measurable()
    {
        var (driver, _, _, state) = Build();
        Busy(driver, 0, 10, TimeSpan.Zero);

        Assert.Empty(state.Current.Unmeasured);
    }

    [Fact]
    public void Published_state_clears_the_holder_when_the_work_stops()
    {
        var (driver, _, _, state) = Build();
        var cpu = Busy(driver, 0, 10, TimeSpan.Zero);
        for (int t = 10; t < 20; t++) { driver.Tick(Inf.At(t), Inf.One(100, "ollama", cpu)); cpu += Inf.IdlePerTick; }

        var s = state.Current;
        Assert.False(s.Holding);
        Assert.Null(s.HoldingSince);
        Assert.Empty(s.Workers);
    }
}

public class InferenceHoldOptionsTests
{
    private static Func<string, string?> Env(Dictionary<string, string> vars)
        => k => vars.TryGetValue(k, out var v) ? v : null;

    [Fact]
    public void Enforcement_is_off_unless_explicitly_turned_on()
    {
        Assert.False(InferenceHoldOptions.FromEnvironment(Env([])).Enforce);
        Assert.False(InferenceHoldOptions.FromEnvironment(Env(new() { ["GPDFORGE_INFERENCE_HOLD"] = "0" })).Enforce);
        Assert.False(InferenceHoldOptions.FromEnvironment(Env(new() { ["GPDFORGE_INFERENCE_HOLD"] = "true" })).Enforce);
        Assert.True(InferenceHoldOptions.FromEnvironment(Env(new() { ["GPDFORGE_INFERENCE_HOLD"] = "1" })).Enforce);
    }

    [Fact]
    public void Defaults_cover_the_runtimes_actually_installed_on_this_class_of_machine()
    {
        var names = InferenceHoldOptions.DefaultWatchedNames;
        Assert.Contains("ollama", names);
        Assert.Contains("llama-server", names);
        Assert.Contains("LM Studio", names);
        Assert.Contains("python", names);
        // Process.GetProcessesByName takes the name WITHOUT the extension.
        Assert.DoesNotContain(names, n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_watch_list_is_overridable_and_tolerates_how_task_manager_shows_names()
    {
        var names = InferenceHoldOptions.ParseNames(" vllm.exe , mlc_chat , OLLAMA , ollama ");
        Assert.NotNull(names);
        Assert.Equal(["vllm", "mlc_chat", "OLLAMA"], names);   // .exe stripped, duplicates folded
    }

    [Fact]
    public void An_empty_override_falls_back_to_the_defaults_rather_than_watching_nothing()
    {
        // A list of nothing would disable the feature while looking configured — the worst outcome.
        Assert.Null(InferenceHoldOptions.ParseNames(null));
        Assert.Null(InferenceHoldOptions.ParseNames("   "));
        Assert.Null(InferenceHoldOptions.ParseNames(",, ,"));
        Assert.Equal(InferenceHoldOptions.DefaultWatchedNames,
            InferenceHoldOptions.FromEnvironment(Env(new() { ["GPDFORGE_INFERENCE_PROCESSES"] = " , " })).WatchedNames);
    }

    [Fact]
    public void A_nonsense_cpu_threshold_is_ignored_rather_than_obeyed()
    {
        foreach (var bad in new[] { "0", "-1", "1.5", "loads", "" })
            Assert.Equal(0.15, InferenceHoldOptions.FromEnvironment(
                Env(new() { ["GPDFORGE_INFERENCE_CPU"] = bad })).BusyCpuFraction);

        Assert.Equal(0.30, InferenceHoldOptions.FromEnvironment(
            Env(new() { ["GPDFORGE_INFERENCE_CPU"] = "0.30" })).BusyCpuFraction);
    }
}

/// <summary>The worker itself is a thin loop; the only thing worth asserting about it that the driver
/// tests cannot is that however the loop ends, the hold comes back.</summary>
public class InferenceHoldWorkerTests
{
    private sealed class BusySampler : IProcessCpuSampler
    {
        private readonly TaskCompletionSource _engaged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TimeSpan _cpu;
        private readonly AntiStandbyService _anti;

        public BusySampler(AntiStandbyService anti) => _anti = anti;
        public Task Engaged => _engaged.Task;

        public IReadOnlyList<ProcessCpuSample> Sample(IReadOnlyList<string> watched)
        {
            // A whole second of CPU per tick against a 20 ms tick interval: unambiguously "working"
            // however the loop's real timing lands, so this test does not depend on wall-clock luck.
            _cpu += TimeSpan.FromSeconds(1);
            if (_anti.HolderCount > 0) _engaged.TrySetResult();
            return [new ProcessCpuSample(100, "ollama", _cpu)];
        }
    }

    /// <summary>
    /// Works until the hold is taken, then fails forever — a wedged process table, a permissions
    /// change, a resume that broke enumeration.
    /// </summary>
    private sealed class BreaksAfterEngagingSampler(AntiStandbyService anti) : IProcessCpuSampler
    {
        private TimeSpan _cpu;
        private volatile bool _broken;

        public IReadOnlyList<ProcessCpuSample> Sample(IReadOnlyList<string> watched)
        {
            if (_broken) throw new InvalidOperationException("process table unreadable");
            _cpu += TimeSpan.FromSeconds(1);
            if (anti.HolderCount > 0) _broken = true;
            return [new ProcessCpuSample(100, "ollama", _cpu)];
        }
    }

    /// <summary>
    /// THE BATTERY TEST. Every safety mechanism — per-tick re-justification, the release streak and
    /// the MaxHold ceiling — lives inside Tick, so a loop that catches a sampler failure and skips the
    /// tick freezes the engine WITH THE HOLD TAKEN and nothing can ever take it back. The machine is
    /// then pinned awake forever on stale evidence. A sampler that never works again must end with the
    /// hold released, not held.
    /// </summary>
    [Fact]
    public async Task A_sampler_that_fails_forever_releases_the_hold_instead_of_freezing_it()
    {
        var sink = new FakeSink();
        var anti = new AntiStandbyService(sink);
        var opt = Inf.Options(enforce: true) with { TickInterval = TimeSpan.FromMilliseconds(20) };
        var state = new InferenceHoldState(opt);
        var worker = new InferenceHoldWorker(new BreaksAfterEngagingSampler(anti), anti, state, opt,
            NullLogger<InferenceHoldWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            bool everHeld = false;
            while (DateTime.UtcNow < deadline)
            {
                if (anti.HolderCount > 0) everHeld = true;
                if (everHeld && anti.HolderCount == 0) break;
                await Task.Delay(20);
            }

            Assert.True(everHeld, "the sampler never produced a hold, so the test proved nothing");
            Assert.Equal(0, anti.HolderCount);
            Assert.Equal(1, sink.Released);

            // ...and it says why, without claiming an exit it did not observe.
            var s = state.Current;
            Assert.False(s.Holding);
            Assert.NotEmpty(s.Unmeasured);
            Assert.DoesNotContain("exited", s.LastReason ?? "");
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Shutting_the_service_down_gives_the_hold_back()
    {
        var sink = new FakeSink();
        var anti = new AntiStandbyService(sink);
        var opt = Inf.Options(enforce: true) with { TickInterval = TimeSpan.FromMilliseconds(20) };
        var sampler = new BusySampler(anti);
        var worker = new InferenceHoldWorker(sampler, anti, new InferenceHoldState(opt), opt,
            NullLogger<InferenceHoldWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await sampler.Engaged.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, anti.HolderCount);

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, anti.HolderCount);
        Assert.Equal(1, sink.Released);
    }
}
