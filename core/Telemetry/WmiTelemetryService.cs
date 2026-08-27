// GPD Forge — read-only telemetry via WMI. GPL-3.0-or-later.
//
// NO kernel driver: this reads only what WMI exposes (battery, AC, discharge, CPU clock,
// ACPI thermal zone). Package power (RAPL), per-core temps, fan RPM and FPS need the PawnIO
// broker / PresentMon and are filled in later, behind hardware-access approval — they stay 0
// here and are reported as "n/a".
using System.Management;
using GpdForge.Fan;
using Microsoft.Extensions.Logging;

namespace GpdForge.Telemetry;

public sealed class WmiTelemetryService(
    IHardwareSensors? sensors = null,
    IFanRpm? fanRpmSource = null,
    ILogger<WmiTelemetryService>? logger = null) : ITelemetryService
{
    public Task<TelemetrySnapshot> ReadAsync(CancellationToken ct)
    {
        double cpuTempC = ReadThermalZoneC() ?? 0;
        int cpuClockMhz = ReadCpuClockMhz() ?? 0;
        var (batteryPct, acConnected) = ReadBattery();
        double dischargeW = ReadDischargeW() ?? 0;

        // Package power / GPU temp / fan RPM need a driver. Filled by the optional read-only
        // LHM sensors when hardware access is enabled; otherwise 0 (WMI can't provide them).
        double gpuTempC = 0, packageW = 0;
        int fanRpm = 0;
        const double fps = 0, fps1PctLow = 0;
        const int fanDutyPct = 0;

        if (sensors is not null && sensors.TryRead(out var hw))
        {
            if (hw.PackageW > 0) packageW = hw.PackageW;
            if (hw.GpuTempC > 0) gpuTempC = hw.GpuTempC;
            if (hw.CpuTempC > 0) cpuTempC = hw.CpuTempC; // LHM per-core temp beats the ACPI zone
            if (hw.FanRpm > 0) fanRpm = hw.FanRpm;
        }

        // Real GPD fan RPM via the PawnIO EC read (LHM doesn't expose it). Read-only; only present
        // when hardware access is enabled. Wins over LHM's fan reading (which is 0 on these boards).
        if (fanRpmSource?.ReadRpm() is int rpm && rpm > 0) fanRpm = rpm;

        var snapshot = new TelemetrySnapshot(
            cpuTempC, gpuTempC, packageW, cpuClockMhz, fanRpm, fanDutyPct,
            fps, fps1PctLow, batteryPct, dischargeW, acConnected, TdpVerified: true);

        return Task.FromResult(snapshot);
    }

    /// <summary>ACPI thermal zone reports tenths of a Kelvin. Pure + unit-tested.</summary>
    public static double KelvinTenthsToCelsius(double tenthsKelvin) => tenthsKelvin / 10.0 - 273.15;

    private double? ReadThermalZoneC()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    var raw = Convert.ToDouble(mo["CurrentTemperature"]);
                    return Math.Round(KelvinTenthsToCelsius(raw), 1);
                }
            }
        }
        catch (Exception ex) { logger?.LogDebug(ex, "thermal zone unavailable (needs elevation/ACPI support)"); }
        return null;
    }

    private int? ReadCpuClockMhz()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT CurrentClockSpeed FROM Win32_Processor");
            foreach (var mo in searcher.Get())
            {
                using (mo)
                    return Convert.ToInt32(mo["CurrentClockSpeed"]);
            }
        }
        catch (Exception ex) { logger?.LogDebug(ex, "cpu clock unavailable"); }
        return null;
    }

    private (int pct, bool ac) ReadBattery()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    int pct = Convert.ToInt32(mo["EstimatedChargeRemaining"]);
                    // Win32_Battery.BatteryStatus: 1 = discharging (on battery), 2 = AC line.
                    int status = Convert.ToInt32(mo["BatteryStatus"]);
                    return (pct, status == 2);
                }
            }
        }
        catch (Exception ex) { logger?.LogDebug(ex, "battery unavailable"); }
        return (0, false);
    }

    private double? ReadDischargeW()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT DischargeRate FROM BatteryStatus");
            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    var mW = Convert.ToDouble(mo["DischargeRate"]); // milliwatts
                    return Math.Round(mW / 1000.0, 1);
                }
            }
        }
        catch (Exception ex) { logger?.LogDebug(ex, "discharge rate unavailable"); }
        return null;
    }
}
