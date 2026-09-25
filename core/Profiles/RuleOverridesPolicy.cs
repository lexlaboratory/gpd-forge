// GPD Forge — validation and repair for per-game overrides. GPL-3.0-or-later.
//
// Two different jobs, deliberately kept apart. The API REFUSES bad input (Validate): a user who types
// 50 W is told the band, not silently handed 40. The file loader REPAIRS (Sanitize): a hand-edited
// app-rules.json with one bad value must not cost the user every rule in it, which is what the
// quarantine would do — so an out-of-band watt figure is clamped the way ModeProfiles.Set clamps a
// preset, and anything that cannot be repaired into a meaning (an unknown fan mode) becomes null,
// i.e. "the mode decides", never a guess.
using GpdForge.Gpu;

namespace GpdForge.Profiles;

public static class RuleOverridesPolicy
{
    /// <summary>The STAPM band every mode preset is clamped to (ModeProfiles.Set) and POST /tdp accepts.</summary>
    public const int MinStapmW = TdpIntent.ManualMinW;
    public const int MaxStapmW = TdpIntent.ManualMaxW;

    /// <summary>A frame cap of 0 turns the driver cap off for this game.</summary>
    public const int FrameCapOff = 0;

    public const int MaxFreeze = 32;

    /// <summary>The fan modes a game may ask for. Manual is excluded: a fixed duty is a hand setting
    /// for right now, and replaying it on every launch of a game is how a fan ends up pinned low on
    /// a hot day.</summary>
    public static readonly IReadOnlyList<string> FanModes = ["Auto", "Quiet", "Balanced", "Aggressive"];

    public static OverrideError? Validate(RuleOverrides? o)
    {
        if (o is null) return null;
        if (o.StapmW is int w && w is < MinStapmW or > MaxStapmW)
            return new("bad_stapm", $"stapmW must be between {MinStapmW} and {MaxStapmW} W.");
        if (o.FrameCapFps is int fps && fps != FrameCapOff && GpuDesiredState.Reject(fps, null, null) is string why)
            return new("bad_frame_cap", $"frameCapFps must be 0 (cap off) or a frame rate. {why}");
        if (o.FanMode is string fan && !FanModes.Contains(fan, StringComparer.Ordinal))
            return new("bad_fan_mode", $"fanMode must be one of {string.Join(", ", FanModes)}.");
        if (o.Gpu is { AntiLag: true, Chill: true })
            return new("bad_gpu", "Radeon Chill cannot be on together with Anti-Lag; AMD's driver refuses the pair.");
        if (o.Freeze is { } freeze)
        {
            if (freeze.Count > MaxFreeze)
                return new("bad_freeze", $"freeze holds at most {MaxFreeze} process names.");
            if (freeze.Any(p => !IsFreezable(p)))
                return new("bad_freeze", "freeze entries must be bare process names (no paths).");
        }
        return null;
    }

    /// <summary>Canonical form of already-valid overrides: freeze names normalised like a rule's
    /// match and de-duplicated, an empty gpu block dropped, and nothing-at-all collapsed to null.</summary>
    public static RuleOverrides? Normalize(RuleOverrides? o)
    {
        if (o is null) return null;
        var freeze = o.Freeze?.Select(AppRulePolicy.Normalize).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var n = o with
        {
            Gpu = o.Gpu is { IsEmpty: false } ? o.Gpu : null,
            Freeze = freeze is { Length: > 0 } ? freeze : null,
        };
        return n.IsEmpty ? null : n;
    }

    /// <summary>Load-time repair: what can be clamped is, what cannot be understood becomes null.</summary>
    public static RuleOverrides? Sanitize(RuleOverrides? o)
    {
        if (o is null) return null;
        var fixedUp = new RuleOverrides(
            StapmW: o.StapmW is int w ? Math.Clamp(w, MinStapmW, MaxStapmW) : null,
            FrameCapFps: o.FrameCapFps is int fps && Validate(new RuleOverrides(FrameCapFps: fps)) is null ? fps : null,
            FanMode: o.FanMode is string fan && FanModes.Contains(fan, StringComparer.Ordinal) ? fan : null,
            Gpu: o.Gpu is { AntiLag: true, Chill: true } ? null : o.Gpu,
            Freeze: o.Freeze?.Where(IsFreezable).Take(MaxFreeze).ToArray());
        return Normalize(fixedUp);
    }

    // SessionForegroundApp's rule for a process name the agent reports, reused rather than restated:
    // a name the foreground can carry is exactly a name a rule can freeze. (It accepts null; a freeze
    // entry must be a name, so blanks are refused here.)
    private static bool IsFreezable(string? p) =>
        !string.IsNullOrWhiteSpace(p) && SessionForegroundApp.IsValidProcessName(p);
}
