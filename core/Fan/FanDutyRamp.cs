// GPD Forge — slew limit and dwell between the fan curve and the EC. GPL-3.0-or-later.
//
// FanCurve.DutyForTemp answers "what duty does this temperature call for", and it answers in one
// step: once cooling passes the hysteresis band it drops straight to the curve value (about 12 %
// at once on Balanced between 70 and 80°C), and a rise lands instantly whatever its size. Measured
// on 2026-09-24 that shape is audible as "fast up, stepped down, repeat".
//
// This sits after the curve and decides how fast the fan may GET there:
//   - rises are rate-limited, but quickly (full range in ~10 s);
//   - drops are rate-limited slowly, and only start once the duty has not risen for a hold period,
//     so a target that wobbles around a level produces a steady fan instead of a hunting one.
// The thermal guardian throttles power on its own input regardless, so a limited rise never
// leaves the APU unprotected; the firmware's own Tctl limit sits behind that.

namespace GpdForge.Fan;

/// <summary>Rate limiter with a post-rise hold, in the 0..255 user duty scale.</summary>
public sealed class FanDutyRamp(
    int upPerSecond = FanDutyRamp.DefaultUpPerSecond,
    int downPerSecond = FanDutyRamp.DefaultDownPerSecond,
    double holdSeconds = FanDutyRamp.DefaultHoldSeconds)
{
    /// <summary>~10 %/s: 0→255 in about ten seconds.</summary>
    public const int DefaultUpPerSecond = 25;

    /// <summary>~2.4 %/s: a full-range drop takes the better part of a minute.</summary>
    public const int DefaultDownPerSecond = 6;

    /// <summary>How long after the last increase the duty is held before it may start to fall.</summary>
    public const double DefaultHoldSeconds = 8.0;

    private readonly int _up = Math.Max(1, upPerSecond);
    private readonly int _down = Math.Max(1, downPerSecond);
    private readonly double _hold = Math.Max(0, holdSeconds);
    private int? _duty;
    private double _holdRemaining;

    /// <summary>Moves toward <paramref name="target"/> by at most the allowed rate for
    /// <paramref name="dtSeconds"/> of elapsed time and returns the duty to write. The first step
    /// after construction or <see cref="Reset"/> adopts the target directly.</summary>
    public int Step(int target, double dtSeconds)
    {
        target = Math.Clamp(target, 0, 255);
        if (_duty is not int duty)
        {
            _duty = target;
            _holdRemaining = 0;
            return target;
        }
        if (dtSeconds <= 0) return duty;

        _holdRemaining = Math.Max(0, _holdRemaining - dtSeconds);

        if (target > duty)
        {
            duty = Math.Min(target, duty + MaxDelta(_up, dtSeconds));
            _holdRemaining = _hold;
        }
        else if (target < duty && _holdRemaining <= 0)
        {
            duty = Math.Max(target, duty - MaxDelta(_down, dtSeconds));
        }

        _duty = duty;
        return duty;
    }

    /// <summary>Forgets the current duty, so the next step is a cold start. Call whenever fan
    /// control leaves curve mode.</summary>
    public void Reset()
    {
        _duty = null;
        _holdRemaining = 0;
    }

    private static int MaxDelta(int perSecond, double dtSeconds) =>
        Math.Max(1, (int)Math.Round(perSecond * dtSeconds, MidpointRounding.AwayFromZero));
}
