// GPD Forge — POST /tdp's write, conditional on the mode it was asked in. GPL-3.0-or-later.
//
// The endpoint read the mode, recorded the override and queued for the write gate as three separate
// steps. A POST /mode arriving between them set the new mode, ended the override (ProfileApplier) and,
// if it reached the gate first, wrote its preset — and then the queued manual write landed on top,
// with the OLD mode's Tctl. GET /tdp then named the new mode, reported no override (manualStapmW:
// null), and the 30 s reassert kept that orphaned manual profile in force every 30 s until something
// else wrote (audit round 2, 2026-09-24).
//
// The write is now conditional, checked INSIDE the gate: the mode is still the one it was asked in and
// the override is still this one. Otherwise it stands down — the newer decision already holds the
// machine — and the caller says so rather than reporting a write that did not happen.
using GpdForge.Api;
using GpdForge.Tdp;

namespace GpdForge.Profiles;

public static class ManualTdpWrite
{
    /// <summary>
    /// Records <paramref name="stapmW"/> as the active mode's override and writes it. Null when a mode
    /// change (or a later POST /tdp) superseded it while it waited for the gate; nothing was written.
    /// The caller validates the band first (<see cref="TdpIntent.IsManualInRange"/>).
    /// </summary>
    public static Task<TdpApplyResult?> ApplyAsync(
        SerializedTdpController tdp, ModeState mode, TdpIntent intent, int stapmW, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tdp);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(intent);

        string askedIn = mode.Active;
        var profile = TdpIntent.ManualProfile(stapmW, askedIn);
        intent.SetManual(askedIn, profile);

        return tdp.ApplyIfAsync(
            () => string.Equals(mode.Active, askedIn, StringComparison.Ordinal) && intent.Manual(askedIn) == profile,
            profile, TdpOwner.Manual, ct);
    }
}
