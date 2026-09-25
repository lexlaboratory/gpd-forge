// GPD Forge — auto-TDP-to-target-FPS controller tests. GPL-3.0-or-later.
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

/// <summary>Exercises the PURE PID (FpsTdpController.NextStapm): direction, clamps, deadband,
/// slew limit, anti-windup, determinism and closed-loop convergence against a model plant.</summary>
public class FpsTdpControllerTests
{
    private const int Min = 8;
    private const int Max = 30;

    private static FpsTdpController Controller(FpsTdpController.Options? opt = null) => new(opt);

    // --- direction -----------------------------------------------------------------------------

    [Fact]
    public void Raises_stapm_when_below_target()
    {
        int next = Controller().NextStapm(targetFps: 60, measuredFps: 45, currentStapm: 20, Min, Max);
        Assert.True(next > 20, $"expected STAPM to rise from 20, got {next}");
    }

    [Fact]
    public void Lowers_stapm_when_above_target()
    {
        int next = Controller().NextStapm(targetFps: 60, measuredFps: 75, currentStapm: 20, Min, Max);
        Assert.True(next < 20, $"expected STAPM to fall from 20, got {next}");
    }

    // --- clamps --------------------------------------------------------------------------------

    [Fact]
    public void Never_exceeds_maxW_even_with_a_huge_deficit()
    {
        int next = Controller().NextStapm(targetFps: 240, measuredFps: 30, currentStapm: Max, Min, Max);
        Assert.Equal(Max, next);
    }

    [Fact]
    public void Never_drops_below_minW_even_with_a_huge_surplus()
    {
        int next = Controller().NextStapm(targetFps: 30, measuredFps: 240, currentStapm: Min, Min, Max);
        Assert.Equal(Min, next);
    }

    [Fact]
    public void Clamps_an_out_of_range_current_into_the_window()
    {
        // On target so no step is added; a bogus 999W accumulator must still be pulled to the rail.
        int next = Controller().NextStapm(targetFps: 60, measuredFps: 60, currentStapm: 999, Min, Max);
        Assert.Equal(Max, next);
    }

    // --- stability / deadband ------------------------------------------------------------------

    [Fact]
    public void Holds_stapm_inside_the_deadband()
    {
        var c = Controller(new FpsTdpController.Options(DeadbandFps: 2.0));
        Assert.Equal(22, c.NextStapm(60, 61, 22, Min, Max));   // error -1, within band
        Assert.Equal(22, c.NextStapm(60, 59, 22, Min, Max));   // error +1, within band
        Assert.Equal(22, c.NextStapm(60, 60, 22, Min, Max));   // exactly on target
    }

    // --- slew-rate limit -----------------------------------------------------------------------

    [Fact]
    public void Limits_the_step_to_MaxStepW_per_tick()
    {
        var c = Controller(new FpsTdpController.Options(WattsPerFps: 0.5, MaxStepW: 3.0));
        // 90 fps deficit * 0.5 = 45W desired, slewed to +3W in one tick.
        Assert.Equal(18, c.NextStapm(targetFps: 120, measuredFps: 30, currentStapm: 15, Min, Max));
    }

    // --- anti-windup ---------------------------------------------------------------------------

    [Fact]
    public void Does_not_wind_up_past_the_rail_and_reverses_immediately()
    {
        var c = Controller();
        // Saturated at the ceiling with a persistent deficit -> stays pinned, no phantom accumulation.
        Assert.Equal(Max, c.NextStapm(60, 20, Max, Min, Max));
        Assert.Equal(Max, c.NextStapm(60, 20, Max, Min, Max));
        // The moment we overshoot, it steps down on the very next tick (no windup lag).
        int afterReversal = c.NextStapm(60, 80, Max, Min, Max);
        Assert.True(afterReversal < Max, $"expected immediate step down from {Max}, got {afterReversal}");
    }

    // --- progress guarantee --------------------------------------------------------------------

    [Fact]
    public void Moves_at_least_one_watt_outside_the_deadband_despite_a_tiny_gain()
    {
        var c = Controller(new FpsTdpController.Options(WattsPerFps: 0.001, DeadbandFps: 2.0));
        // error +5 fps -> raw step 0.005W rounds to 0, but we force a 1W move so it still converges.
        Assert.Equal(21, c.NextStapm(60, 55, 20, Min, Max));
        // symmetric on the way down.
        Assert.Equal(19, c.NextStapm(60, 65, 20, Min, Max));
    }

    // --- determinism / purity ------------------------------------------------------------------

    [Fact]
    public void Is_deterministic_for_identical_inputs()
    {
        var c = Controller();
        int a = c.NextStapm(60, 48, 17, Min, Max);
        int b = c.NextStapm(60, 48, 17, Min, Max);
        Assert.Equal(a, b);
    }

    // --- inverted window ------------------------------------------------------------------------

