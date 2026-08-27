// GPD Forge — temp→duty fan curve tests (interpolation + hysteresis + named curves). GPL-3.0-or-later.
using GpdForge.Fan;
using Xunit;

namespace GpdForge.Core.Tests;

public class FanCurveInterpolateTests
{
    private static readonly CurvePoint[] Simple = [new(0, 0), new(10, 100), new(20, 100), new(30, 255)];

    [Fact]
    public void Below_first_point_clamps_to_first_duty()
    {
        Assert.Equal(0, FanCurve.Interpolate(-50, Simple));
        Assert.Equal(0, FanCurve.Interpolate(0, Simple));
    }

    [Fact]
    public void Above_last_point_clamps_to_last_duty()
    {
        Assert.Equal(255, FanCurve.Interpolate(30, Simple));
        Assert.Equal(255, FanCurve.Interpolate(999, Simple));
    }

    [Fact]
    public void Exact_breakpoints_return_the_exact_defined_duty()
    {
        foreach (var p in Simple)
            Assert.Equal(p.Duty, FanCurve.Interpolate(p.TempC, Simple));
    }

    [Fact]
    public void Midpoint_of_a_linear_segment_interpolates_halfway()
    {
        // (0,0) -> (10,100): halfway (5°C) should be duty 50.
        Assert.Equal(50, FanCurve.Interpolate(5, Simple));
    }

    [Fact]
    public void Flat_segment_holds_steady()
    {
        // (10,100) -> (20,100): flat, any point in between is 100.
        Assert.Equal(100, FanCurve.Interpolate(15, Simple));
    }

    [Fact]
    public void Quarter_and_three_quarter_points_interpolate_proportionally()
    {
        // (20,100) -> (30,255): span 155 over 10°C.
        Assert.Equal(100 + (int)Math.Round(155 * 0.25), FanCurve.Interpolate(22.5, Simple));
        Assert.Equal(100 + (int)Math.Round(155 * 0.75), FanCurve.Interpolate(27.5, Simple));
    }

    [Fact]
    public void Result_is_always_clamped_into_0_255_even_for_out_of_range_authored_duty()
    {
        CurvePoint[] badCurve = [new(0, -50), new(10, 400)];
        Assert.Equal(0, FanCurve.Interpolate(0, badCurve));
        Assert.Equal(255, FanCurve.Interpolate(10, badCurve));
        Assert.InRange(FanCurve.Interpolate(5, badCurve), 0, 255);
    }

    [Fact]
    public void Duplicate_temperature_points_do_not_throw()
    {
        CurvePoint[] degenerate = [new(10, 50), new(10, 200), new(20, 255)];
        var ex = Record.Exception(() => FanCurve.Interpolate(10, degenerate));
        Assert.Null(ex);
    }

    [Fact]
    public void Empty_points_returns_zero_rather_than_throwing()
    {
        Assert.Equal(0, FanCurve.Interpolate(50, Array.Empty<CurvePoint>()));
    }

    [Fact]
    public void Single_point_returns_that_duty_everywhere()
    {
        CurvePoint[] one = [new(70, 128)];
        Assert.Equal(128, FanCurve.Interpolate(-10, one));
        Assert.Equal(128, FanCurve.Interpolate(70, one));
        Assert.Equal(128, FanCurve.Interpolate(200, one));
    }
}

public class FanCurveHysteresisTests
{
    private static readonly CurvePoint[] Ramp = [new(50, 60), new(80, 200)];

    [Fact]
    public void Cold_start_with_zero_last_duty_adopts_the_curve_immediately()
    {
        int d = FanCurve.DutyForTemp(65, Ramp, hysteresisC: 5, lastDuty: 0);
        Assert.Equal(FanCurve.Interpolate(65, Ramp), d);
    }

    [Fact]
    public void Rising_temperature_always_applies_immediately_no_hysteresis_delay()
    {
        // lastDuty below what the curve now wants -> rise right away regardless of hysteresis.
        int d = FanCurve.DutyForTemp(80, Ramp, hysteresisC: 20, lastDuty: 60);
        Assert.Equal(200, d);
    }

    [Fact]
    public void Cooling_within_the_hysteresis_band_holds_the_last_duty_steady()
    {
        // lastDuty=200 was set at 80°C. Cooling to 76°C (< 5°C below 80) must NOT drop yet.
        int held = FanCurve.DutyForTemp(76, Ramp, hysteresisC: 5, lastDuty: 200);
        Assert.Equal(200, held);
    }

