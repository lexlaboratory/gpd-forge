// GPD Forge - applies a mode's TDP through the closed loop, yielding to other controllers. GPL-3.0-or-later.
using GpdForge.Tdp;
using Microsoft.Extensions.Logging;

namespace GpdForge.Profiles;

public enum ApplyOutcome { UnknownMode, SkippedConflict, AppliedVerified, AppliedUnverified }

public sealed class ProfileApplier(
    ITdpController tdp,
    IPowerControllerDetector detector,
    ILogger<ProfileApplier>? logger = null,
    TdpIntent? intent = null,
    TdpState? state = null)
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
    /// </summary>
    public async Task<ApplyOutcome> ApplyAsync(string mode, CancellationToken ct)
    {
        var profile = ModeProfiles.For(mode);
        if (profile is null) return ApplyOutcome.UnknownMode;

        intent?.ClearManual();

        if (detector.OthersRunning(out var names))
        {
            logger?.LogInformation("Yielding TDP for '{Mode}': another power controller is active ({Names}).",
                mode, string.Join(", ", names));
            state?.MarkStale();
            return ApplyOutcome.SkippedConflict;
        }

        var r = await tdp.ApplyAsync(profile.Value, TdpOwner.Mode, ct);
        logger?.LogInformation("Applied '{Mode}' TDP: STAPM {W}W -> {Verdict}",
            mode, profile.Value.StapmW, r.Verified ? "verified" : "UNVERIFIED");

        return r.Verified ? ApplyOutcome.AppliedVerified : ApplyOutcome.AppliedUnverified;
    }

}
