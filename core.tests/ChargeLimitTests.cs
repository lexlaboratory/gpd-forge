// GPD Forge — battery charge-limit validator + service tests. GPL-3.0-or-later.
using GpdForge.Battery;
using Xunit;

namespace GpdForge.Core.Tests;

public class ChargeLimitValidatorTests
{
    [Theory]
    [InlineData(0, 50)]
    [InlineData(49, 50)]
    [InlineData(50, 50)]
    [InlineData(75, 75)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(1000, 100)]
    [InlineData(-20, 50)]
    public void Normalize_clamps_into_the_50_to_100_band(int input, int expected)
    {
        Assert.Equal(expected, ChargeLimitValidator.Normalize(input));
    }
}

public class UnavailableChargeLimitBackendTests
{
    [Fact]
    public void Read_and_Write_are_both_honestly_unavailable()
    {
        var backend = new UnavailableChargeLimitBackend();
        Assert.Null(backend.Read());
        Assert.False(backend.Write(80));
    }
}

public class ChargeLimitServiceTests
{
    private sealed class FakeBackend : IChargeLimitBackend
    {
        public int? Value;
        public bool WriteResult = true;
        public int WriteCalls { get; private set; }
        public int? LastWritten { get; private set; }

        public int? Read() => Value;

        public bool Write(int percent)
        {
            WriteCalls++;
            LastWritten = percent;
            if (WriteResult) Value = percent;
            return WriteResult;
        }
    }

    [Fact]
    public void Get_reports_unavailable_when_the_backend_cannot_read()
    {
        var backend = new FakeBackend { Value = null };
        var svc = new ChargeLimitService(backend, hardwareGateOpen: true);

        var s = svc.Get();

        Assert.False(s.Available);
        Assert.False(s.Applied);
        Assert.Equal(ChargeLimitAdvisor.UnavailableReadAdvisory, s.Advisory);
    }

    [Fact]
    public void Get_reports_the_live_value_when_the_backend_can_read()
    {
        var backend = new FakeBackend { Value = 80 };
        var svc = new ChargeLimitService(backend, hardwareGateOpen: true);

        var s = svc.Get();

        Assert.True(s.Available);
        Assert.Equal(80, s.Percent);
    }

    [Fact]
    public void Set_with_the_gate_closed_never_calls_the_backend_write()
    {
        var backend = new FakeBackend();
        var svc = new ChargeLimitService(backend, hardwareGateOpen: false);

        var s = svc.Set(80);

        Assert.Equal(0, backend.WriteCalls);
        Assert.False(s.Applied);
        Assert.Equal(80, s.Percent);
        Assert.Equal(ChargeLimitAdvisor.GateClosedAdvisory, s.Advisory);
    }

    [Fact]
    public void Set_normalizes_out_of_band_percentages_before_storing()
    {
        var backend = new FakeBackend();
        var svc = new ChargeLimitService(backend, hardwareGateOpen: false);

        var s = svc.Set(10);

        Assert.Equal(50, s.Percent);
    }

    [Fact]
    public void Set_with_the_gate_open_attempts_a_write_and_stays_honest_when_it_fails()
    {
        var backend = new FakeBackend { WriteResult = false };
        var svc = new ChargeLimitService(backend, hardwareGateOpen: true);

        var s = svc.Set(70);

        Assert.Equal(1, backend.WriteCalls);
        Assert.Equal(70, backend.LastWritten);
        Assert.False(s.Applied);
        Assert.Equal(ChargeLimitAdvisor.WriteFailedAdvisory, s.Advisory);
    }

    [Fact]
    public void Set_with_the_gate_open_and_a_successful_write_reports_applied_true()
    {
        var backend = new FakeBackend { WriteResult = true };
        var svc = new ChargeLimitService(backend, hardwareGateOpen: true);

        var s = svc.Set(60);

        Assert.True(s.Applied);
        Assert.True(s.Available);
    }
}
