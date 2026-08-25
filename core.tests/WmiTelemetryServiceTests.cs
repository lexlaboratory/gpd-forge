// GPD Forge — telemetry tests. GPL-3.0-or-later.
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class WmiTelemetryServiceTests
{
    [Theory]
    [InlineData(2731.5, 0.0)]     // 273.15 K = 0 °C
    [InlineData(3031.5, 30.0)]    // 303.15 K = 30 °C
    [InlineData(3131.5, 40.0)]
    public void KelvinTenths_converts_to_celsius(double tenths, double expectedC)
    {
        Assert.Equal(expectedC, WmiTelemetryService.KelvinTenthsToCelsius(tenths), precision: 2);
    }

    [Fact]
    public async Task ReadAsync_returns_a_snapshot_without_throwing()
    {
        // Read-only; on machines lacking a sensor/class the field degrades to 0 rather than throwing.
        var svc = new WmiTelemetryService();
        var snap = await svc.ReadAsync(CancellationToken.None);

        Assert.InRange(snap.BatteryPct, 0, 100);
        Assert.True(snap.CpuTempC >= 0);
    }
}
