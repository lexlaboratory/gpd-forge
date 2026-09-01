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

        // --- no temperature reading -------------------------------------------------------------
        //
        // Handled FIRST and explicitly, because the alternative is silent and dangerous. Since
        // CpuTempC became nullable, every comparison below would still compile: in C# `null >= 90`
        // is false. So an unreadable sensor would flow through as "not hot", the guardian would
        // never throttle, and — worse — `currentThrottleW is not null && null <= 86` is also false,
        // so a throttle already applied would NEVER be released. The machine would sit at 12 W
        // forever with nothing on screen explaining why.
        //
        // The choice when the sensor is lost is between holding an existing throttle (slow, safe) and
        // releasing it (fast, unprotected). It holds: the last evidence said the part was hot, and
        // nothing since has contradicted it. But it holds LOUDLY — a guardian that cannot see and
        // does not say so is the failure this codebase keeps removing.
        if (t.CpuTempC is not double temp)
        {
            return new GuardianDecision(
                currentThrottleW,
                ClearThrottle: false,
                currentThrottleW is int held
                    ? $"CPU temperature is unreadable — holding {held} W rather than releasing a throttle we cannot verify"
                    : "CPU temperature is unreadable — the thermal guardian cannot protect this device",
                "warn");
        }

        // --- thermal takes priority over battery ---
        if (temp >= c.TempCriticalC)
            return new GuardianDecision(c.ThrottleFloorW, false,
                $"CPU {temp:0}°C — critical, holding {c.ThrottleFloorW} W", "critical");

        if (temp >= c.TempThrottleC)
        {
            int w = RampWatts(temp, c);
            return new GuardianDecision(w, false, $"CPU {temp:0}°C — easing to {w} W", "warn");
        }

        // cooled down enough → release any throttle we were holding
        if (currentThrottleW is not null && temp <= c.TempThrottleC - c.ClearHysteresisC)
            return new GuardianDecision(null, true, "Temps recovered — throttle cleared", "info");

        // --- battery (only meaningful on battery, and only with a reading) -----------------------
        //
        // `pct` is unwrapped rather than compared through the lifted operator for the same reason as
        // the temperature: `null <= 8` is false, so a failed battery query would silently mean "not
        // low". That direction is at least safe — it under-reports rather than crying wolf — but it
        // is still a guard passing on a value it never had.
        if (!t.AcConnected && t.BatteryPct is int pct)
        {
            if (pct <= c.BatteryCriticalPct)
                return new GuardianDecision(currentThrottleW, false, $"Battery {pct}% — critical", "critical");
            if (pct <= c.BatteryLowPct)
                return new GuardianDecision(currentThrottleW, false, $"Battery {pct}% — low", "warn");
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
