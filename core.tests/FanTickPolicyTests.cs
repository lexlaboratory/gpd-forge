// GPD Forge — the per-tick fan decision, without a timer, an EC or a sampler. GPL-3.0-or-later.
//
// Everything below used to live inline in ForgeWorker's tick, where the only way to exercise it was
// to run the whole worker. The rules have not changed; they are now pinned.
using GpdForge.Fan;
using Xunit;

namespace GpdForge.Core.Tests;

public class FanTickPolicyTests
{
    private static int Curve(string mode, double tempC) =>
        FanCurve.DutyForTemp(tempC, FanCurve.ForMode(mode)!, FanCurve.DefaultHysteresisC, 0);

    // ---------------------------------------------------------------------------------------------
    // Auto, Manual and unknown modes
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Auto_is_written_once_on_the_transition_not_every_tick()
    {
        var p = new FanTickPolicy();
        Assert.Equal(FanCommand.Auto, p.Next("Auto", 128, 60, 0));
        Assert.Equal(FanCommand.None, p.Next("Auto", 128, 60, 1));
        Assert.Equal(FanCommand.None, p.Next("Auto", 128, null, 2));
    }

    [Fact]
    public void Returning_to_Auto_from_a_curve_hands_back_to_firmware_once()
    {
        var p = new FanTickPolicy();
        Assert.Equal(FanCommandKind.Duty, p.Next("Balanced", 128, 70, 0).Kind);
        Assert.Equal(FanCommand.Auto, p.Next("Auto", 128, 70, 1));
        Assert.Equal(FanCommand.None, p.Next("Auto", 128, 70, 2));
    }

    [Fact]
    public void Manual_writes_the_manual_duty_every_tick_whatever_the_sensor_says()
    {
        var p = new FanTickPolicy();
        Assert.Equal(FanCommand.DutyOf(200), p.Next("Manual", 200, null, 0));
        Assert.Equal(FanCommand.DutyOf(200), p.Next("Manual", 200, double.NaN, 1));
        Assert.Equal(FanCommand.DutyOf(90), p.Next("Manual", 90, 70, 2));
    }

