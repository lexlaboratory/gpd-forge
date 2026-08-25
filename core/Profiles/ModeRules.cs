// GPD Forge - foreground-process -> mode rules. GPL-3.0-or-later.
// Maps the focused app to a usage mode. Data-driven and overridable.
namespace GpdForge.Profiles;

public sealed class ModeRules
{
    private readonly (string mode, string[] needles)[] _rules;

    public ModeRules((string mode, string[] needles)[] rules) => _rules = rules;

    /// <summary>Reasonable defaults. Process names are matched case-insensitively as substrings.</summary>
    public static ModeRules Default() => new(
    [
        ("ai", ["ollama", "lmstudio", "lm studio", "koboldcpp", "jan", "gpt4all", "text-generation", "comfyui"]),
        ("gaming", ["steam", "gamescope", "retroarch", "rpcs3", "cemu", "yuzu", "ryujinx", "dolphin", "pcsx2", "duckstation"]),
    ]);

    /// <summary>The mode this process implies, or null if it has no opinion.</summary>
    public string? ModeFor(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        var p = processName.ToLowerInvariant();
        if (p.EndsWith(".exe")) p = p[..^4];
        foreach (var (mode, needles) in _rules)
            if (needles.Any(n => p.Contains(n, StringComparison.Ordinal)))
                return mode;
        return null;
    }
}
