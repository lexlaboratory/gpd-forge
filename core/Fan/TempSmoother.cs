// GPD Forge — time-weighted smoothing for the fan curve's temperature input. GPL-3.0-or-later.
//
// Tctl on this APU is an instantaneous control temperature: under a bursty game load it swings
// ten-plus degrees from one second to the next, and that is sampling noise, not a thermal trend.
// FanCurve.DutyForTemp never delays a RISE (a safety choice, see FanCurve.cs), so a raw reading
// turns every spike into an audible duty jump — the "revs up and down constantly" complaint.
//
// The first version of this was a 4-sample moving average. That was too weak for Tctl (one 90°C
// tick among 70s still moved the fan), and it counted SAMPLES, not seconds: when a slow ryzenadj
// apply stretched the worker loop, four samples could span anywhere from 4 s to 30 s. This one is
// an exponential moving average weighted by the real elapsed time, and it is asymmetric: it follows
// heating faster than cooling, so the fan answers a real load promptly but does not chase every dip.
//
// The fan's instance smooths only the READING handed to the curve. The thermal guardian runs its
// own, much shorter instance (GuardianService) and bypasses it entirely at the critical limit.

namespace GpdForge.Fan;

/// <summary>Asymmetric exponential moving average over elapsed time.</summary>
public sealed class TempSmoother(
    double riseTauSeconds = TempSmoother.DefaultRiseTauSeconds,
    double fallTauSeconds = TempSmoother.DefaultFallTauSeconds)
{
    /// <summary>Time constant while the reading is above the average: a sustained rise is ~95 %
    /// tracked after 3τ (9 s).</summary>
    public const double DefaultRiseTauSeconds = 3.0;

    /// <summary>Time constant while the reading is below the average. Slower on purpose: cooling
    /// is never urgent, and following every dip is what made the fan hunt.</summary>
    public const double DefaultFallTauSeconds = 10.0;

    private readonly double _riseTau = Math.Max(0.001, riseTauSeconds);
    private readonly double _fallTau = Math.Max(0.001, fallTauSeconds);
    private double? _average;

    /// <summary>Folds in a reading taken <paramref name="dtSeconds"/> after the previous one and
    /// returns the new average. The first reading after construction or <see cref="Reset"/> is
    /// adopted as-is. A non-positive <paramref name="dtSeconds"/> leaves the average unchanged.</summary>
    public double Add(double tempC, double dtSeconds)
    {
        if (_average is not double avg)
        {
            _average = tempC;
            return tempC;
        }
        if (dtSeconds <= 0) return avg;

        double tau = tempC > avg ? _riseTau : _fallTau;
        double alpha = 1 - Math.Exp(-dtSeconds / tau);
        avg += alpha * (tempC - avg);
        _average = avg;
        return avg;
    }

    /// <summary>Drops all history. Call whenever the reading goes untrustworthy or fan control
    /// hands back to firmware, so a stale average never leaks into the next curve-mode session.</summary>
    public void Reset() => _average = null;
}
