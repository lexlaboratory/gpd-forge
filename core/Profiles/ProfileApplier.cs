// GPD Forge - applies a mode's TDP through the closed loop, yielding to other controllers. GPL-3.0-or-later.
using GpdForge.Guardian;
using GpdForge.Tdp;
using Microsoft.Extensions.Logging;

namespace GpdForge.Profiles;

/// <summary>
/// <see cref="HeldByGuardian"/> (F1, 2026-09-25): the thermal guardian is throttling, so nothing was
/// written — its ceiling stays in force and the throttle-clear restore applies what was asked.
/// </summary>
public enum ApplyOutcome { UnknownMode, SkippedConflict, AppliedVerified, AppliedUnverified, HeldByGuardian }

/// <summary>An apply's outcome plus who it yielded to (<see cref="ApplyOutcome.SkippedConflict"/>), so a
/// game profile's notice can say which controller kept its watts (F1 audit round 1, 2026-09-25).</summary>
public sealed record ApplyReport(ApplyOutcome Outcome, IReadOnlyList<string> Rivals);

public sealed class ProfileApplier(
    ITdpController tdp,
    IPowerControllerDetector detector,
    ILogger<ProfileApplier>? logger = null,
    TdpIntent? intent = null,
    TdpState? state = null,
    GuardianService? guardian = null)
{
    /// <summary>
    /// Apply the mode's TDP preset — but only if GPD Forge is the sole power controller. If
    /// MotionAssistant / GPD Tool are running we yield (return SkippedConflict) so two controllers
    /// never fight. With hardware disabled the underlying backend is a no-op stub.
    /// <para>
    /// Every call ends a manual override (<see cref="TdpIntent"/>), before the yield check: the mode
    /// changed — or was deliberately re-picked — whether or not GPD Forge got to write it, and an
    /// override left behind would resurface through the next restore once the rival exits.
    /// </para>
    /// <para>
    /// A yield also marks the last write STALE (<see cref="TdpState.MarkStale"/>): it was the previous
    /// mode's, and the 30 s reassert must not put it back once the rival exits.
    /// </para>
    /// <para>
    /// F1 (2026-09-25): when a game profile is layered on this mode (<see cref="TdpIntent.Game"/>), that
    /// is what gets written, under <see cref="TdpOwner.GameProfile"/> — one write for "switch to gaming
    /// for Elden Ring", not the preset followed by the game's watts a moment later. And while the
    /// thermal guardian is throttling nothing is written at all: its ceiling is computed under the
    /// intent every tick (so a lower game value still lowers it), and the throttle-clear restore puts
    /// the intent back. Writing here would lift a hot device out of its throttle for up to the
    /// guardian's 30 s re-assert — true of a plain mode pick too, which had the same hole.
    /// </para>
    /// </summary>
    public async Task<ApplyOutcome> ApplyAsync(string mode, CancellationToken ct) =>
        (await ApplyWithReportAsync(mode, clearManual: true, ct)).Outcome;

    /// <summary>
    /// <see cref="ApplyAsync"/>, with the rivals it yielded to. <paramref name="clearManual"/> false is
    /// for a write where only the GAME layer moved and the mode did not (F1 audit round 1, 2026-09-25):
    /// ending the manual override there broke TdpIntent's rule that it lives until the mode changes —
    /// alt-tabbing out of Elden Ring, or saving only its fan mode, dropped the 12 W set in the overlay.
    /// </summary>
    public async Task<ApplyReport> ApplyWithReportAsync(string mode, bool clearManual, CancellationToken ct)
    {
        var preset = ModeProfiles.For(mode);
        if (preset is null) return new ApplyReport(ApplyOutcome.UnknownMode, []);

        if (clearManual) intent?.ClearManual();
        var manual = clearManual ? null : intent?.Manual(mode);
        var game = intent?.Game(mode);
        var profile = manual ?? game ?? preset.Value;
        var owner = manual is not null ? TdpOwner.Manual : game is null ? TdpOwner.Mode : TdpOwner.GameProfile;

        if (detector.OthersRunning(out var names))
        {
            logger?.LogInformation("Yielding TDP for '{Mode}': another power controller is active ({Names}).",
                mode, string.Join(", ", names));
            state?.MarkStale();
            return new ApplyReport(ApplyOutcome.SkippedConflict, names);
        }

        if (guardian is { Throttling: true })
        {
            logger?.LogInformation("'{Mode}' TDP (STAPM {W}W, {Owner}) held: the thermal guardian is throttling to {Ceiling}W and restores it when it clears.",
                mode, profile.StapmW, owner, guardian.ThrottledToW);
            return new ApplyReport(ApplyOutcome.HeldByGuardian, []);
        }

        var r = await tdp.ApplyAsync(profile, owner, ct);
        logger?.LogInformation("Applied '{Mode}' TDP ({Owner}): STAPM {W}W -> {Verdict}",
            mode, owner, profile.StapmW, r.Verified ? "verified" : "UNVERIFIED");

        return new ApplyReport(r.Verified ? ApplyOutcome.AppliedVerified : ApplyOutcome.AppliedUnverified, []);
    }
}
