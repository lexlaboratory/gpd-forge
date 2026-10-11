// GPD Forge — turn a guardian throttle wattage into a TDP profile. GPL-3.0-or-later.
//
// A throttle is a CEILING. It used to be applied as a flat `throttleW` on all three limits with
// Tctl = TempCriticalC, and throttleW starts at NominalCeilingW (25 W) — a number chosen for
// `gaming`. In any mode below that it RAISED power: measured on 2026-09-24 in `windows`
// (15/20/17 W, Tctl 92), a "throttle" applied 25/25/25 W at Tctl 96 to an already hot device.
// Here every limit is the lower of the throttle and what the active mode allows.

using GpdForge.Tdp;

namespace GpdForge.Guardian;

public static class GuardianThrottle
{
    /// <summary>The profile to apply for a <paramref name="throttleW"/> ceiling under the active
    /// <paramref name="mode"/>: never higher than the mode on any limit. With no mode profile it
    /// falls back to the flat throttle at <paramref name="tctlCriticalC"/>.</summary>
    public static TdpProfile Profile(int throttleW, TdpProfile? mode, int tctlCriticalC)
    {
        if (mode is not TdpProfile m)
            return new TdpProfile(throttleW, throttleW, throttleW, tctlCriticalC);

        return new TdpProfile(
            Math.Min(throttleW, m.StapmW),
            Math.Min(throttleW, m.FastW),
            Math.Min(throttleW, m.SlowW),
            Math.Min(tctlCriticalC, m.TctlC));
    }

    /// <summary>The alert for a <paramref name="throttleW"/> ceiling that lowers nothing, because every
    /// limit in force is already under it; null when it does lower something, and the evaluator's
    /// "requesting a thermal limit of N W" is then true.</summary>
    /// <remarks>On 2026-10-10 this device logged 1,746 alerts of "CPU 91°C — requesting a thermal limit
    /// of 24 W" while the user's 12 W held — read on screen as Forge lifting a 12 W handheld to 24 W.</remarks>
    public static string? NothingLowered(double? tempC, int throttleW, TdpProfile? inForce, int tctlCriticalC)
    {
        if (inForce is not TdpProfile m || Profile(throttleW, m, tctlCriticalC) != m) return null;
        return $"CPU {tempC:0}°C — the {m.StapmW} W limit in force is already at or under the {throttleW} W thermal ceiling; nothing to lower";
    }
}
