// GPD Forge - applies a mode's TDP through the closed loop, yielding to other controllers. GPL-3.0-or-later.
using GpdForge.Gpu;
using GpdForge.Tdp;
using Microsoft.Extensions.Logging;

namespace GpdForge.Profiles;

public enum ApplyOutcome { UnknownMode, SkippedConflict, AppliedVerified, AppliedUnverified }

public sealed class ProfileApplier(
    ITdpController tdp,
    IPowerControllerDetector detector,
    ILogger<ProfileApplier>? logger = null,
    GpuProfileApplier? gpu = null)
{
    /// <summary>
    /// Apply the mode's TDP preset — but only if GPD Forge is the sole power controller. If
    /// MotionAssistant / GPD Tool are running we yield (return SkippedConflict) so two controllers
    /// never fight. With hardware disabled the underlying backend is a no-op stub.
    /// </summary>
    public async Task<ApplyOutcome> ApplyAsync(string mode, CancellationToken ct)
    {
        var profile = ModeProfiles.For(mode);
        if (profile is null) return ApplyOutcome.UnknownMode;

        if (detector.OthersRunning(out var names))
        {
            logger?.LogInformation("Yielding TDP for '{Mode}': another power controller is active ({Names}).",
                mode, string.Join(", ", names));
            return ApplyOutcome.SkippedConflict;
        }

        var r = await tdp.ApplyAsync(profile.Value, ct);
        logger?.LogInformation("Applied '{Mode}' TDP: STAPM {W}W -> {Verdict}",
            mode, profile.Value.StapmW, r.Verified ? "verified" : "UNVERIFIED");

        ApplyGpuProfile(mode);
        return r.Verified ? ApplyOutcome.AppliedVerified : ApplyOutcome.AppliedUnverified;
    }

    /// <summary>
    /// Apply the mode's Radeon settings, if this build can. Every caller that sets a mode reaches
    /// here — the focus worker, a manual switch, the AC/battery rule, the standby restore — so the
    /// GPU follows the mode from all of them without any of them knowing about ADLX.
    ///
    /// Deliberately NOT behind the conflict guard above. That guard exists because two programs
    /// writing TDP fight over the same silicon; it says nothing about the Radeon 3D settings, which
    /// MotionAssistant does not touch. Yielding the GPU because a rival power tool is running would
    /// be a guess dressed up as caution.
    ///
    /// Never throws into the caller: a driver-API problem must not turn a successful TDP apply into a
    /// failure. It is logged, and the outcome is readable from GET /gpu.
    /// </summary>
    private void ApplyGpuProfile(string mode)
    {
        if (gpu is null) return;
        try
        {
            var outcome = gpu.ApplyForMode(mode);
            if (outcome.Attempted) logger?.LogInformation("{Reason}", outcome.Reason);
            else logger?.LogDebug("GPU profile not applied for '{Mode}': {Reason}", mode, outcome.Reason);
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "Applying the GPU profile for '{Mode}' failed.", mode);
        }
    }
}