    [Fact]
    public void Cooling_past_the_hysteresis_band_drops_to_the_curve_value()
    {
        // 80 - 5 = 75; below that it's safe to drop.
        int dropped = FanCurve.DutyForTemp(74, Ramp, hysteresisC: 5, lastDuty: 200);
        Assert.True(dropped < 200);
        Assert.Equal(FanCurve.Interpolate(74, Ramp), dropped);
    }

    [Fact]
    public void Zero_hysteresis_tracks_the_curve_instantly_in_both_directions()
    {
        int atHigh = FanCurve.DutyForTemp(80, Ramp, hysteresisC: 0, lastDuty: 60);
        Assert.Equal(200, atHigh);
        int afterCooling = FanCurve.DutyForTemp(75, Ramp, hysteresisC: 0, lastDuty: 200);
        Assert.True(afterCooling < 200);
    }

    [Fact]
    public void Negative_hysteresis_is_treated_as_zero_never_amplified()
    {
        int a = FanCurve.DutyForTemp(70, Ramp, hysteresisC: -10, lastDuty: 200);
        int b = FanCurve.DutyForTemp(70, Ramp, hysteresisC: 0, lastDuty: 200);
        Assert.Equal(b, a);
    }

    [Fact]
    public void Extreme_hysteresis_still_resolves_to_a_value_in_range()
    {
        // hysteresisC absurdly large -> fallCheck lands far past the last breakpoint (clamped there);
        // the hold decision and its result must still stay sane.
        int held = FanCurve.DutyForTemp(50, Ramp, hysteresisC: 1000, lastDuty: 200);
        Assert.InRange(held, 0, 255);
        Assert.Equal(200, held);
    }
}

public class FanCurveNamedCurvesTests
{
    private static readonly (string Name, IReadOnlyList<CurvePoint> Curve)[] All =
    [
        ("Quiet", FanCurve.Quiet),
        ("Balanced", FanCurve.Balanced),
        ("Aggressive", FanCurve.Aggressive),
    ];

    public static readonly TheoryData<string> AllNames = new() { "Quiet", "Balanced", "Aggressive" };

    [Theory]
    [MemberData(nameof(AllNames))]
    public void Every_named_curve_is_monotonic_non_decreasing_across_the_full_temp_range(string name)
    {
        var curve = All.First(c => c.Name == name).Curve;
        int prev = FanCurve.Interpolate(0, curve);
        for (double t = 0; t <= 100; t += 0.5)
        {
            int cur = FanCurve.Interpolate(t, curve);
            Assert.True(cur >= prev, $"{name} duty regressed at {t}°C: {cur} < {prev}");
            Assert.InRange(cur, 0, 255);
            prev = cur;
        }
    }

    [Fact]
    public void Quiet_is_never_zero_above_fifty_degrees()
    {
        for (double t = 50; t <= 100; t += 0.5)
            Assert.True(FanCurve.Interpolate(t, FanCurve.Quiet) > 0, $"Quiet was 0 at {t}°C");
    }

    [Fact]
    public void Quiet_has_ramped_up_hard_by_eighty_five_degrees()
    {
        // Even the quiet profile prioritizes cooling over silence once genuinely hot.
        Assert.True(FanCurve.Interpolate(85, FanCurve.Quiet) >= 200);
    }

    [Theory]
    [MemberData(nameof(AllNames))]
    public void Every_named_curve_reaches_full_duty_by_ninety_degrees(string name)
    {
        var curve = All.First(c => c.Name == name).Curve;
        Assert.Equal(255, FanCurve.Interpolate(90, curve));
    }

    [Fact]
    public void Aggressive_is_never_lower_than_quiet_or_balanced_at_the_same_temperature()
    {
        for (double t = 0; t <= 90; t += 5)
        {
            int aggr = FanCurve.Interpolate(t, FanCurve.Aggressive);
            int quiet = FanCurve.Interpolate(t, FanCurve.Quiet);
            int balanced = FanCurve.Interpolate(t, FanCurve.Balanced);
            Assert.True(aggr >= quiet, $"Aggressive < Quiet at {t}°C ({aggr} < {quiet})");
            Assert.True(aggr >= balanced, $"Aggressive < Balanced at {t}°C ({aggr} < {balanced})");
        }
    }

    [Theory]
    [InlineData("Quiet")]
    [InlineData("Balanced")]
    [InlineData("Aggressive")]
    public void ForMode_resolves_the_matching_curve(string mode)
    {
        var expected = All.First(c => c.Name == mode).Curve;
        Assert.Same(expected, FanCurve.ForMode(mode));
    }

    [Theory]
    [InlineData("Auto")]
    [InlineData("Manual")]
    [InlineData("")]
    [InlineData("unknown")]
    public void ForMode_returns_null_for_modes_without_a_curve(string mode)
    {
        Assert.Null(FanCurve.ForMode(mode));
    }
}