    [Fact]
    public void Tolerates_an_inverted_min_max_window()
    {
        // Pass max/min swapped; the controller normalizes and still clamps to [8,30].
        int next = Controller().NextStapm(targetFps: 240, measuredFps: 30, currentStapm: 30, minW: Max, maxW: Min);
        Assert.Equal(Max, next);
    }

    // --- convergence (closed loop against a monotonic model plant) -----------------------------

    // Linear, monotonic: more sustained watts -> more fps. Exactly 60 fps at 25W.
    private static double Plant(int stapm) => 2.0 * stapm + 10.0;

    [Fact]
    public void Converges_to_the_target_and_settles_without_oscillating()
    {
        var c = Controller();

        int stapm = Min;
        for (int i = 0; i < 40; i++)
            stapm = c.NextStapm(60, Plant(stapm), stapm, Min, Max);

        // Settled at the correct operating point (25W -> 60 fps)...
        Assert.Equal(25, stapm);
        // ...and it STAYS there: no limit-cycle oscillation once inside the band.
        int a = c.NextStapm(60, Plant(stapm), stapm, Min, Max);
        int b = c.NextStapm(60, Plant(a), a, Min, Max);
        Assert.Equal(25, a);
        Assert.Equal(25, b);
    }

    [Fact]
    public void Settles_within_the_deadband_for_a_non_integer_operating_point()
    {
        var c = Controller(new FpsTdpController.Options(DeadbandFps: 2.0));

        // Target 61 fps -> ideal 25.5W, which is between integer-watt steps.
        int stapm = Min;
        for (int i = 0; i < 40; i++)
            stapm = c.NextStapm(61, Plant(stapm), stapm, Min, Max);

        double finalFps = Plant(stapm);
        Assert.True(Math.Abs(61 - finalFps) <= 2.0, $"expected fps within the deadband of 61, got {finalFps} at {stapm}W");
    }
}

/// <summary>Exercises the telemetry-backed <see cref="IFpsSource"/> adapter.</summary>
public class TelemetryFpsSourceTests
{
    [Fact]
    public void Reads_through_the_delegate_live()
    {
        double fps = 42.0;
        var src = new TelemetryFpsSource(() => fps);
        Assert.Equal(42.0, src.CurrentFps());
        fps = 58.5;
        Assert.Equal(58.5, src.CurrentFps());   // reflects the latest snapshot, not a cached value
    }

    [Fact]
    public void Rejects_a_null_reader()
    {
        Assert.Throws<ArgumentNullException>(() => new TelemetryFpsSource(null!));
    }
}

/// <summary>Exercises the <see cref="AutoFpsLoop"/> orchestrator's single tick.</summary>
public class AutoFpsLoopTests
{
    private sealed class FakeTdp : ITdpController
    {
        public TdpProfile Last { get; private set; }
        public int Calls { get; private set; }

        public Task<TdpApplyResult> ApplyAsync(TdpProfile profile, string owner, CancellationToken ct)
        {
            Last = profile;
            Calls++;
            return Task.FromResult(new TdpApplyResult(profile, new TdpReadout(profile.StapmW, profile.FastW), true, 1));
        }
    }

    private static readonly TdpProfile Gaming = new(StapmW: 25, FastW: 33, SlowW: 28, TctlC: 95);

    [Fact]
    public async Task Tick_raises_stapm_and_applies_when_below_target()
    {
        var tdp = new FakeTdp();
        var loop = new AutoFpsLoop(new TelemetryFpsSource(() => 40.0), new FpsTdpController(), tdp,
            new AutoFpsLoop.Options(MinW: 8, MaxW: 30));

        int next = await loop.TickAsync(targetFps: 60, current: Gaming, CancellationToken.None);

        Assert.True(next > Gaming.StapmW, $"expected STAPM to rise from {Gaming.StapmW}, got {next}");
        Assert.Equal(1, tdp.Calls);
        Assert.Equal(next, tdp.Last.StapmW);
    }

    [Fact]
    public async Task Tick_preserves_fast_slow_tctl_of_the_current_profile()
    {
        var tdp = new FakeTdp();
        var loop = new AutoFpsLoop(new TelemetryFpsSource(() => 40.0), new FpsTdpController(), tdp);

        await loop.TickAsync(60, Gaming, CancellationToken.None);

        Assert.Equal(Gaming.FastW, tdp.Last.FastW);
        Assert.Equal(Gaming.SlowW, tdp.Last.SlowW);
        Assert.Equal(Gaming.TctlC, tdp.Last.TctlC);
    }

    [Fact]
    public async Task Tick_holds_stapm_when_already_on_target()
    {
        var tdp = new FakeTdp();
        var loop = new AutoFpsLoop(new TelemetryFpsSource(() => 60.0), new FpsTdpController(), tdp);

        int next = await loop.TickAsync(60, Gaming, CancellationToken.None);

        Assert.Equal(Gaming.StapmW, next);   // inside the deadband -> unchanged
        // ...and NOT written. Each apply is two ryzenadj launches; re-issuing an unchanged limit
        // every tick bought nothing the 30 s readback reassert (TdpReasserter) does not already cover.
        Assert.Equal(0, tdp.Calls);
    }

