// GPD Forge — thermal/battery guardian (stateful wrapper over the pure evaluator). GPL-3.0-or-later.
using GpdForge.Telemetry;

namespace GpdForge.Guardian;

/// <summary>
/// Holds guardian config + state and turns each telemetry snapshot into an action for the worker.
/// Throttle actions are gated by <see cref="GuardianConfig.AutoThrottle"/>; alerts always surface.
/// Thread-safe: the worker calls <see cref="Observe"/> while the API may call <see cref="Configure"/>.
/// </summary>
public sealed class GuardianService(Func<double>? secondsNow = null)
{
    // The throttle band reacts to a short time-weighted average of Tctl, not the raw reading. Tctl
    // on this APU is an instantaneous control temperature that pokes past 90°C for a single second
    // under a bursty game load; reacting to that flattened the boost limits and then held them
    // until the RAW reading happened to dip to 86°C — the "does not hold the watts" complaint.
    // τ = 2 s still throttles a sustained excursion within a few seconds, the critical limit keeps
    // reacting to the raw reading instantly, and the firmware's own Tctl limit sits behind both.
    public const double ThrottleSmoothingTauSeconds = 2.0;

    private readonly Func<double> _now = secondsNow ?? DefaultClock;
    private readonly GpdForge.Fan.TempSmoother _smoother =
        new(ThrottleSmoothingTauSeconds, ThrottleSmoothingTauSeconds);
    private double? _lastObservedAt;

    private readonly object _lock = new();
    private int? _throttleW;
    private bool _pendingClear;

    public GuardianConfig Config { get; private set; } = new();
    public string? LastAlert { get; private set; }
    public string LastSeverity { get; private set; } = "ok";
    public bool Throttling => _throttleW is not null;
    public int? ThrottledToW => _throttleW;

    public void Configure(GuardianConfig config)
    {
        lock (_lock)
        {
            bool wasActive = _throttleW is not null;
            Config = config;
            // If the guardian (or just auto-throttle) is being turned off while we were holding a
            // throttle, ask the worker to restore the normal preset on the next tick.
            if ((!config.Enabled || !config.AutoThrottle) && wasActive) { _throttleW = null; _pendingClear = true; }
        }
    }

    /// <summary>Evaluate a snapshot, update state, and return the effective (auto-throttle-gated) decision.</summary>
    public GuardianDecision Observe(TelemetrySnapshot t)
    {
        lock (_lock)
        {
            if (_pendingClear)
            {
                _pendingClear = false;
                return new GuardianDecision(null, true, "Guardian disabled — throttle cleared", "info");
            }

            GuardianDecision d = GuardianEvaluator.Evaluate(Smoothed(t), Config, _throttleW);
            if (d.Alert is not null) { LastAlert = d.Alert; LastSeverity = d.Severity; }

            if (d.ClearThrottle) _throttleW = null;
            else if (Config.AutoThrottle && d.ThrottleToW is int w) _throttleW = w;

            return Config.AutoThrottle ? d : d with { ThrottleToW = null, ClearThrottle = false };
        }
    }

    /// <summary>The snapshot the evaluator sees: CPU temperature replaced by its short average,
    /// except at or above the critical limit, where the raw reading goes straight through.</summary>
    private TelemetrySnapshot Smoothed(TelemetrySnapshot t)
    {
        double now = _now();
        double dt = _lastObservedAt is double last ? now - last : 0;
        _lastObservedAt = now;

        if (t.CpuTempC is not double raw)
        {
            _smoother.Reset();   // blind: the next reading starts fresh rather than from stale history
            return t;
        }
        double avg = _smoother.Add(raw, dt);
        return raw >= Config.TempCriticalC ? t : t with { CpuTempC = avg };
    }

    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static double DefaultClock() => Clock.Elapsed.TotalSeconds;
}
