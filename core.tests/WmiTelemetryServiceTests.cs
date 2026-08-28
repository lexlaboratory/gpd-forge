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

    /// <summary>Same shape as FakeFanRpm in FanRpmTests: an optional sensor that may say "nothing".</summary>
    private sealed class FakeFrameRateProbe(FpsSample? sample) : IFrameRateProbe
    {
        public bool TryRead(out FpsSample s) { s = sample ?? default; return sample.HasValue; }
        public void Dispose() { }
    }

    [Fact]
    public async Task Reports_the_frame_rate_when_a_probe_supplies_one()
    {
        var svc = new WmiTelemetryService(
            frameRateProbe: new FakeFrameRateProbe(new FpsSample(58.5, 41.2, "game.exe")));
        var snap = await svc.ReadAsync(CancellationToken.None);

        Assert.Equal(58.5, snap.Fps, 1);
        Assert.Equal(41.2, snap.Fps1PctLow, 1);
    }

    [Fact]
    public async Task Falls_back_to_zero_when_the_probe_has_no_reading()
    {
        // Nothing is rendering. That must read as "no FPS" (0), never as a stale or invented value.
        var svc = new WmiTelemetryService(frameRateProbe: new FakeFrameRateProbe(null));
        var snap = await svc.ReadAsync(CancellationToken.None);

        Assert.Equal(0, snap.Fps);
        Assert.Equal(0, snap.Fps1PctLow);
    }

    [Fact]
    public async Task Reports_zero_fps_when_no_probe_is_registered_at_all()
    {
        var snap = await new WmiTelemetryService().ReadAsync(CancellationToken.None);
        Assert.Equal(0, snap.Fps);
    }
}
