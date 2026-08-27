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
    public async Task Telemetry_fan_rpm_stays_zero_when_source_returns_null_or_absent()
    {
        Assert.Equal(0, (await new WmiTelemetryService().ReadAsync(CancellationToken.None)).FanRpm);
        var withNull = new WmiTelemetryService(sensors: null, fanRpmSource: new FakeFanRpm(null));
        Assert.Equal(0, (await withNull.ReadAsync(CancellationToken.None)).FanRpm);
    }
}
