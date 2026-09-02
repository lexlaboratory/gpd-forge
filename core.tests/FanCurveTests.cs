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

    // -------------------------------------------------------------------------------------------
    // The precondition Interpolate documents, and what it costs when it is false.
    //
    // Interpolate's summary says points "must be sorted ascending by TempC — true of the three
    // curves above". Until 2026-09-02 nothing checked that: it was an invariant asserted in a
    // comment, held only by the three list literals happening to be written in order.
    //
    // There is NO validation to add at a store boundary, because there is no store — CurvePoint is
    // constructed nowhere but FanCurve.cs, and POST /fan takes a mode and a duty, never a curve. So
    // the only way this can break is someone editing those literals, and the only useful guard is
    // one that reads them. Sorting inside Interpolate would be paying every worker tick to defend
    // against an input that cannot vary at runtime.
    // -------------------------------------------------------------------------------------------

    public static TheoryData<string> ShippedCurves => new() { "Quiet", "Balanced", "Aggressive" };

    [Theory]
    [MemberData(nameof(ShippedCurves))]
    public void Every_shipped_curve_satisfies_the_precondition_Interpolate_documents(string mode)
    {
        var points = FanCurve.ForMode(mode);
        Assert.NotNull(points);
        Assert.NotEmpty(points!);

        for (int i = 1; i < points!.Count; i++)
            Assert.True(points[i].TempC > points[i - 1].TempC,
                $"{mode} point {i} is at {points[i].TempC}°C, not above the previous {points[i - 1].TempC}°C. " +
                "Interpolate reads these in order and does not sort; see the test below for what that costs.");

        Assert.All(points, p => Assert.InRange(p.Duty, 0, 255));

        // The last point is what every temperature above it clamps to, which on a thermal path means
        // it is the duty the fan holds at 95°C and at 105°C alike.
        Assert.Equal(255, points[^1].Duty);
    }

    [Fact]
    public void An_out_of_order_curve_silently_caps_the_fan_below_full_when_hot()
    {
        // Quiet with only its last two points transposed — a plausible editing slip, and one that
        // no existing test would have caught: every other case here uses curves already in order.
        CurvePoint[] slipped =
        [
            new(0, 0), new(45, 0), new(50, 55), new(60, 90),
            new(70, 130), new(80, 190), new(90, 255), new(85, 235),
        ];

        // Because the clamp for "at or above the last point" reads points[^1] positionally rather
        // than the hottest point, everything from 85°C upward pins to 235.
        Assert.Equal(255, FanCurve.Interpolate(99, FanCurve.Quiet));
        Assert.Equal(235, FanCurve.Interpolate(99, slipped));

        // Not a rounding difference — the fan never reaches full duty at any temperature at all.
        var hottest = Enumerable.Range(0, 130).Max(t => FanCurve.Interpolate(t, slipped));
        Assert.Equal(235, hottest);
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
