// GPD Forge — thermal/battery guardian (pure decision logic). GPL-3.0-or-later.
using GpdForge.Telemetry;

namespace GpdForge.Guardian;

/// <summary>Thresholds for the guardian. Watts are absolute STAPM ceilings; °C are CPU package.
/// A record CLASS (not struct) so `new GuardianConfig()` actually applies these parameter defaults.</summary>
public sealed record GuardianConfig(
    bool Enabled = true,
    bool AutoThrottle = true,
    double TempThrottleC = 90,   // begin easing power above this
    double TempCriticalC = 96,   // hard floor + critical alert at/above this
    int ThrottleFloorW = 12,     // never throttle below this
    int NominalCeilingW = 25,    // top of the throttle ramp
    double ClearHysteresisC = 4, // cool this far below TempThrottleC before clearing
    int BatteryLowPct = 15,
    int BatteryCriticalPct = 8);

/// <summary>
/// What the guardian wants this tick. <see cref="ThrottleToW"/> is an absolute STAPM ceiling to apply;
/// <see cref="ClearThrottle"/> asks to restore the mode's normal preset. <see cref="Alert"/> is a
/// human message (null = nothing new), with a severity of ok|info|warn|critical.
/// </summary>
public readonly record struct GuardianDecision(int? ThrottleToW, bool ClearThrottle, string? Alert, string Severity);

/// <summary>Pure, side-effect-free evaluation — trivially unit-testable.</summary>
public static class GuardianEvaluator
{
    public static GuardianDecision Evaluate(TelemetrySnapshot t, GuardianConfig c, int? currentThrottleW)
    {
        if (!c.Enabled)
            return new GuardianDecision(null, currentThrottleW is not null, null, "ok");

        // --- thermal takes priority over battery ---
        if (t.CpuTempC >= c.TempCriticalC)
            return new GuardianDecision(c.ThrottleFloorW, false,
                $"CPU {t.CpuTempC:0}°C — critical, holding {c.ThrottleFloorW} W", "critical");

        if (t.CpuTempC >= c.TempThrottleC)
        {
            int w = RampWatts(t.CpuTempC, c);
            return new GuardianDecision(w, false, $"CPU {t.CpuTempC:0}°C — easing to {w} W", "warn");
        }

        // cooled down enough → release any throttle we were holding
        if (currentThrottleW is not null && t.CpuTempC <= c.TempThrottleC - c.ClearHysteresisC)
            return new GuardianDecision(null, true, "Temps recovered — throttle cleared", "info");

        // --- battery (only meaningful on battery) ---
        if (!t.AcConnected)
        {
            if (t.BatteryPct <= c.BatteryCriticalPct)
                return new GuardianDecision(currentThrottleW, false, $"Battery {t.BatteryPct}% — critical", "critical");
            if (t.BatteryPct <= c.BatteryLowPct)
                return new GuardianDecision(currentThrottleW, false, $"Battery {t.BatteryPct}% — low", "warn");
        }

        return new GuardianDecision(currentThrottleW, false, null, "ok");
    }

    /// <summary>Linear ramp: at TempThrottleC → NominalCeilingW; at TempCriticalC → ThrottleFloorW.</summary>
    public static int RampWatts(double temp, GuardianConfig c)
    {
        double span = Math.Max(1.0, c.TempCriticalC - c.TempThrottleC);
        double frac = Math.Clamp((temp - c.TempThrottleC) / span, 0, 1);
        int w = (int)Math.Round(c.NominalCeilingW - frac * (c.NominalCeilingW - c.ThrottleFloorW));
        return Math.Clamp(w, c.ThrottleFloorW, c.NominalCeilingW);
    }
}
