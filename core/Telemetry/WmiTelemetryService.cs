// GPD Forge — read-only telemetry via WMI. GPL-3.0-or-later.
//
// NO kernel driver: this reads only what WMI exposes (battery, AC, discharge, CPU clock,
// ACPI thermal zone). Package power (RAPL), per-core temps and fan RPM come from the optional
// PawnIO/LHM sensors, and FPS from the optional PresentMon probe. Each is injected only when its
// gate is on; whatever is absent stays 0 and is reported as "n/a" — never guessed.
using System.Management;
using GpdForge.Fan;
using Microsoft.Extensions.Logging;

namespace GpdForge.Telemetry;

public sealed class WmiTelemetryService(
    IHardwareSensors? sensors = null,
    IFanRpm? fanRpmSource = null,
    IFrameRateProbe? frameRateProbe = null,
    // Optional so every existing test that news this up directly keeps working — and when it is
    // absent TdpVerified is null, which is the correct answer for "nobody is tracking TDP here".
    GpdForge.Tdp.TdpState? tdpState = null,
    ILogger<WmiTelemetryService>? logger = null) : ITelemetryService
{
    public Task<TelemetrySnapshot> ReadAsync(CancellationToken ct)
    {
        // `?? 0` on every one of these until 2026-09-01. A failed read became a confident zero, and
        // a CPU reported at 0 °C is worse than no reading at all: the panel cannot tell it from cold,
        // and the guardian cannot tell it from safe.
        double? cpuTempC = ReadThermalZoneC();
        int? cpuClockMhz = ReadCpuClockMhz();
        var (batteryPct, acConnected) = ReadBattery();
        double? dischargeW = ReadDischargeW();

        // Package power / GPU temp / fan RPM need a driver. Filled by the optional read-only LHM
        // sensors when hardware access is enabled; NULL otherwise, because WMI genuinely cannot
        // provide them and saying "0 W" would be inventing a measurement.
        double? gpuTempC = null, packageW = null;
        int? fanRpm = null;
        double? fps = null, fps1PctLow = null;

        // Fan duty is not measured anywhere yet — it was a `const int fanDutyPct = 0` presented as a
        // reading. Null until something actually reads the EC's duty register back.
        int? fanDutyPct = null;

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

        // Frame rate via the optional PresentMon probe. Null in BOTH the no-probe and the
        // probe-with-no-sample cases, and that is the honest reading rather than a shortcut: a probe
        // returning no sample does not distinguish "nothing is presenting frames" from "PresentMon
        // has not produced a window of data yet". Reporting 0.0 would assert the first when only the
        // second is known. A genuine 0.0 still arrives when the probe measures one.
        if (frameRateProbe is not null && frameRateProbe.TryRead(out var frames))
        {
            fps = frames.Fps;
            fps1PctLow = frames.Fps1PctLow;
        }

        // Was `TdpVerified: true` — a literal, on every snapshot, regardless of whether anything had
        // ever written a power limit or whether the write was confirmed. Now it reports what the last
        // write actually observed, and null when there has not been one.
        var snapshot = new TelemetrySnapshot(
            cpuTempC, gpuTempC, packageW, cpuClockMhz, fanRpm, fanDutyPct,
            fps, fps1PctLow, batteryPct, dischargeW, acConnected,
            TdpVerified: tdpState?.Last?.Verified);

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

    /// <summary>
    /// Charge percentage and whether we are on mains.
    ///
    /// The percentage is nullable because the old fallback of <c>0</c> was the most dangerous zero in
    /// this file: the guardian raises a CRITICAL battery alert below 8 %, so a failed WMI query
    /// announced an emergency on a machine that might be at 90 %.
    ///
    /// <c>acConnected</c> stays a plain bool and falls back to <c>false</c>. That is a deliberate
    /// asymmetry rather than an oversight: every consumer treats "on battery" as the more
    /// conservative state, so an unknown power source behaves cautiously instead of assuming mains.
    /// </summary>
    private (int? pct, bool ac) ReadBattery()
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
        return (null, false);
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
