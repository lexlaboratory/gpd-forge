// GPD Forge — pure normalization + validation for per-app rules. GPL-3.0-or-later.
namespace GpdForge.Profiles;

public static class AppRulePolicy
{
    public const int MaxMatchLength = 120;

    // "standby" is a ModeProfiles preset for a system state, not something a foreground app should
    // ever be able to select — a rule that put the machine into standby mode on focus would be a
    // trap. The remaining four are the usage modes the UI offers.
    private static readonly string[] Modes = ["battery", "windows", "gaming", "ai"];

    public static IReadOnlyList<string> SelectableModes => Modes;

    /// <summary>Canonical form of a process fragment: trimmed, lowercase, without a ".exe" tail.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var p = value.Trim().ToLowerInvariant();
        if (p.EndsWith(".exe", StringComparison.Ordinal)) p = p[..^4];
        return p.Trim();
    }

    public static bool IsValidMode(string? mode) => mode is not null && Modes.Contains(mode, StringComparer.Ordinal);

    public static bool Matches(string? match, string? processName)
    {
        var needle = Normalize(match);
        var process = Normalize(processName);
        return needle.Length > 0 && process.Length > 0 && process.Contains(needle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Null when the rule is acceptable, otherwise the reason. <paramref name="excluding"/> is the
    /// rule being edited, so it does not collide with itself.
    /// </summary>
    public static string? Validate(string? match, string? mode, IEnumerable<AppRule> existing, Guid? excluding = null)
    {
        var needle = Normalize(match);
        if (needle.Length == 0) return "A rule needs a process name to match.";
        if (needle.Length > MaxMatchLength) return $"Process name is too long (max {MaxMatchLength} characters).";
        if (!IsValidMode(mode)) return $"Unknown mode '{mode}'. Valid modes: {string.Join(", ", Modes)}.";
        if (existing.Any(r => r.Id != excluding && string.Equals(r.Match, needle, StringComparison.Ordinal)))
            return $"A rule for '{needle}' already exists.";
        return null;
    }
}
