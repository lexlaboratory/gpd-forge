// GPD Forge — how often WmiTelemetryService actually queries WMI. GPL-3.0-or-later.
//
// Measured 2026-09-24: one ReadAsync built four fresh ManagementObjectSearchers and ran all four
// queries, every call, from six callers. Battery charge and the ACPI thermal zone do not change
// meaningfully in a second, so they are now read on a 5 s cadence and served from cache in between.
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class WmiTelemetryCadenceTests
{
    private static (WmiTelemetryService svc, FakeWmiQueries wmi, ManualTimeProvider time) Build(IHardwareSensors? sensors = null)
    {
        var wmi = new FakeWmiQueries();
        var time = new ManualTimeProvider();
        return (new WmiTelemetryService(sensors: sensors, wmi: wmi, time: time), wmi, time);
    }

    private static Task<TelemetrySnapshot> Read(WmiTelemetryService s) => s.ReadAsync(CancellationToken.None);

    [Fact]
    public async Task Battery_discharge_and_thermal_zone_are_read_once_per_five_seconds()
    {
        var (svc, wmi, time) = Build();

        for (int i = 0; i < 5; i++) { await Read(svc); time.Advance(TimeSpan.FromSeconds(0.99)); }
        Assert.Equal(1, wmi.BatteryReads);
        Assert.Equal(1, wmi.DischargeReads);
        Assert.Equal(1, wmi.ThermalReads);

        time.Advance(TimeSpan.FromSeconds(0.1));   // 5.05 s since the first read
        await Read(svc);
        Assert.Equal(2, wmi.BatteryReads);
        Assert.Equal(2, wmi.DischargeReads);
        Assert.Equal(2, wmi.ThermalReads);
    }

    [Fact]
    public async Task Cached_values_are_served_between_reads_not_dropped_to_null()
    {
        var (svc, wmi, time) = Build();
        var first = await Read(svc);

        wmi.Battery = new WmiBatteryReading(10, Ac: true);   // changes that land between cadence ticks
        time.Advance(TimeSpan.FromSeconds(2));
        var cached = await Read(svc);

        Assert.Equal(80, first.BatteryPct);
        Assert.Equal(80, cached.BatteryPct);
        Assert.False(cached.AcConnected);
        Assert.Equal(12.3, cached.DischargeW);
        Assert.Equal(50.0, cached.CpuTempC!.Value, 1);

        time.Advance(TimeSpan.FromSeconds(3));
        var fresh = await Read(svc);
        Assert.Equal(10, fresh.BatteryPct);
        Assert.True(fresh.AcConnected);
    }

    [Fact]
    public async Task A_suspend_forces_a_fresh_battery_read_even_if_the_monotonic_clock_slept_through_it()
    {
        var (svc, wmi, time) = Build();
        await Read(svc);

        wmi.Battery = new WmiBatteryReading(72, Ac: false);
        time.AdvanceWallOnly(TimeSpan.FromHours(8));

        Assert.Equal(72, (await Read(svc)).BatteryPct);
        Assert.Equal(2, wmi.BatteryReads);
    }

    [Fact]
    public async Task The_thermal_zone_is_not_queried_while_LHM_supplies_a_cpu_temperature()
    {
        var (svc, wmi, time) = Build(new FakeHardwareSensors(new HwSample(15, 71.5, 60, 0, CpuClockMhz: 3100)));

        for (int i = 0; i < 12; i++) { await Read(svc); time.Advance(TimeSpan.FromSeconds(1)); }

        Assert.Equal(0, wmi.ThermalReads);
    }

    [Fact]
    public async Task The_LHM_effective_clock_is_reported_and_Win32_is_never_asked()
    {
        var (svc, wmi, _) = Build(new FakeHardwareSensors(new HwSample(15, 71.5, 60, 0, CpuClockMhz: 3100.4)));

        var snap = await Read(svc);

        Assert.Equal(3100, snap.CpuClockMhz);
        Assert.Equal(0, wmi.ClockReads);
    }

    [Fact]
    public async Task A_static_Win32_clock_is_reported_as_null()
    {
        // The HX 370 case with the hardware gate closed: CurrentClockSpeed is the 2000 MHz base
        // clock, forever. Reporting it would be a plausible, confident, wrong number.
        var (svc, wmi, time) = Build();
        for (int i = 0; i < 10; i++)
        {
            Assert.Null((await Read(svc)).CpuClockMhz);
            time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.True(wmi.ClockReads >= 2, "the fallback must keep being consulted to notice movement");
    }

    [Fact]
    public async Task A_Win32_clock_that_moves_is_reported()
    {
        var (svc, wmi, time) = Build();
        await Read(svc);
        time.Advance(TimeSpan.FromSeconds(1));
        wmi.ClockMhz = 2900;

        Assert.Equal(2900, (await Read(svc)).CpuClockMhz);
    }

    [Fact]
    public async Task Failed_WMI_reads_stay_null_and_the_battery_falls_back_to_on_battery()
    {
        var (svc, wmi, _) = Build();
        wmi.Battery = null; wmi.DischargeMw = null; wmi.ThermalTenthsKelvin = null; wmi.ClockMhz = null;

        var snap = await Read(svc);

        Assert.Null(snap.BatteryPct);
        Assert.False(snap.AcConnected);
        Assert.Null(snap.DischargeW);
        Assert.Null(snap.CpuTempC);
        Assert.Null(snap.CpuClockMhz);
    }
}
