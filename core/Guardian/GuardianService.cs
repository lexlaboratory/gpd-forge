// GPD Forge — thermal/battery guardian (stateful wrapper over the pure evaluator). GPL-3.0-or-later.
using GpdForge.Telemetry;

namespace GpdForge.Guardian;

/// <summary>
/// Holds guardian config + state and turns each telemetry snapshot into an action for the worker.
/// Throttle actions are gated by <see cref="GuardianConfig.AutoThrottle"/>; alerts always surface.
/// Thread-safe: the worker calls <see cref="Observe"/> while the API may call <see cref="Configure"/>.
/// </summary>
public sealed class GuardianService
{
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

            GuardianDecision d = GuardianEvaluator.Evaluate(t, Config, _throttleW);
            if (d.Alert is not null) { LastAlert = d.Alert; LastSeverity = d.Severity; }

            if (d.ClearThrottle) _throttleW = null;
            else if (Config.AutoThrottle && d.ThrottleToW is int w) _throttleW = w;

            return Config.AutoThrottle ? d : d with { ThrottleToW = null, ClearThrottle = false };
        }
    }
}
