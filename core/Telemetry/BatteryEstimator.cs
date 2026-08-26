// GPD Forge — battery budget: minutes remaining now + per-TDP projections. GPL-3.0-or-later.
//
// BatteryEstimator.Estimate/Project are pure functions (no I/O) — unit-tested directly in
// core.tests/BatteryEstimatorTests.cs. BatteryService reads the real numbers from WMI
// (root\WMI BatteryStatus for RemainingCapacity/Charging/DischargeRate, root\cimv2 Win32_Battery
// as an AC-state fallback) and turns them into a BatteryBudget. Read-only, no kernel driver;
// every WMI call degrades to 0/false/null on failure rather than throwing — same trust level as
// WmiTelemetryService / DisplayService in this folder.
using System.Management;
using Microsoft.Extensions.Logging;

namespace GpdForge.Telemetry;

/// <summary>One projected runtime at a given sustained TDP.</summary>
public sealed record Projection(int Watts, int Minutes);

/// <summary>Battery budget: minutes remaining at the CURRENT discharge rate, plus projections at
/// a handful of TDP levels — e.g. "47 min a este TDP · 78 min a 12 W".</summary>
public sealed record BatteryBudget(
    int? MinutesRemaining,
    double RemainingWh,
    double DischargeW,
    IReadOnlyList<Projection> Projections);

/// <summary>Pure battery-budget math. No I/O, no WMI — safe to unit test directly.</summary>
public static class BatteryEstimator
{
    /// <summary>TDP levels projected by default when the caller doesn't supply its own set.</summary>
    public static readonly int[] DefaultTdpWatts = [8, 12, 15, 20, 25];

    /// <summary>Minutes remaining at a constant discharge rate. Null when there's nothing to
    /// divide by: on AC power the live discharge rate is 0 (or negative while charging), and a
    /// projection at 0 W / negative W is meaningless rather than "0 minutes".</summary>
    public static int? Estimate(double remainingWh, double dischargeW)
    {
        if (dischargeW <= 0) return null;
        double minutes = remainingWh / dischargeW * 60.0;
        return Math.Max(0, (int)Math.Round(minutes, MidpointRounding.AwayFromZero));
    }

    /// <summary>Projected minutes at each given TDP, approximating draw ~ TDP
    /// (minutes = remainingWh / watts * 60). Rough on purpose — real draw includes
    /// display/RAM/SSD/board overhead beyond the CPU package — but good enough for
    /// "about N min at W watts" in the UI.</summary>
    public static IReadOnlyList<Projection> Project(double remainingWh, int[] tdpWatts)
    {
        ArgumentNullException.ThrowIfNull(tdpWatts);

        var projections = new List<Projection>(tdpWatts.Length);
        foreach (int watts in tdpWatts)
        {
            int minutes = watts > 0
                ? Math.Max(0, (int)Math.Round(remainingWh / watts * 60.0, MidpointRounding.AwayFromZero))
                : 0;
            projections.Add(new Projection(watts, minutes));
        }
        return projections;
    }

    /// <summary>Convenience overload projecting at <see cref="DefaultTdpWatts"/>.</summary>
    public static IReadOnlyList<Projection> Project(double remainingWh) => Project(remainingWh, DefaultTdpWatts);
}

/// <summary>Reads real battery capacity/discharge from WMI and builds a <see cref="BatteryBudget"/>.
/// Read-only, no kernel driver — same trust level as <c>WmiTelemetryService</c>/<c>DisplayService</c>.</summary>
public sealed class BatteryService(ILogger<BatteryService>? logger = null)
{
    /// <summary>Builds today's budget from a fresh WMI read.</summary>
    public BatteryBudget GetBudget(int[]? tdpWatts = null)
    {
        var (remainingWh, charging) = ReadCapacityAndCharging();
        bool acConnected = charging ?? ReadAcConnectedFallback() ?? false;
        double wh = remainingWh ?? 0;
        double dischargeW = acConnected ? 0 : (ReadDischargeW() ?? 0);

        int? minutes = BatteryEstimator.Estimate(wh, dischargeW);
        var projections = BatteryEstimator.Project(wh, tdpWatts ?? BatteryEstimator.DefaultTdpWatts);

        return new BatteryBudget(minutes, wh, dischargeW, projections);
    }

    /// <summary>root\WMI BatteryStatus: RemainingCapacity (mWh, converted to Wh) + Charging.</summary>
    private (double? remainingWh, bool? charging) ReadCapacityAndCharging()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT RemainingCapacity, Charging FROM BatteryStatus");
            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    double wh = Math.Round(Convert.ToDouble(mo["RemainingCapacity"]) / 1000.0, 2);
                    bool charging = Convert.ToBoolean(mo["Charging"]);
                    return (wh, charging);
                }
            }
        }
        catch (Exception ex) { logger?.LogDebug(ex, "battery capacity/charging unavailable (root\\WMI BatteryStatus)"); }
        return (null, null);
    }

    /// <summary>root\WMI BatteryStatus.DischargeRate (mW), converted to watts.</summary>
    private double? ReadDischargeW()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT DischargeRate FROM BatteryStatus");
            foreach (var mo in searcher.Get())
            {
                using (mo)
                    return Math.Round(Convert.ToDouble(mo["DischargeRate"]) / 1000.0, 1);
            }
        }
        catch (Exception ex) { logger?.LogDebug(ex, "discharge rate unavailable (root\\WMI BatteryStatus)"); }
        return null;
    }

    /// <summary>Fallback AC check used only when root\WMI BatteryStatus.Charging didn't resolve:
    /// root\cimv2 Win32_Battery.BatteryStatus (2 = on AC line).</summary>
    private bool? ReadAcConnectedFallback()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery");
            foreach (var mo in searcher.Get())
            {
                using (mo)
                    return Convert.ToInt32(mo["BatteryStatus"]) == 2;
            }
        }
        catch (Exception ex) { logger?.LogDebug(ex, "AC status unavailable (root\\cimv2 Win32_Battery)"); }
        return null;
    }
}
