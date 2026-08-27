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

    public static bool IsUsableTemperature(double tempC) => double.IsFinite(tempC) && tempC > 0;

    public static bool IsValidMode(string? mode) => mode is not null && ValidModes.Contains(mode);
}
