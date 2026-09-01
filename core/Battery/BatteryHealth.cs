// GPD Forge — battery health: what the pack can still hold, and what cannot be measured.
// GPL-3.0-or-later.
//
// The point of this file is a number GPD Forge has never shown: the pack's remaining capacity as a
// fraction of what it left the factory with. On the reference device, 40,009 mWh of an original
// 43,890 — 91.2 %. Without it, every battery feature in this app (the runtime estimate, the what-if
// projections, any future charge guard) is reasoning about a pack whose actual size nobody checked.
//
// THE RULE THIS FILE EXISTS TO OBEY. Two of the four things anyone would want here are NOT available
// on this board, and they are returned as null with a reason rather than as a number:
//
//   * Cycle count. Both powercfg and BatteryCycleCount report 0. Zero is not "this battery has never
//     been charged" — it is the EC declining to answer. Reporting 0 would put a brand-new pack on
//     screen next to a health figure of 91 %, which is a contradiction the user has to resolve.
//   * Cell temperature. The BatteryTemperature WMI class has no instances here.
//
// This is the same discipline already applied to standby drain, where an unmeasurable value became
// null instead of a plausible extrapolation — and the opposite of what GET /telemetry still does,
// reporting 0 °C for a sensor it cannot read.
namespace GpdForge.Battery;

/// <summary>
/// One health reading. Every field is nullable because on this board most of them genuinely are —
/// see the header. <paramref name="Unavailable"/> explains the absence when the whole reading failed.
/// </summary>
/// <param name="DesignedMilliwattHours">Factory capacity. Constant for the life of the pack.</param>
/// <param name="FullChargeMilliwattHours">What it can hold now, fully charged.</param>
/// <param name="HealthPercent">Full charge as a percentage of designed. Null if either input is.</param>
/// <param name="CycleCount">Null when the EC does not report it — which is always, on this board.</param>
/// <param name="CellTemperatureC">Null when no BatteryTemperature instance exists.</param>
public readonly record struct BatteryHealthReading(
    int? DesignedMilliwattHours,
    int? FullChargeMilliwattHours,
    double? HealthPercent,
    int? CycleCount,
    double? CellTemperatureC,
    string? Chemistry,
    string? Unavailable);

public static class BatteryHealthMath
{
    /// <summary>
    /// Health as a percentage of designed capacity, or null when it cannot be computed.
    ///
    /// Null rather than 0 or 100 for every degenerate input. A designed capacity of zero is a failed
    /// read, and dividing by it to produce "0 % health" would tell someone their battery is dead when
    /// what actually happened is that a WMI query returned nothing.
    /// </summary>
    public static double? HealthPercent(int? designedMwh, int? fullChargeMwh)
    {
        if (designedMwh is not > 0) return null;
        if (fullChargeMwh is not > 0) return null;

        // Not clamped to 100. A new pack often reports slightly above its design capacity, and
        // clamping would hide a reading of 104 % that is genuinely useful — it says the pack is
        // healthy AND that these two numbers come from different places. Rounded to one decimal
        // because the underlying values are quantised in mWh and more digits imply precision the
        // measurement does not have.
        return Math.Round(100.0 * fullChargeMwh.Value / designedMwh.Value, 1);
    }

    /// <summary>
    /// Turns a raw cycle count into an honest one: 0 means "not reported", not "never charged".
    ///
    /// Measured on the reference device 2026-09-01 — both `powercfg /batteryreport /xml` and the
    /// BatteryCycleCount WMI class return 0 on a pack that has lost 8.8 % of its capacity. A pack
    /// cannot lose that while having been charged zero times, so 0 is the EC's way of saying it does
    /// not keep the number.
    /// </summary>
    public static int? NormaliseCycleCount(int? raw) => raw is > 0 ? raw : null;

    /// <summary>
    /// Degradation between the oldest and newest samples, in percentage points, or null when there
    /// is not enough to say.
    ///
    /// Requires at least two samples on DIFFERENT days. One reading is a value, not a trend, and a
    /// "trend" drawn from two readings twenty minutes apart would report noise as decline — this pack
    /// loses single-digit percent over years, so anything sub-daily is measurement jitter.
    /// </summary>
    public static double? DegradationPoints(IReadOnlyList<BatteryHealthSample> samples)
    {
        if (samples is null || samples.Count < 2) return null;

        var ordered = samples.OrderBy(s => s.AtUtc).ToList();
        var first = ordered[0];
        var last = ordered[^1];

        if (last.AtUtc.Date <= first.AtUtc.Date) return null;
        if (first.HealthPercent is null || last.HealthPercent is null) return null;

        return Math.Round(first.HealthPercent.Value - last.HealthPercent.Value, 2);
    }
}

/// <summary>One persisted health sample. Deliberately small: this file is appended to for years.</summary>
public readonly record struct BatteryHealthSample(
    DateTimeOffset AtUtc,
    int? FullChargeMilliwattHours,
    double? HealthPercent);

/// <summary>Reads what the platform will tell us about the pack. Abstracted so the calculation and
/// the endpoint are testable without WMI or a real battery.</summary>
public interface IBatteryHealthProbe
{
    BatteryHealthReading Read();
}
