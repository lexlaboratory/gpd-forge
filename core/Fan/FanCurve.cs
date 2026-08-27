// GPD Forge — temp→duty fan curves (pure logic). GPL-3.0-or-later.
//
// Piecewise-linear curve evaluation + hysteresis, all in the same 0..255 "user" duty scale as
// FanMath (GpdFanController casts to the EC's native scale at write time — this file never touches
// pwmMax). Three named curves are provided for the Quiet/Balanced/Aggressive fan-mode presets;
// GpdFanController.MinManualDuty is enforced separately, at write time, as the hard safety floor —
// these curves are free to describe near-zero duty at low temps.

namespace GpdForge.Fan;

/// <summary>One breakpoint of a fan curve: at <see cref="TempC"/>, duty is <see cref="Duty"/> (0..255).</summary>
public readonly record struct CurvePoint(double TempC, int Duty);

public static class FanCurve
{
    /// <summary>Default hysteresis band (°C) ForgeWorker feeds into <see cref="DutyForTemp"/>.</summary>
    public const double DefaultHysteresisC = 5.0;

    // Conservative: stays low while cool, but never truly silent above ~50°C, and is already ramped
    // hard by the time the CPU is hot (~85°C) — "quiet" trades noise for a few extra degrees when
    // it's cool, never for safety margin when it's hot.
    public static readonly IReadOnlyList<CurvePoint> Quiet =
    [
        new(0, 0),
        new(45, 0),
        new(50, 55),
        new(60, 90),
        new(70, 130),
        new(80, 190),
        new(85, 235),
        new(90, 255),
    ];

    // The default: a smoother, earlier ramp than Quiet, full duty a little sooner.
    public static readonly IReadOnlyList<CurvePoint> Balanced =
    [
        new(0, 0),
        new(40, 40),
        new(55, 95),
        new(70, 150),
        new(80, 210),
        new(88, 255),
    ];

    // Prioritizes cooling over noise: audible even near idle, full duty well before critical.
    public static readonly IReadOnlyList<CurvePoint> Aggressive =
    [
        new(0, 60),
        new(35, 70),
        new(50, 120),
        new(65, 180),
        new(75, 230),
        new(82, 255),
    ];

    /// <summary>Looks up the named curve for a <c>FanState.Mode</c> value. Null for modes that don't
    /// use a curve (Auto, Manual).</summary>
    public static IReadOnlyList<CurvePoint>? ForMode(string mode) => mode switch
    {
        "Quiet" => Quiet,
        "Balanced" => Balanced,
        "Aggressive" => Aggressive,
        _ => null,
    };

    /// <summary>
    /// Piecewise-linear interpolation over <paramref name="points"/> (must be sorted ascending by
    /// <see cref="CurvePoint.TempC"/> — true of the three curves above). Temps at or below the first
    /// point, or at or above the last, clamp to that endpoint's duty. Result is always clamped 0..255.
    /// </summary>
    public static int Interpolate(double tempC, IReadOnlyList<CurvePoint> points)
    {
        if (points.Count == 0) return 0;
        if (tempC <= points[0].TempC) return Math.Clamp(points[0].Duty, 0, 255);

        var last = points[^1];
        if (tempC >= last.TempC) return Math.Clamp(last.Duty, 0, 255);

        for (int i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (tempC > b.TempC) continue;

            double span = b.TempC - a.TempC;
            double frac = span <= 0 ? 0 : (tempC - a.TempC) / span;
            double duty = a.Duty + frac * (b.Duty - a.Duty);
            return Math.Clamp((int)Math.Round(duty, MidpointRounding.AwayFromZero), 0, 255);
        }
        return Math.Clamp(last.Duty, 0, 255); // unreachable given the bounds checks above
    }

    /// <summary>
    /// Curve duty for the current temperature, with hysteresis so the fan doesn't hunt: duty rises
    /// immediately (never delayed — this is a thermal safety path), but a drop is only allowed once
    /// the temperature has fallen <paramref name="hysteresisC"/> below the point that produced
    /// <paramref name="lastDuty"/>. Concretely: re-evaluate the curve at
    /// <c>cpuTempC + hysteresisC</c> — if that warmer-than-reality reading would still call for less
    /// than <paramref name="lastDuty"/>, it's safe to drop now; otherwise hold <paramref
    /// name="lastDuty"/> steady. Pass <c>lastDuty: 0</c> on a cold start so the first reading is
    /// simply adopted with no hysteresis holdback.
    /// </summary>
    public static int DutyForTemp(double cpuTempC, IReadOnlyList<CurvePoint> points, double hysteresisC, int lastDuty)
    {
        int rise = Interpolate(cpuTempC, points);
        if (rise >= lastDuty) return rise;

        int fallCheck = Interpolate(cpuTempC + Math.Max(0, hysteresisC), points);
        return fallCheck < lastDuty ? rise : Math.Clamp(lastDuty, 0, 255);
    }
}
