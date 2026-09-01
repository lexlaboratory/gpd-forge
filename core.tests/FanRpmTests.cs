// GPD Forge — telemetry fan-RPM wiring tests. GPL-3.0-or-later.
using System.Threading;
using System.Threading.Tasks;
using GpdForge.Fan;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class FanRpmTests
{
    private sealed class FakeFanRpm(int? rpm) : IFanRpm
    {
        public int? ReadRpm() => rpm;
        public GpdFanDevice? Device => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task Telemetry_reports_the_pawnio_fan_rpm_when_a_source_is_present()
    {
        var svc = new WmiTelemetryService(sensors: null, fanRpmSource: new FakeFanRpm(4608));
        var s = await svc.ReadAsync(CancellationToken.None);
        Assert.Equal(4608, s.FanRpm);
    }

    [Fact]
    public async Task Telemetry_fan_rpm_is_null_when_the_source_returns_nothing_or_is_absent()
    {
        // Was "stays zero" until 2026-09-01. Zero RPM is a real and alarming reading — it is what the
        // health check watches for to detect a fan that has stopped while the CPU is warm — so
        // emitting it for "no EC fan source is wired" meant an unconfigured machine looked exactly
        // like one with a dead fan.
        Assert.Null((await new WmiTelemetryService().ReadAsync(CancellationToken.None)).FanRpm);

        var withNull = new WmiTelemetryService(sensors: null, fanRpmSource: new FakeFanRpm(null));
        Assert.Null((await withNull.ReadAsync(CancellationToken.None)).FanRpm);
    }
}
