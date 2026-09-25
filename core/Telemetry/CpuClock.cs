// GPD Forge — where cpuClockMhz comes from. Pure: no LHM, no WMI. GPL-3.0-or-later.
//
// Until 2026-09-24 the reported clock was Win32_Processor.CurrentClockSpeed, which on this Ryzen AI 9
// HX 370 is the fixed 2000 MHz base clock: it read 2000 at idle and 2000 at 5 GHz boost. LHM's clock
// sensors are the real thing, and of those the EFFECTIVE clock (time-weighted, so a parked core counts
// as the near-zero it is) is the one that answers "how hard is the CPU working right now".
namespace GpdForge.Telemetry;

/// <summary>One LHM clock sensor on the CPU, by name. Null value = the sensor exists but has no reading.</summary>
public readonly record struct ClockSensorReading(string Name, double? Mhz);

public static class CpuClock
{
    // Anything outside this is a garbage register rather than a clock; no x86 core runs at 10 GHz.
    private const double MaxPlausibleMhz = 10_000;

    /// <summary>
    /// The average effective core clock, choosing in this order — the first that has a usable value:
    /// LHM's own "Cores (Average Effective)"; the mean of the per-core "(Effective)" sensors;
    /// "Cores (Average)"; the mean of the plain per-core clocks. Never the bus speed. Null when none.
    /// </summary>
    public static int? EffectiveCoreMhz(IEnumerable<ClockSensorReading> sensors)
    {
        var usable = sensors.Where(s => IsPlausible(s.Mhz)).ToList();

        double? chosen =
            Single(usable, "Cores (Average Effective)")
            ?? Mean(usable.Where(s => IsPerCore(s.Name) && IsEffective(s.Name)))
            ?? Single(usable, "Cores (Average)")
            ?? Mean(usable.Where(s => IsPerCore(s.Name) && !IsEffective(s.Name)));

        return chosen is double mhz ? (int)Math.Round(mhz, MidpointRounding.AwayFromZero) : null;
    }

    private static bool IsPlausible(double? mhz) =>
        mhz is double v && double.IsFinite(v) && v > 0 && v < MaxPlausibleMhz;

    // "Core #1", "P-Core #3", "E-Core #2" — with or without the " (Effective)" suffix.
    private static bool IsPerCore(string name) => name.Contains("Core #", StringComparison.OrdinalIgnoreCase);

    private static bool IsEffective(string name) => name.Contains("(Effective)", StringComparison.OrdinalIgnoreCase);

    private static double? Single(List<ClockSensorReading> sensors, string name) =>
        sensors.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).Mhz;

    private static double? Mean(IEnumerable<ClockSensorReading> sensors)
    {
        var values = sensors.Select(s => s.Mhz!.Value).ToList();
        return values.Count > 0 ? values.Average() : null;
    }
}

/// <summary>
/// The Win32_Processor fallback, trusted only once it has been seen to move.
///
/// CurrentClockSpeed is a live clock on some machines and a constant on others — this one included —
/// and a single reading cannot tell which. A value that has never changed is reported as null; the
/// first change proves the source is live, and from then on every reading is reported.
/// </summary>
public sealed class Win32ClockFilter
{
    private int? _first;
    private bool _live;

    public int? Observe(int? mhz)
    {
        if (mhz is not int v || v <= 0) return null;
        if (_first is null) _first = v;
        else if (v != _first) _live = true;
        return _live ? v : null;
    }
}
