// GPD Forge — which game profile is in force right now, and what it actually changed. GPL-3.0-or-later.
//
// "Automatic with notice" (Alex's decision for F1) needs the notice to be TRUE: the toast and the
// overlay line say what was applied, so this records what GameProfileApplier did, field by field —
// and, separately, what it refused to do and why (a cap below the auto-FPS target, a cap outside the
// driver's range). A field the rule sets that appears in neither list does not exist. In memory only:
// after a restart the focus loop settles on the game again and re-records it.
namespace GpdForge.Profiles;

/// <summary>The overrides that were layered. Null = left to the mode. FrameCapFps 0 = cap turned off.</summary>
public sealed record AppliedOverrides(int? StapmW, int? FrameCapFps, string? FanMode, bool? AntiLag, bool? Chill);

public sealed record SkippedOverride(string Field, string Reason);

/// <param name="Game">The process the focus loop judged (the game under the overlay, not the overlay).</param>
/// <param name="Freeze">The rule's freeze list, reported as STORED: nothing is frozen until F5.</param>
public sealed record ActiveGameProfile(
    string Game,
    Guid RuleId,
    string Match,
    string Mode,
    AppliedOverrides Applied,
    IReadOnlyList<SkippedOverride> Skipped,
    IReadOnlyList<string> Freeze,
    DateTimeOffset SinceUtc);

/// <summary>Written by the focus loop's thread, read by GET /profiles/active.</summary>
public sealed class ActiveGameProfileState
{
    private ActiveGameProfile? _current;

    public ActiveGameProfile? Current => Volatile.Read(ref _current);

    public void Set(ActiveGameProfile profile) => Volatile.Write(ref _current, profile);

    public void Clear() => Volatile.Write(ref _current, null);
}
