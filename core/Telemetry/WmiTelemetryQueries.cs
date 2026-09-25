// GPD Forge — the four WMI queries telemetry makes, each built once. GPL-3.0-or-later.
//
// These used to be four `new ManagementObjectSearcher(...)` per ReadAsync, i.e. four per caller per
// tick. A searcher is reusable — Get() re-runs its query — so they are now built once and kept.
// Read-only: nothing here writes to WMI.
using System.Management;
using Microsoft.Extensions.Logging;

namespace GpdForge.Telemetry;

/// <summary>Charge percentage and whether the machine is on mains, as Win32_Battery reports them.</summary>
public readonly record struct WmiBatteryReading(int Pct, bool Ac);

/// <summary>Raw WMI answers; null means the query failed or returned nothing. Seam for tests.</summary>
public interface IWmiTelemetryQueries
{
    double? ReadThermalZoneTenthsKelvin();
    int? ReadProcessorClockMhz();
    WmiBatteryReading? ReadBattery();
    double? ReadDischargeRateMw();
}

public sealed class WmiTelemetryQueries(ILogger? logger = null) : IWmiTelemetryQueries, IDisposable
{
    private readonly ManagementObjectSearcher _thermal =
        new(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
    private readonly ManagementObjectSearcher _clock =
        new("SELECT CurrentClockSpeed FROM Win32_Processor");
    private readonly ManagementObjectSearcher _battery =
        new("SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
    private readonly ManagementObjectSearcher _discharge =
        new(@"root\WMI", "SELECT DischargeRate FROM BatteryStatus");

    // A searcher is not documented as thread-safe. The sampler is the only steady caller, but the
    // --probe paths construct their own service, and one lock costs nothing at 1 Hz.
    private readonly Lock _gate = new();

    public double? ReadThermalZoneTenthsKelvin() =>
        First(_thermal, mo => Convert.ToDouble(mo["CurrentTemperature"]), "thermal zone unavailable (needs elevation/ACPI support)");

    public int? ReadProcessorClockMhz() =>
        First(_clock, mo => Convert.ToInt32(mo["CurrentClockSpeed"]), "cpu clock unavailable");

    // Win32_Battery.BatteryStatus: 1 = discharging (on battery), 2 = AC line.
    public WmiBatteryReading? ReadBattery() =>
        First(_battery, mo => new WmiBatteryReading(
            Convert.ToInt32(mo["EstimatedChargeRemaining"]), Convert.ToInt32(mo["BatteryStatus"]) == 2),
            "battery unavailable");

    public double? ReadDischargeRateMw() =>
        First(_discharge, mo => Convert.ToDouble(mo["DischargeRate"]), "discharge rate unavailable");

    // `where T : struct` is load-bearing: on an unconstrained T, `T?` is plain T for value types and
    // `default` would be 0 — the confident zero this whole telemetry path was rebuilt to stop emitting.
    private T? First<T>(ManagementObjectSearcher searcher, Func<ManagementBaseObject, T> map, string failure)
        where T : struct
    {
        try
        {
            lock (_gate)
            {
                using var results = searcher.Get();
                foreach (var mo in results)
                {
                    using (mo) return map(mo);
                }
            }
        }
        catch (Exception ex) { logger?.LogDebug(ex, "{Failure}", failure); }
        return null;
    }

    public void Dispose()
    {
        _thermal.Dispose();
        _clock.Dispose();
        _battery.Dispose();
        _discharge.Dispose();
    }
}
