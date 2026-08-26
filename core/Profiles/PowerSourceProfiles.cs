// GPD Forge — per-power-source auto mode-switch: pure evaluator over AC/battery state. GPL-3.0-or-later.
//
// Lets a user say "switch to Battery mode the instant I unplug, Windows mode the instant I plug
// back in" without relying on the per-app focus rules. ForgeWorker tracks the AC edge and calls
// Resolve only on a flip; the config itself lives in a small API-facing singleton (see
// GpdForge.Api.PowerSourceState in Program.cs) alongside FanState/AutoFpsState — the same shape as
// the rest of the local API's mutable settings.
namespace GpdForge.Profiles;

/// <summary>Per-power-source auto-switch settings. A record CLASS (not struct) so `new
/// PowerSourceConfig()` actually applies these parameter defaults, and so `with` partial-updates
/// (mirroring GuardianConfig) work from the API.</summary>
public sealed record PowerSourceConfig(
    bool Enabled = false,
    string OnBatteryMode = "battery",
    string OnAcMode = "windows");

/// <summary>Pure, side-effect-free decision: given the current AC state and the active mode, which
/// mode (if any) should become active. Trivially unit-testable — no I/O, no clock.</summary>
public static class PowerSourceProfiles
{
    /// <summary>
    /// Returns the mode <paramref name="config"/> wants for the given power source, or null if
    /// nothing should change (feature disabled, target mode unset/blank, or already there). Callers
    /// are expected to invoke this only when the power source actually changed (see ForgeWorker) —
    /// this function itself doesn't track edges, it just answers "what should the mode be right now".
    /// </summary>
    public static string? Resolve(bool acConnected, PowerSourceConfig config, string currentMode)
    {
        if (!config.Enabled) return null;

        string desired = acConnected ? config.OnAcMode : config.OnBatteryMode;
        if (string.IsNullOrWhiteSpace(desired)) return null;

        return desired == currentMode ? null : desired;
    }
}