    [Fact]
    public async Task Tick_at_the_ceiling_with_nothing_to_gain_does_not_write()
    {
        var tdp = new FakeTdp();
        var loop = new AutoFpsLoop(new TelemetryFpsSource(() => 10.0), new FpsTdpController(), tdp,
            new AutoFpsLoop.Options(MinW: 8, MaxW: 28));

        await loop.TickAsync(120, Gaming with { StapmW = 28 }, CancellationToken.None);

        Assert.Equal(0, tdp.Calls);   // clamped to where it already is
    }

    [Fact]
    public async Task Tick_respects_the_configured_max_ceiling()
    {
        var tdp = new FakeTdp();
        var loop = new AutoFpsLoop(new TelemetryFpsSource(() => 10.0), new FpsTdpController(), tdp,
            new AutoFpsLoop.Options(MinW: 8, MaxW: 28));
        TdpProfile atCeiling = Gaming with { StapmW = 28 };

        int next = await loop.TickAsync(120, atCeiling, CancellationToken.None);

        Assert.Equal(28, next);
        Assert.True(tdp.Calls == 0 || tdp.Last.StapmW <= 28);
    }
}

/// <summary>
/// The worker's per-tick auto-FPS decision (<see cref="AutoFpsStep"/>): the controller's next STAPM,
/// and a write only when that changes what is in force.
/// </summary>
public class AutoFpsStepTests
{
    private static readonly TdpProfile Gaming = new(StapmW: 25, FastW: 33, SlowW: 28, TctlC: 95);
    private static readonly FpsTdpController Controller = new();

    [Fact]
    public void Inside_the_deadband_nothing_is_written()
    {
        // Before 2026-09-24 ForgeWorker applied the result every tick, changed or not: at a steady
        // 60 FPS on a 60 FPS target that was the same 25 W re-written each second — one ryzenadj apply
        // plus one --info readback per tick, for nothing.
        var d = AutoFpsStep.Decide(Controller, targetFps: 60, measuredFps: 61, basis: Gaming, inForce: Gaming, minW: 8, maxW: 30);

        Assert.Equal(25, d.NextStapm);
        Assert.Null(d.Write);
    }

    [Fact]
    public void Outside_the_deadband_the_new_stapm_is_written_on_the_modes_profile()
    {
        var d = AutoFpsStep.Decide(Controller, 60, 40, Gaming, Gaming, 8, 30);

        Assert.True(d.NextStapm > 25);
        Assert.Equal(Gaming with { StapmW = d.NextStapm }, d.Write);
    }

    [Fact]
    public void The_integrator_starts_from_what_is_in_force_not_from_a_stale_counter()
    {
        // `AutoFpsState.CurrentStapm` used to start at 25 whatever the mode and was never synced, so
        // the first governor tick after `POST /mode` could jump STAPM by the difference in one step.
        // In force here is a 15 W limit set by something else; inside the deadband it is kept.
        var inForce = Gaming with { StapmW = 15 };

        var d = AutoFpsStep.Decide(Controller, 60, 60, Gaming, inForce, 8, 30);

        Assert.Equal(15, d.NextStapm);
        Assert.Null(d.Write);
    }

    [Fact]
    public void With_nothing_in_force_it_starts_from_the_modes_preset_and_writes_it()
    {
        var d = AutoFpsStep.Decide(Controller, 60, 60, Gaming, inForce: null, 8, 30);

        Assert.Equal(25, d.NextStapm);
        Assert.Equal(Gaming, d.Write);   // nothing verified is there yet, so the steady value is applied once
    }

    [Fact]
    public void A_steady_stapm_on_a_different_profile_is_written_once_to_take_the_modes_boost()
    {
        // A manual override is flat (12/12/12). Auto-FPS steers STAPM on the MODE's profile, so the
        // first tick moves fast/slow back to the mode's headroom even when STAPM stays put.
        var manual = new TdpProfile(12, 12, 12, 95);

        var d = AutoFpsStep.Decide(Controller, 60, 60, Gaming, manual, 8, 30);

        Assert.Equal(12, d.NextStapm);
        Assert.Equal(Gaming with { StapmW = 12 }, d.Write);
    }

    [Fact]
    public void A_preset_above_the_governors_ceiling_is_clamped_once_and_then_held()
    {
        var hot = Gaming with { StapmW = 35 };

        var first = AutoFpsStep.Decide(Controller, 60, 60, hot, hot, 8, 30);
        Assert.Equal(30, first.NextStapm);
        Assert.NotNull(first.Write);

        var second = AutoFpsStep.Decide(Controller, 60, 60, hot, first.Write, 8, 30);
        Assert.Null(second.Write);
    }
}
