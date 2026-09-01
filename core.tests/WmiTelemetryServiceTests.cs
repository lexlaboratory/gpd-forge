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
        // Read-only; on machines lacking a sensor/class the field degrades to NULL rather than
        // throwing — and rather than to 0, which is what it used to do until 2026-09-01. A battery
        // reported at 0 % is an emergency; a CPU at 0 °C is impossible. Both were being emitted by a
        // failed WMI query.
        var svc = new WmiTelemetryService();
        var snap = await svc.ReadAsync(CancellationToken.None);

        if (snap.BatteryPct is int pct) Assert.InRange(pct, 1, 100);
        if (snap.CpuTempC is double temp) Assert.True(temp > 0, "A reported temperature must be a real one.");
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

        Assert.Equal(58.5, snap.Fps!.Value, 1);
        Assert.Equal(41.2, snap.Fps1PctLow!.Value, 1);
    }

    [Fact]
    public async Task Reports_null_when_the_probe_has_no_reading()
    {
        // Changed from "falls back to 0" on 2026-09-01, and the distinction is the point: a probe
        // with no sample does not distinguish "nothing is presenting frames" from "PresentMon has not
        // produced a window of data yet". Reporting 0 asserts the first when only the second is
        // known, and never as a stale or invented value either.
        var svc = new WmiTelemetryService(frameRateProbe: new FakeFrameRateProbe(null));
        var snap = await svc.ReadAsync(CancellationToken.None);

        Assert.Null(snap.Fps);
        Assert.Null(snap.Fps1PctLow);
    }

    [Fact]
    public async Task A_measured_zero_is_reported_as_zero_not_as_null()
    {
        // The other half, and why this is not just "null everywhere": when the probe DOES measure and
        // the answer is zero frames, that is a fact worth reporting. Collapsing it into null would
        // lose as much information as the bug this change fixed.
        var svc = new WmiTelemetryService(frameRateProbe: new FakeFrameRateProbe(new FpsSample(0, 0, "idle.exe")));
        var snap = await svc.ReadAsync(CancellationToken.None);

        Assert.Equal(0, snap.Fps);
    }

    [Fact]
    public async Task Reports_null_fps_when_no_probe_is_registered_at_all()
    {
        var snap = await new WmiTelemetryService().ReadAsync(CancellationToken.None);
        Assert.Null(snap.Fps);
    }
}
