// GPD Forge — which game profile is in force right now, and what it actually changed. GPL-3.0-or-later.
//
// "Automatic with notice" (Alex's decision for F1) needs the notice to be TRUE: the toast and the
// overlay line say what was applied, so this records what GameProfileApplier did, field by field —
// and, separately, what it refused to do and why (a cap below the auto-FPS target, a cap outside the
// driver's range). A field the rule sets that appears in neither list does not exist. In memory only:
// after a restart the focus loop settles on the game again and re-records it.
//
// F1 audit round 1 (2026-09-25): a record made at Begin goes stale. The user sets 18 W in the overlay,
// picks Quiet, changes the cap — and the overlay's live line kept saying "22 W · 60 FPS · Aggressive"
// beside controls showing otherwise. So what the profile still holds is derived when it is READ
// (ActiveGameProfileView), from the owners themselves; this record keeps only what that needs: the
// cap request it made (CapVersion) and the TDP apply's outcome (TdpHold).
using GpdForge.Gpu;

namespace GpdForge.Profiles;

/// <summary>The overrides that were layered. Null = left to the mode. FrameCapFps 0 = cap turned off.</summary>
/// <remarks><paramref name="Image"/>: the RSR / RIS fields requested (F4), null when none was.</remarks>
public sealed record AppliedOverrides(int? StapmW, int? FrameCapFps, string? FanMode, bool? AntiLag, bool? Chill,
    GpuImageRequest? Image = null);

public sealed record SkippedOverride(string Field, string Reason);

/// <summary>Why the game's watts were not written when the profile went on: another power controller
/// was running (<paramref name="Rivals"/>), or the thermal guardian was throttling.</summary>
public sealed record TdpHold(ApplyOutcome Outcome, IReadOnlyList<string> Rivals);

/// <param name="Game">The process the focus loop judged (the game under the overlay, not the overlay).</param>
/// <param name="Freeze">The rule's freeze list, as stored. What was actually suspended is <see cref="Frozen"/>.</param>
public sealed record ActiveGameProfile(
    string Game,
    Guid RuleId,
    string Match,
    string Mode,
    AppliedOverrides Applied,
    IReadOnlyList<SkippedOverride> Skipped,
    IReadOnlyList<string> Freeze,
    DateTimeOffset SinceUtc)
{
    /// <summary>GpuDesiredState.CapVersion right after this profile's cap request; null when it made none.
    /// Anything else there later means someone asked for another cap since.</summary>
    public long? CapVersion { get; init; }

    /// <summary>GpuDesiredState.ImageVersion right after this profile's RSR / RIS request; null when it
    /// made none. Anything else there later means the Display page asked for something since.</summary>
    public long? ImageVersion { get; init; }

    /// <summary>The names F5 actually suspended for this game (running, not protected, not frozen by
    /// hand already). Empty when it froze nothing.</summary>
    public IReadOnlyList<string> Frozen { get; init; } = [];

    /// <summary>Set when the TDP write that should have carried the game's watts did not happen.</summary>
    public TdpHold? TdpHold { get; init; }
}

/// <summary>Written by the focus loop's thread, read by GET /profiles/active.</summary>
public sealed class ActiveGameProfileState
{
    private ActiveGameProfile? _current;

    public ActiveGameProfile? Current => Volatile.Read(ref _current);

    public void Set(ActiveGameProfile profile) => Volatile.Write(ref _current, profile);

    public void Clear() => Volatile.Write(ref _current, null);
}
