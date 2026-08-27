// GPD Forge — fan-control safety policy tests. GPL-3.0-or-later.
using GpdForge.Fan;
using Xunit;

namespace GpdForge.Core.Tests;

public class FanControlPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Hardware_and_fan_gates_must_both_be_open(bool hardware, bool fan, bool expected)
    {
        Assert.Equal(expected, FanControlPolicy.IsGateOpen(hardware, fan));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Missing_or_non_finite_temperature_is_never_safe_for_a_manual_curve(double tempC)
    {
        Assert.False(FanControlPolicy.IsUsableTemperature(tempC));
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(45)]
    [InlineData(105)]
    public void Positive_finite_temperature_is_usable(double tempC)
    {
        Assert.True(FanControlPolicy.IsUsableTemperature(tempC));
    }

    [Theory]
    [InlineData("Auto")]
    [InlineData("Quiet")]
    [InlineData("Balanced")]
    [InlineData("Aggressive")]
    [InlineData("Manual")]
    public void Public_fan_modes_are_accepted(string mode)
    {
        Assert.True(FanControlPolicy.IsValidMode(mode));
    }

    [Theory]
    [InlineData("")]
    [InlineData("manual")]
    [InlineData("Turbo")]
    [InlineData(" Auto")]
    public void Unknown_or_case_mismatched_fan_modes_are_rejected(string mode)
    {
        Assert.False(FanControlPolicy.IsValidMode(mode));
    }
}
