// GPD Forge — auto-TDP-to-target-FPS controller. GPL-3.0-or-later.
// Copyright (C) 2026 lexlaboratory. GPL-3.0-or-later. See ../../LICENSE.
//
// The "hold a target FPS by moving TDP" loop — the gaming headline feature. A frame-rate source
// feeds a pure, deterministic proportional-integral controller that nudges STAPM up when we are
// below the target frame rate and down when we are above it, so the handheld spends exactly the
// watts the game needs and no more (cooler, quieter, longer battery). The control math lives in a
// pure method (NextStapm) so it is unit-testable with zero hardware.
using Microsoft.Extensions.Logging;

namespace GpdForge.Tdp;

/// <summary>A source of the current frame rate, in frames per second.</summary>
/// <remarks>
/// Abstracted so the controller can be driven by the existing telemetry snapshot today and by a
/// real PresentMon/ETW feed later without any change to the control logic. This layer deliberately
/// does NOT integrate PresentMon/ETW — that is future work.
/// </remarks>
public interface IFpsSource
{
    /// <summary>The most recent frame-rate reading (fps). 0 when nothing is being presented.</summary>
    double CurrentFps();
}

/// <summary>
/// An <see cref="IFpsSource"/> that pulls the frame rate from a caller-supplied delegate — e.g.
/// <c>() =&gt; latestSnapshot.Fps</c> reading <c>TelemetrySnapshot.Fps</c>. Keeps the controller
/// decoupled from the telemetry service; the delegate is read live on every call.
/// </summary>
public sealed class TelemetryFpsSource : IFpsSource
{
    private readonly Func<double> _read;

    public TelemetryFpsSource(Func<double> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _read = read;
    }

    public double CurrentFps() => _read();
}

/// <summary>
/// Pure proportional-integral controller that maps a frame-rate error onto a new STAPM (sustained
/// power) target. Velocity form: <c>currentStapm</c> IS the integrator, so feeding the clamped
/// output back in as the next input gives integral action with built-in anti-windup — the
/// accumulator can never leave the [min,max] rail (clamping / conditional integration).
/// </summary>
public sealed class FpsTdpController
{
    /// <summary>Tunables. All immutable, so <see cref="NextStapm"/> stays deterministic.</summary>
    /// <param name="WattsPerFps">Gain: watts of STAPM change requested per fps of error.</param>
    /// <param name="MaxStepW">Slew limit: the most STAPM may move in a single tick (W).</param>
    /// <param name="DeadbandFps">Half-width of the "on target" band (fps); inside it STAPM is held to stop hunting.</param>
    public sealed record Options(double WattsPerFps = 0.5, double MaxStepW = 3.0, double DeadbandFps = 2.0);

    private readonly Options _opt;

    public FpsTdpController(Options? options = null) => _opt = options ?? new Options();

    /// <summary>
    /// Compute the next STAPM (whole watts) from the frame-rate error. Pure and deterministic: the
    /// result depends only on the arguments and the immutable options.
    /// <para>
    /// measured &lt; target (below the target fps) raises STAPM toward <paramref name="maxW"/>;
    /// measured &gt; target lowers it toward <paramref name="minW"/>; inside the deadband STAPM is
    /// held. The result is always clamped to [<paramref name="minW"/>, <paramref name="maxW"/>].
    /// </para>
    /// </summary>
    public int NextStapm(double targetFps, double measuredFps, int currentStapm, int minW, int maxW)
    {
        // Tolerate an inverted window instead of producing nonsense.
        if (maxW < minW)
            (minW, maxW) = (maxW, minW);

        // Anti-windup on entry: a stale / out-of-range accumulator can never leak through.
        int current = Clamp(currentStapm, minW, maxW);

        double error = targetFps - measuredFps;

        // Deadband: close enough to target — hold STAPM so we don't oscillate around it.
        if (Math.Abs(error) <= _opt.DeadbandFps)
            return current;

        // Proportional term over the velocity-form integrator, slew-limited so we ramp, not slam.
        double step = Math.Clamp(_opt.WattsPerFps * error, -_opt.MaxStepW, _opt.MaxStepW);
        int wholeStep = (int)Math.Round(step, MidpointRounding.AwayFromZero);

        // Guarantee progress on the integer-watt grid whenever we are outside the deadband, so a
        // tiny gain can never leave us permanently one step short of the target.
        if (wholeStep == 0)
            wholeStep = error > 0 ? 1 : -1;

        // Anti-windup on exit: clamp to the rail so the accumulator never winds up past it.
        return Clamp(current + wholeStep, minW, maxW);
    }

    private static int Clamp(int value, int lo, int hi) => Math.Max(lo, Math.Min(hi, value));
}

/// <summary>
/// Optional orchestrator that turns one control tick into a verified hardware apply: read fps →
/// compute the next STAPM → apply it through the closed-loop <see cref="ITdpController"/>, keeping
/// the current profile's fast/slow/tctl. No timer here — the caller (worker) owns the cadence; this
/// exposes a single testable <see cref="TickAsync"/>.
/// </summary>
public sealed class AutoFpsLoop
{
    /// <param name="MinW">Lowest STAPM the loop may request (device floor).</param>
    /// <param name="MaxW">Highest STAPM the loop may request (device / thermal ceiling).</param>
    public sealed record Options(int MinW = 8, int MaxW = 30);

    private readonly IFpsSource _fps;
    private readonly FpsTdpController _controller;
    private readonly ITdpController _tdp;
    private readonly Options _opt;
    private readonly ILogger<AutoFpsLoop>? _logger;

    public AutoFpsLoop(
        IFpsSource fps,
        FpsTdpController controller,
        ITdpController tdp,
        Options? options = null,
        ILogger<AutoFpsLoop>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(fps);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(tdp);
        _fps = fps;
        _controller = controller;
        _tdp = tdp;
        _opt = options ?? new Options();
        _logger = logger;
    }

    /// <summary>
    /// Run one control tick against <paramref name="current"/> (the profile in force) and return the
    /// STAPM that was requested. Only <c>StapmW</c> is steered; fast/slow/tctl are carried through so
    /// the caller keeps ownership of the rest of the profile. The apply is verified by the underlying
    /// closed-loop controller, and is re-issued every tick so firmware reverts are corrected.
    /// </summary>
    public async Task<int> TickAsync(double targetFps, TdpProfile current, CancellationToken ct)
    {
        double measured = _fps.CurrentFps();
        int nextStapm = _controller.NextStapm(targetFps, measured, current.StapmW, _opt.MinW, _opt.MaxW);

        TdpProfile applied = current with { StapmW = nextStapm };
        TdpApplyResult result = await _tdp.ApplyAsync(applied, TdpOwner.AutoFps, ct);

        _logger?.LogDebug(
            "auto-fps: target={Target:F1} measured={Measured:F1} stapm {Old}W->{New}W verified={Verified}",
            targetFps, measured, current.StapmW, nextStapm, result.Verified);

        return nextStapm;
    }
}