    [Theory]
    [InlineData("Turbo")]
    [InlineData("manual")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_mode_hands_the_fan_back_to_firmware_on_every_tick(string? mode)
    {
        // Defense in depth for imported/legacy state: the HTTP API rejects these, but a bad value that
        // got in anyway must never leave a previous manual duty pinned.
        var p = new FanTickPolicy();
        Assert.Equal(FanCommand.DutyOf(220), p.Next("Manual", 220, 70, 0));
        Assert.Equal(FanCommand.Auto, p.Next(mode, 220, 70, 1));
        Assert.Equal(FanCommand.Auto, p.Next(mode, 220, 70, 2));
    }

    // ---------------------------------------------------------------------------------------------
    // Curve modes: smoother → curve → ramp
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Quiet", 65)]
    [InlineData("Balanced", 75)]
    [InlineData("Aggressive", 85)]
    public void A_curve_cold_start_adopts_the_curve_duty_directly(string mode, double tempC)
    {
        var p = new FanTickPolicy();
        Assert.Equal(FanCommand.DutyOf(Curve(mode, tempC)), p.Next(mode, 128, tempC, 0));
    }

    [Fact]
    public void A_sudden_rise_is_smoothed_and_then_rate_limited()
    {
        var p = new FanTickPolicy();
        int duty = 0;
        for (int s = 0; s < 30; s++) duty = p.Next("Balanced", 128, 50, s).Duty;

        // One tick at 95 °C: the smoother damps the reading and the ramp caps the step at 25/s, so the
        // fan does not jump to the 95 °C duty the raw curve would ask for.
        int next = p.Next("Balanced", 128, 95, 30).Duty;
        Assert.InRange(next - duty, 0, FanDutyRamp.DefaultUpPerSecond);
        Assert.True(next < Curve("Balanced", 95));
    }

    [Fact]
    public void A_sustained_rise_reaches_the_curve_duty()
    {
        var p = new FanTickPolicy();
        for (int s = 0; s < 10; s++) p.Next("Balanced", 128, 50, s);
        int duty = 0;
        for (int s = 10; s < 40; s++) duty = p.Next("Balanced", 128, 90, s).Duty;
        Assert.Equal(Curve("Balanced", 90), duty);
    }

    [Fact]
    public void Elapsed_time_is_measured_not_assumed()
    {
        // A tick that lands 3 s after the last one may move the duty three times as far: both the
        // smoother and the ramp are rates, and a late tick must not make the fan slower to respond.
        var fast = new FanTickPolicy();
        var slow = new FanTickPolicy();
        for (int s = 0; s < 30; s++) { fast.Next("Balanced", 128, 50, s); slow.Next("Balanced", 128, 50, s); }

        int fastStep = fast.Next("Balanced", 128, 100, 30).Duty;
        int slowStep = slow.Next("Balanced", 128, 100, 32).Duty;   // the same reading, 2 s later
        Assert.True(slowStep > fastStep, $"a later tick must be allowed further: {slowStep} vs {fastStep}");
    }

    // ---------------------------------------------------------------------------------------------
    // Sensor grace
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_missed_reading_within_the_grace_reuses_the_last_usable_one()
    {
        var p = new FanTickPolicy();
        for (int s = 0; s < 10; s++) p.Next("Balanced", 128, 72, s);
        int steady = p.Next("Balanced", 128, 72, 10).Duty;

        Assert.Equal(FanCommand.DutyOf(steady), p.Next("Balanced", 128, null, 11));
        Assert.Equal(FanCommand.DutyOf(steady), p.Next("Balanced", 128, 0, 12));
        Assert.Equal(FanCommand.DutyOf(steady), p.Next("Balanced", 128, double.NaN, 10 + FanTickPolicy.SensorGraceSeconds));
    }

    [Fact]
    public void A_sensor_gone_past_the_grace_hands_the_fan_back_to_firmware_and_forgets_the_curve()
    {
        var p = new FanTickPolicy();
        for (int s = 0; s < 20; s++) p.Next("Balanced", 128, 60, s);
        for (int s = 20; s < 40; s++) p.Next("Balanced", 128, 90, s);   // ramped up, smoother hot

        Assert.Equal(FanCommand.Auto, p.Next("Balanced", 128, null, 39 + FanTickPolicy.SensorGraceSeconds + 0.5));

        // The next usable reading is a cold start: no stale average or ramp position leaks across.
        Assert.Equal(FanCommand.DutyOf(Curve("Balanced", 55)), p.Next("Balanced", 128, 55, 50));
    }

    [Fact]
    public void Curve_mode_never_takes_control_without_ever_having_a_usable_reading()
    {
        var p = new FanTickPolicy();
        Assert.Equal(FanCommand.Auto, p.Next("Aggressive", 128, null, 0));
        Assert.Equal(FanCommand.Auto, p.Next("Aggressive", 128, -1, 1));
        Assert.Equal(FanCommand.Auto, p.Next("Aggressive", 128, double.PositiveInfinity, 2));
    }

    [Fact]
    public void Leaving_curve_mode_through_Manual_resets_the_curve_state()
    {
        var p = new FanTickPolicy();
        for (int s = 0; s < 20; s++) p.Next("Balanced", 128, 90, s);
        p.Next("Manual", 100, 90, 20);

        // Back on the curve at a cool reading: a cold start, not a slow ramp down from the old duty.
        Assert.Equal(FanCommand.DutyOf(Curve("Balanced", 50)), p.Next("Balanced", 128, 50, 21));
    }

    // ---------------------------------------------------------------------------------------------
    // /panic: the mode switch lands on the next tick
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Panic_from_Auto_takes_full_effect_on_the_very_next_tick()
    {
        var p = new FanTickPolicy();
        p.Next("Auto", 128, 80, 0);
        Assert.Equal(FanCommand.DutyOf(Curve("Aggressive", 80)), p.Next("Aggressive", 128, 80, 1));
    }

    [Fact]
    public void Panic_from_another_curve_retargets_on_the_next_tick_and_ramps_from_there()
    {
        var p = new FanTickPolicy();
        for (int s = 0; s < 20; s++) p.Next("Quiet", 128, 80, s);
        int quiet = p.Next("Quiet", 128, 80, 20).Duty;

        // Kept from the inline tick: a change of curve counts as a fresh start of the clock (dt = 0),
        // so the switch tick holds the duty and the ramp climbs from the tick after.
        Assert.Equal(FanCommand.DutyOf(quiet), p.Next("Aggressive", 128, 80, 21));
        int after = p.Next("Aggressive", 128, 80, 22).Duty;
        Assert.True(after > quiet, $"Aggressive must pull the duty up: {after} vs {quiet}");
    }

    [Fact]
    public void Reset_makes_the_next_curve_tick_a_cold_start()
    {
        var p = new FanTickPolicy();
        for (int s = 0; s < 20; s++) p.Next("Balanced", 128, 90, s);
        p.Reset();
        Assert.Equal(FanCommand.DutyOf(Curve("Balanced", 50)), p.Next("Balanced", 128, 50, 20));
    }
}
