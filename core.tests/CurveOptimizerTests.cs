// GPD Forge — Curve Optimizer / undervolt validator + service tests. GPL-3.0-or-later.
using GpdForge.Undervolt;
using Xunit;

namespace GpdForge.Core.Tests;

public class CurveOptimizerValidatorTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-30, -30)]
    [InlineData(30, 30)]
    [InlineData(-31, -30)]
    [InlineData(31, 30)]
    [InlineData(-1000, -30)]
    [InlineData(1000, 30)]
    public void ClampCoCount_clamps_into_the_documented_band(int input, int expected)
    {
        Assert.Equal(expected, CurveOptimizerValidator.ClampCoCount(input));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-100, -100)]
    [InlineData(100, 100)]
    [InlineData(-101, -100)]
    [InlineData(101, 100)]
    public void ClampOffsetMv_clamps_into_the_documented_band(int input, int expected)
    {
        Assert.Equal(expected, CurveOptimizerValidator.ClampOffsetMv(input));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(-30, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(30, false)]
    public void IsUndervolt_is_true_only_for_negative_counts(int count, bool expected)
    {
        Assert.Equal(expected, CurveOptimizerValidator.IsUndervolt(count));
    }
}

public class CurveOptimizerServiceTests
{
    [Fact]
    public void Get_defaults_to_zero_and_is_never_applied()
    {
        var svc = new CurveOptimizerService(hardwareGateOpen: false);

        var s = svc.Get();

        Assert.Equal(0, s.CoCount);
        Assert.Equal(0, s.OffsetMv);
        Assert.False(s.Applied);
        Assert.Equal(CurveOptimizerAdvisor.GateClosedAdvisory, s.Advisory);
    }

    [Fact]
    public void Set_with_the_gate_closed_stores_the_clamped_value_and_is_never_applied()
    {
        var svc = new CurveOptimizerService(hardwareGateOpen: false);

        var s = svc.Set(coCount: -1000, offsetMv: null);

        Assert.Equal(-30, s.CoCount);
        Assert.False(s.Applied);
        Assert.Equal(CurveOptimizerAdvisor.GateClosedAdvisory, s.Advisory);
    }

    [Fact]
    public void Set_with_the_gate_open_is_still_never_applied_because_RyzenAdj_has_no_CO_path()
    {
        var svc = new CurveOptimizerService(hardwareGateOpen: true);

        var s = svc.Set(coCount: -10, offsetMv: -20);

        Assert.Equal(-10, s.CoCount);
        Assert.Equal(-20, s.OffsetMv);
        Assert.False(s.Applied);
        Assert.Equal(CurveOptimizerAdvisor.NoBackendAdvisory, s.Advisory);
    }

    [Fact]
    public void Set_with_a_null_field_leaves_the_other_stored_value_untouched()
    {
        var svc = new CurveOptimizerService(hardwareGateOpen: false);

        svc.Set(coCount: -15, offsetMv: -25);
        var s = svc.Set(coCount: null, offsetMv: null);

        Assert.Equal(-15, s.CoCount);
        Assert.Equal(-25, s.OffsetMv);
    }
}
