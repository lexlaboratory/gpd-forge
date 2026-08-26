// GPD Forge — tablet-mode advisory + gated registry toggle tests. GPL-3.0-or-later.
using GpdForge.Display;
using Xunit;

namespace GpdForge.Core.Tests;

public class TabletModeAdvisorTests
{
    [Fact]
    public void Unset_value_maps_to_a_null_convertible_state()
    {
        Assert.Null(TabletModeAdvisor.ToConvertible(null));
    }

    [Fact]
    public void Zero_maps_to_not_convertible()
    {
        Assert.False(TabletModeAdvisor.ToConvertible(0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    public void Nonzero_maps_to_convertible(int raw)
    {
        Assert.True(TabletModeAdvisor.ToConvertible(raw));
    }

    [Fact]
    public void Describe_mentions_the_fix_when_zero()
    {
        Assert.Contains("NOT convertible", TabletModeAdvisor.Describe(0));
    }

    [Fact]
    public void Describe_explains_default_detection_when_unset()
    {
        var msg = TabletModeAdvisor.Describe(null);
        Assert.Contains("not set", msg);
        Assert.Contains("maximized", msg);
    }

    [Fact]
    public void Describe_reports_the_raw_value_when_convertible()
    {
        Assert.Contains("= 1", TabletModeAdvisor.Describe(1));
    }
}

public class TabletModeServiceTests
{
    private sealed class FakeTabletModeRegistry : ITabletModeRegistry
    {
        public int? Value;
        public bool WriteResult = true;
        public int WriteCalls { get; private set; }
        public int? LastWritten { get; private set; }

        public int? Read() => Value;

        public bool Write(int value)
        {
            WriteCalls++;
            LastWritten = value;
            if (WriteResult) Value = value;
            return WriteResult;
        }
    }

    [Fact]
    public void Get_never_writes_and_reports_applied_false()
    {
        var reg = new FakeTabletModeRegistry { Value = 0 };
        var svc = new TabletModeService(reg, hardwareGateOpen: true);

        var s = svc.Get();

        Assert.False(s.Applied);
        Assert.Equal(0, reg.WriteCalls);
        Assert.False(s.Convertible);
        Assert.Equal(0, s.Raw);
    }

    [Fact]
    public void Set_with_the_gate_closed_never_touches_the_registry()
    {
        var reg = new FakeTabletModeRegistry { Value = null };
        var svc = new TabletModeService(reg, hardwareGateOpen: false);

        var s = svc.Set(false);

        Assert.False(s.Applied);
        Assert.Equal(0, reg.WriteCalls);
        Assert.Null(reg.Value); // untouched
        Assert.Equal(TabletModeAdvisor.GateClosedAdvisory, s.Advisory);
    }

    [Fact]
    public void Set_with_the_gate_open_writes_zero_for_disable()
    {
        var reg = new FakeTabletModeRegistry { Value = 1 };
        var svc = new TabletModeService(reg, hardwareGateOpen: true);

        var s = svc.Set(false);

        Assert.True(s.Applied);
        Assert.Equal(1, reg.WriteCalls);
        Assert.Equal(0, reg.LastWritten);
        Assert.False(s.Convertible);
    }

    [Fact]
    public void Set_with_the_gate_open_writes_one_for_enable()
    {
        var reg = new FakeTabletModeRegistry { Value = 0 };
        var svc = new TabletModeService(reg, hardwareGateOpen: true);

        var s = svc.Set(true);

        Assert.True(s.Applied);
        Assert.Equal(1, reg.LastWritten);
        Assert.True(s.Convertible);
    }

    [Fact]
    public void Set_reports_applied_false_when_the_write_fails()
    {
        var reg = new FakeTabletModeRegistry { Value = 1, WriteResult = false };
        var svc = new TabletModeService(reg, hardwareGateOpen: true);

        var s = svc.Set(false);

        Assert.False(s.Applied);
        Assert.Equal(TabletModeAdvisor.WriteFailedAdvisory, s.Advisory);
        Assert.Equal(1, s.Raw); // registry never actually changed
    }
}
