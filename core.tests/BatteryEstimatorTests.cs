// GPD Forge — battery budget estimator tests. GPL-3.0-or-later.
//
// Only the pure math (BatteryEstimator.Estimate/Project) is tested here. BatteryService itself
// reads live WMI and is environment-dependent (battery presence, AC state), so it is exercised
// manually (e.g. a future --probe-battery) rather than asserted on in CI — same policy the repo
// already applies to WmiTelemetryService's WMI-backed reads vs. its pure KelvinTenthsToCelsius.
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class BatteryEstimatorTests
{
    [Theory]
    [InlineData(40.0, 20.0, 120)]   // 40 Wh / 20 W * 60 = 120 min
    [InlineData(60.0, 12.0, 300)]   // 60 Wh / 12 W * 60 = 300 min
    [InlineData(0.0, 10.0, 0)]      // empty battery, still discharging -> 0 min, not null
    public void Estimate_divides_remaining_watt_hours_by_discharge_rate(double remainingWh, double dischargeW, int expectedMinutes)
    {
        Assert.Equal(expectedMinutes, BatteryEstimator.Estimate(remainingWh, dischargeW));
    }

    [Theory]
    [InlineData(0.0)]    // on AC: live discharge rate reads 0
    [InlineData(-5.0)]   // charging: some drivers report a negative discharge rate
    public void Estimate_returns_null_when_not_discharging(double dischargeW)
    {
        Assert.Null(BatteryEstimator.Estimate(40.0, dischargeW));
    }

    [Fact]
    public void Project_lower_tdp_yields_more_minutes_than_higher_tdp()
    {
        var projections = BatteryEstimator.Project(40.0, [12, 25]);

        int minutesAt12W = projections.Single(p => p.Watts == 12).Minutes;
        int minutesAt25W = projections.Single(p => p.Watts == 25).Minutes;

        Assert.True(minutesAt12W > minutesAt25W);
    }

    [Fact]
    public void Project_computes_minutes_per_watt_level()
    {
        var projections = BatteryEstimator.Project(60.0, [12, 20]);

        Assert.Equal(2, projections.Count);
        Assert.Equal(new Projection(12, 300), projections[0]);  // 60 / 12 * 60 = 300
        Assert.Equal(new Projection(20, 180), projections[1]);  // 60 / 20 * 60 = 180
    }

    [Fact]
    public void Project_default_overload_uses_the_default_tdp_levels()
    {
        var projections = BatteryEstimator.Project(40.0);

        Assert.Equal(BatteryEstimator.DefaultTdpWatts.Length, projections.Count);
        Assert.Equal(BatteryEstimator.DefaultTdpWatts, projections.Select(p => p.Watts));
    }

    [Fact]
    public void Project_guards_against_a_zero_watt_entry()
    {
        var projections = BatteryEstimator.Project(40.0, [0]);

        Assert.Equal(0, projections[0].Minutes);
    }

    [Fact]
    public void Project_throws_on_null_tdp_array()
    {
        Assert.Throws<ArgumentNullException>(() => BatteryEstimator.Project(40.0, null!));
    }
}
