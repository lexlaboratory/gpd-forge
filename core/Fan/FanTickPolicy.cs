// GPD Forge — what the fan should be told on one tick. GPL-3.0-or-later.
//
// Lifted out of ForgeWorker's tick on 2026-09-24, unchanged in behaviour, so it can be tested
// without a timer, an EC or a sampler (FanTickPolicyTests) and driven by its own loop (FanWorker).
// It owns no hardware: it takes the fan mode, the manual duty, a temperature and a monotonic "now",
// and answers with one command. The worker is the only thing that turns that into an EC write.

namespace GpdForge.Fan;

public enum FanCommandKind { None, Auto, Duty }

/// <summary>One tick's instruction to the fan controller. <see cref="Duty"/> is 0..255, meaningful
/// only for <see cref="FanCommandKind.Duty"/>.</summary>
public readonly record struct FanCommand(FanCommandKind Kind, int Duty)
{
    /// <summary>Nothing to write this tick.</summary>
    public static readonly FanCommand None = new(FanCommandKind.None, 0);

    /// <summary>Hand the fan back to firmware.</summary>
    public static readonly FanCommand Auto = new(FanCommandKind.Auto, 0);

    public static FanCommand DutyOf(int duty) => new(FanCommandKind.Duty, duty);
}

/// <summary>
/// The fan decision, one call per tick. Stateful — the smoother, the ramp and the sensor grace all
/// remember the previous tick — but deterministic: the same inputs in the same order give the same
/// commands, with time only ever coming in through the <c>nowSeconds</c> argument.
/// </summary>
public sealed class FanTickPolicy
{
    /// <summary>
    /// A single missed sensor read must not hand the fan back to firmware: that SetAuto is followed
    /// by a MAX safety write when curve mode resumes, which is an audible burst. The last usable
    /// reading is reused for up to this long before giving up.
    /// </summary>
    public const double SensorGraceSeconds = 3.0;

    // _lastMode lets Auto restore fire only ONCE per transition (not every tick).
    private string? _lastMode;

    // Curve mode is a three-stage pipeline: TempSmoother (what temperature to react to) →
    // FanCurve.DutyForTemp (what duty that calls for, with hysteresis) → FanDutyRamp (how fast the
    // fan may get there). _lastTarget is the curve's own last answer, which is what its hysteresis
    // must compare against; the ramp holds what was actually written. All of it resets together
    // (ResetCurve) so a stale average or ramp never leaks into the next session.
    private readonly TempSmoother _smoother = new();
    private readonly FanDutyRamp _ramp = new();
    private int _lastTarget;

    // Elapsed time between curve ticks, measured rather than assumed: a late tick must count as the
    // time it really covered, because both the smoother and the ramp are rates.
    private double _lastCurveTickSeconds;

    private double? _lastUsableTempC;
    private double _lastUsableTempAtSeconds;

    /// <summary>
    /// The command for this tick.
    /// </summary>
    /// <param name="mode">FanState.Mode: Auto, Manual, Quiet, Balanced or Aggressive. Anything else
    /// is treated as invalid and hands the fan back to firmware.</param>
    /// <param name="manualDuty">FanState.ManualDuty, used only in Manual.</param>
    /// <param name="tempC">A NEW CPU temperature reading for this tick, or null when there is none
    /// (sensor missing, or no fresh sample since the previous tick). Zero and non-finite values are
    /// treated like null.</param>
    /// <param name="nowSeconds">A monotonic clock, in seconds.</param>
    /// <param name="sustained">The active power mode is a sustained one (<c>ai</c>): a curve mode
    /// holds its duty through short dips (<see cref="FanCurve.SustainedHysteresisC"/>). Auto and
    /// Manual ignore it — this shapes a curve the user chose, it never takes a fan they did not.</param>
    public FanCommand Next(string? mode, int manualDuty, double? tempC, double nowSeconds, bool sustained = false)
    {
        switch (mode)
        {
            case "Auto":
                // Only write on the transition INTO Auto, not every tick.
                if (_lastMode == "Auto") return FanCommand.None;
                return HandBack();

            case "Manual":
                if (_lastMode != "Manual") ResetCurve();
                _lastMode = "Manual";
                return FanCommand.DutyOf(manualDuty);

            case "Quiet" or "Balanced" or "Aggressive":
                return CurveTick(mode, tempC, nowSeconds, sustained);

            default:
                // Defense in depth for imported/legacy state: invalid state can never leave a
                // previous manual duty pinned. The HTTP API rejects it before this point. Written on
                // every tick, as the inline version did.
                return HandBack();
        }
    }

    /// <summary>Forgets everything, including which mode was last applied, so the next tick starts
    /// from scratch (and re-asserts Auto if that is the mode). For the worker's error path.</summary>
    public void Reset()
    {
        _lastMode = null;
        ResetCurve();
    }

    private FanCommand CurveTick(string mode, double? tempC, double nowS, bool sustained)
    {
        // A change of mode — including between two curves — restarts the clock (dt = 0): the elapsed
        // time since this mode last ran says nothing about the time the new pipeline has covered.
        double dtS = _lastMode == mode ? nowS - _lastCurveTickSeconds : 0;
        _lastCurveTickSeconds = nowS;

        // Zero/non-finite means telemetry is unavailable, not that the CPU is cold. Never take
        // firmware control without a trustworthy temperature sensor — but a single missed read is
        // reused briefly rather than bouncing the fan through firmware and back (SensorGraceSeconds).
        double useTempC;
        if (FanControlPolicy.IsUsableTemperature(tempC))
        {
            useTempC = tempC!.Value;
            _lastUsableTempC = useTempC;
            _lastUsableTempAtSeconds = nowS;
        }
        else if (_lastUsableTempC is double held && nowS - _lastUsableTempAtSeconds <= SensorGraceSeconds)
        {
            useTempC = held;
        }
        else
        {
            return HandBack();
        }

        var curve = FanCurve.ForMode(mode) ?? FanCurve.Balanced;
        // Smoothed, not raw: Tctl swings ten-plus degrees tick to tick under a bursty load, and
        // DutyForTemp never delays a rise. The thermal guardian reacts on its own input regardless,
        // so this never dilutes the safety margin.
        double smoothedTempC = _smoother.Add(useTempC, dtS);
        // Switching sustained on or off does not reset anything: only the width of the drop band
        // changes, so the fan neither jumps nor restarts its ramp when the power mode changes.
        double hysteresisC = sustained ? FanCurve.SustainedHysteresisC : FanCurve.DefaultHysteresisC;
        _lastTarget = FanCurve.DutyForTemp(smoothedTempC, curve, hysteresisC, _lastTarget);
        int duty = _ramp.Step(_lastTarget, dtS);
        _lastMode = mode;
        return FanCommand.DutyOf(duty);
    }

    private FanCommand HandBack()
    {
        _lastMode = "Auto";
        ResetCurve();
        return FanCommand.Auto;
    }

    private void ResetCurve()
    {
        _lastTarget = 0;
        _smoother.Reset();
        _ramp.Reset();
        _lastUsableTempC = null;
    }
}
