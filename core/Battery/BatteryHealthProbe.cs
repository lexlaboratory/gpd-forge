// GPD Forge — where the health numbers actually come from. GPL-3.0-or-later.
//
// The two halves of the health figure come from different places, for a reason worth knowing:
//
//   FULL-CHARGE capacity is live and cheap. MSBatteryClass exposes it over WMI and it changes as the
//   pack ages, so it is read on demand.
//
//   DESIGN capacity is NOT exposed over WMI on this machine. Measured 2026-09-01: both
//   MSBatteryClass.DesignedCapacity and Win32_Battery.DesignCapacity come back empty. The only
//   source that has it is `powercfg /batteryreport`, which spawns a process and writes a 76 KB
//   report — far too expensive for a tick, and pointless to repeat: design capacity is a factory
//   constant that cannot change for the life of the pack.
//
// So it is read once and cached. That asymmetry is the whole design of this file.
using System.Management;
using System.Xml.Linq;
using GpdForge.SystemControl;
using GpdForge.Tdp;
using Microsoft.Extensions.Logging;

namespace GpdForge.Battery;

/// <summary>The pack's factory capacity in mWh. Separate from the live probe because it is obtained
/// differently, costs far more, and never changes.</summary>
public interface IDesignCapacitySource
{
    int? Read();
}

