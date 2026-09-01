// GPD Forge — centralized fan-control safety policy. GPL-3.0-or-later.

namespace GpdForge.Fan;

public static class FanControlPolicy
{
    private static readonly HashSet<string> ValidModes =
        new(StringComparer.Ordinal) { "Auto", "Quiet", "Balanced", "Aggressive", "Manual" };

    public static bool IsGateOpen(bool hardwareEnabled, bool fanControlEnabled) =>
        hardwareEnabled && fanControlEnabled;

    public static bool IsEnvironmentGateOpen() => IsGateOpen(
        Environment.GetEnvironmentVariable("GPDFORGE_ENABLE_HARDWARE") == "1",
        Environment.GetEnvironmentVariable("GPDFORGE_ENABLE_FAN_CONTROL") == "1");

    /// <summary>
    /// Whether a temperature is trustworthy enough to drive the fan from.
    ///
    /// Takes a nullable since 2026-09-01: telemetry now reports null for a sensor it cannot read,
    /// where it used to report 0. Both mean the same thing here and both are refused — this is the
    /// guard that hands the fan back to firmware rather than running a curve off a number nobody
    /// measured.
    /// </summary>
    public static bool IsUsableTemperature(double? tempC) =>
        tempC is double t && double.IsFinite(t) && t > 0;

    public static bool IsValidMode(string? mode) => mode is not null && ValidModes.Contains(mode);
}