/// <summary>
/// Reads design capacity out of `powercfg /batteryreport /xml`, once, then remembers it — in memory
/// for the process and on disk across restarts.
/// </summary>
public sealed class PowercfgDesignCapacitySource(
    IProcessRunner runner,
    string? cacheDirectory = null,
    ILogger<PowercfgDesignCapacitySource>? logger = null) : IDesignCapacitySource
{
    private readonly Lock _gate = new();
    private readonly string _cachePath =
        Path.Combine(cacheDirectory ?? DataRoot.Current, "battery-design-capacity.txt");

    private int? _cached;
    private bool _attempted;

    public int? Read()
    {
        lock (_gate)
        {
            if (_cached is not null) return _cached;

            if (TryReadCacheFile() is { } fromDisk)
            {
                _cached = fromDisk;
                return _cached;
            }

            // Attempted at most once per process. A machine where powercfg is unavailable or refuses
            // would otherwise spawn a process on every request for a value it is never going to get.
            if (_attempted) return null;
            _attempted = true;

            _cached = ReadFromPowercfg();
            if (_cached is not null) TryWriteCacheFile(_cached.Value);
            return _cached;
        }
    }

    private int? ReadFromPowercfg()
    {
        // Its own temp file: powercfg overwrites the target, and pointing two callers at one path is
        // a race that produces a half-written report rather than an error.
        var report = Path.Combine(Path.GetTempPath(), $"gpdforge-batteryreport-{Guid.NewGuid():N}.xml");
        try
        {
            // Synchronous by design: this runs at most once, and the callers (an HTTP handler and a
            // daily sampler) both already tolerate a one-off pause far better than they tolerate the
            // complexity of an async cache primed from two places.
            runner.RunAsync("powercfg", $"/batteryreport /xml /output \"{report}\"", CancellationToken.None)
                  .GetAwaiter().GetResult();

            if (!File.Exists(report))
            {
                logger?.LogDebug("powercfg produced no battery report at {Path}.", report);
                return null;
            }

            return ParseDesignCapacity(File.ReadAllText(report));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or System.Xml.XmlException)
        {
            // A missing design capacity makes health null, which the API already models. It is not a
            // reason to fail a request for the numbers that ARE available.
            logger?.LogDebug(ex, "Could not read design capacity from powercfg.");
            return null;
        }
        finally
        {
            try { if (File.Exists(report)) File.Delete(report); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Pulls DesignCapacity out of the report. Elements are matched by LOCAL name, ignoring the
    /// `http://schemas.microsoft.com/battery/2012` namespace — a schema version bump would otherwise
    /// silently return null for every machine, and the namespace tells us nothing we act on.
    /// </summary>
    public static int? ParseDesignCapacity(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        var doc = XDocument.Parse(xml);

        // A docked or secondary pack appears as a second <Battery>. Take the first that reports a
        // usable capacity rather than assuming ordering.
        foreach (var battery in doc.Descendants().Where(e => e.Name.LocalName == "Battery"))
        {
            var value = battery.Elements().FirstOrDefault(e => e.Name.LocalName == "DesignCapacity")?.Value;
            if (int.TryParse(value, out var mwh) && mwh > 0) return mwh;
        }

        return null;
    }

    private int? TryReadCacheFile()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            return int.TryParse(File.ReadAllText(_cachePath).Trim(), out var mwh) && mwh > 0 ? mwh : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void TryWriteCacheFile(int mwh)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, mwh.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation. Losing it costs one powercfg run per process start.
            logger?.LogDebug(ex, "Could not cache design capacity to {Path}.", _cachePath);
        }
    }
}

/// <summary>Live health from WMI, with design capacity handed in.</summary>
public sealed class WmiBatteryHealthProbe(
    IDesignCapacitySource designCapacity,
    ILogger<WmiBatteryHealthProbe>? logger = null) : IBatteryHealthProbe
{
    public BatteryHealthReading Read()
    {
        try
        {
            var full = QueryFirstInt(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity");
            var designed = designCapacity.Read();

            // Both of these are expected to be null on this board, and are queried anyway: the point
            // of the endpoint is to report what the hardware does and does not expose, and a value
            // that appears on a future firmware should show up without a code change.
            var cycles = BatteryHealthMath.NormaliseCycleCount(
                QueryFirstInt(@"root\WMI", "SELECT CycleCount FROM BatteryCycleCount", "CycleCount"));

            var tempC = ReadCellTemperatureC();

            return new BatteryHealthReading(
                DesignedMilliwattHours: designed,
                FullChargeMilliwattHours: full,
                HealthPercent: BatteryHealthMath.HealthPercent(designed, full),
                CycleCount: cycles,
                CellTemperatureC: tempC,
                Chemistry: QueryFirstString(@"root\CIMV2", "SELECT Chemistry FROM Win32_Battery", "Chemistry"),
                Unavailable: null);
        }
        catch (ManagementException ex)
        {
            logger?.LogDebug(ex, "Battery health query failed.");
            return new BatteryHealthReading(null, null, null, null, null, null,
                $"Windows refused the battery WMI query ({ex.Message.Trim()}).");
        }
    }

    /// <summary>
    /// Cell temperature in °C, or null. The BatteryTemperature class reports DECIKELVIN, and it has
    /// no instances on this device — so this returns null here and is written correctly anyway,
    /// because a unit error that only appears on someone else's hardware is the worst kind.
    /// </summary>
    private double? ReadCellTemperatureC()
    {
        var deciKelvin = QueryFirstInt(@"root\WMI", "SELECT Temperature FROM BatteryTemperature", "Temperature");
        if (deciKelvin is not > 0) return null;
        return Math.Round(deciKelvin.Value / 10.0 - 273.15, 1);
    }

    private int? QueryFirstInt(string scope, string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var raw = mo[property];
                    if (raw is null) continue;
                    return Convert.ToInt32(raw);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or InvalidCastException or FormatException or OverflowException)
        {
            // A class with no instances is the normal case for two of these queries, not an error.
            logger?.LogDebug(ex, "WMI query returned nothing usable: {Query}", query);
        }
        return null;
    }

    private string? QueryFirstString(string scope, string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var raw = mo[property];
                    if (raw is null) continue;
                    // Win32_Battery.Chemistry is an enum code; the readable name lives in the
                    // powercfg report, so map the codes that matter and pass anything else through.
                    if (raw is ushort or int) return ChemistryName(Convert.ToInt32(raw));
                    return raw.ToString();
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or InvalidCastException or FormatException)
        {
            logger?.LogDebug(ex, "WMI query returned nothing usable: {Query}", query);
        }
        return null;
    }

    /// <summary>Win32_Battery.Chemistry codes (CIM schema).</summary>
    public static string ChemistryName(int code) => code switch
    {
        1 => "Other",
        2 => "Unknown",
        3 => "Lead Acid",
        4 => "Nickel Cadmium",
        5 => "Nickel Metal Hydride",
        6 => "Lithium-ion",
        7 => "Zinc air",
        8 => "Lithium Polymer",
        _ => $"Unrecognised ({code})",
    };
}
