// GPD Forge — keeps the TDP GPD Forge wrote actually in force: read back every 30 s, write only on a
// difference. GPL-3.0-or-later.
//
// MotionAssistant holds its limits by re-applying them blind every 30 s: two SMU writes a minute
// whether or not anything moved, and no way to tell whether the firmware took them. GPD Forge's
// closed loop verifies each write ONCE (ClosedLoopTdpController), and until 2026-09-24 nothing
// noticed a limit that moved afterwards — the firmware putting its own STAPM back after a power
// event, or another tool's write. This is the missing half, and it is deliberately not a blind
// re-apply:
//
//   - it READS first (`ryzenadj --info`) and writes only when the readback differs from what GPD
//     Forge last wrote, judged by the closed loop's own Holds rule;
//   - a failed or partial read is "unknown", never "moved" — no write on no evidence;
//   - nothing written since start means nothing owned, and nothing is adopted or written;
//   - it yields to a rival power controller exactly as ProfileApplier does (two controllers
//     applying TDP collapse the device, field-confirmed);
//   - any other write that starts or finishes while the check runs wins: the re-apply is dropped
//     rather than undoing it. The check-and-write is atomic against every other writer
//     (SerializedTdpController) — until the 2026-09-24 audit it only saw writes that had FINISHED, so
//     a POST /tdp still in its closed loop could be overwritten by the stale profile.
//
// "What GPD Forge last wrote" is TdpState.Last — whoever wrote it: the mode, a manual override, a
// guardian ceiling, auto-FPS. Re-asserting any other profile (the preset, say) would silently undo
// the owner that is actually in charge. The re-apply is recorded under its own owner,
// TdpOwner.Reassert, so GET /tdp and the audit log say that it happened and why.
//
// The one exception is a last write the user has since moved away from: a mode switch that YIELDED
// to MotionAssistant or GPD Tool (TdpState.MarkStale). That write was the previous mode's; putting it
// back once the rival exits would hold a TDP while GET /mode named another mode (audit, 2026-09-24).
// Then what is kept is the CURRENT intent — the active mode's preset, or its manual override.
using GpdForge.Api;
using GpdForge.Profiles;
using Microsoft.Extensions.Logging;

namespace GpdForge.Tdp;

public enum ReassertOutcome
{
    /// <summary>Nothing has written TDP since the daemon started: nothing to keep.</summary>
    NothingOwned,
    /// <summary>A rival power controller is running; neither read nor written.</summary>
    Yielded,
    /// <summary>The readback had no STAPM or no fast limit; nothing written.</summary>
    Unreadable,
    /// <summary>The readback matches what was written; nothing written.</summary>
    Holding,
    /// <summary>Another write started or finished during the check; the stale re-apply was dropped.</summary>
    Superseded,
    /// <summary>The limit had moved; it was re-applied and read back as holding.</summary>
    Reasserted,
    /// <summary>The limit had moved; it was re-applied and the firmware still did not take it.</summary>
    NotHeld,
}

public sealed class TdpReasserter(
    ITdpBackend backend,
    SerializedTdpController tdp,
    TdpState state,
    IPowerControllerDetector detector,
    TdpIntent intent,
    ModeState mode,
    TimeProvider? time = null,
    ILogger<TdpReasserter>? logger = null)
{
    /// <summary>MotionAssistant's cadence, so a limit it would have held is held here too — with a
    /// read in place of a write whenever nothing moved, which is nearly always.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    /// <summary>The closed loop's default tolerance: "holding" means what "verified" means.</summary>
    private const int ToleranceW = 1;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _lastCheck = (time ?? TimeProvider.System).GetTimestamp();
    private bool _unreadableLogged;

    /// <summary>
    /// Runs <see cref="ReassertAsync"/> when <see cref="DefaultInterval"/> has passed since the last
    /// check (or since construction), else returns null without touching anything. Monotonic time,
    /// so a wall-clock change cannot stall or burst it. Called from ForgeWorker's tick. The tick's own
    /// writes cannot overlap it, but POST /tdp, POST /mode, POST /panic and the resume restore run on
    /// other threads — <see cref="ReassertAsync"/> is what keeps it safe against those.
    /// </summary>
    public async Task<ReassertOutcome?> ReassertIfDueAsync(CancellationToken ct)
    {
        if (_time.GetElapsedTime(_lastCheck) < DefaultInterval) return null;
        _lastCheck = _time.GetTimestamp();
        return await ReassertAsync(ct);
    }

    /// <summary>One check: read back, compare with what should be in force, re-apply only on a difference.</summary>
    public async Task<ReassertOutcome> ReassertAsync(CancellationToken ct)
    {
        // Read once, under one lock: the generation is what the final write is checked against, so
        // anything that writes (or yields) after this line makes the re-apply stand down.
        var ownership = state.Ownership;
        if (ownership.Last is not TdpSnapshot owned) return ReassertOutcome.NothingOwned;

        // A write already running will record its own result; comparing the hardware with the profile
        // it is replacing would only ever find a "difference".
        if (ownership.Writing) return ReassertOutcome.Superseded;

        if (detector.OthersRunning(out var rivals))
        {
            logger?.LogDebug("TDP reassert skipped: another power controller is active ({Names}).",
                string.Join(", ", rivals));
            return ReassertOutcome.Yielded;
        }

        // What to keep. Normally the last write, whoever made it. After a mode switch that yielded,
        // that write is the previous mode's, so the current intent is kept instead — never the stale one.
        TdpProfile want = owned.Requested;
        if (ownership.Stale)
        {
            if (intent.Resolve(mode.Active) is not TdpProfile current) return ReassertOutcome.NothingOwned;
            want = current;
        }

        // A throw is a failed read, not a crash: this runs inside ForgeWorker's tick, and a periodic
        // `ryzenadj --info` that cannot launch (the tool uninstalled, a locked driver) must cost the
        // reassert, never the worker — the guardian lives in the same loop.
        TdpReadout observed;
        try { observed = await backend.ReadAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogDebug(ex, "TDP reassert: readback threw.");
            observed = new TdpReadout(null, null);
        }

        if (observed.StapmW is null || observed.PptW is null)
        {
            // Once per outage, not every 30 s: a ryzenadj that cannot read its table (no driver, a PM
            // table version it does not know) stays that way, and the closed loop already reports each
            // of its own writes as unverified.
            if (!_unreadableLogged)
                logger?.LogWarning("TDP reassert: the limits could not be read back ({Observed}); nothing re-applied.", observed);
            _unreadableLogged = true;
            return ReassertOutcome.Unreadable;
        }
        _unreadableLogged = false;

        if (ClosedLoopTdpController.Holds(observed, want, ToleranceW)) return ReassertOutcome.Holding;

        if (ownership.Stale)
            logger?.LogInformation(
                "TDP: mode '{Mode}' was selected while another power controller held TDP. It is gone, so applying STAPM {Want}W / fast {WantFast}W (read {Stapm}W / {Fast}W).",
                mode.Active, want.StapmW, want.FastW, observed.StapmW, observed.PptW);
        else
            logger?.LogInformation(
                "TDP moved since it was written: wanted STAPM {Want}W / fast {WantFast}W (by {Owner}), read {Stapm}W / {Fast}W. Re-applying.",
                want.StapmW, want.FastW, owned.Owner, observed.StapmW, observed.PptW);

        try
        {
            // Conditional on the generation read at the top, checked inside the write gate: the read
            // launched a process, and anything that wrote meanwhile — POST /tdp, a mode switch, a
            // throttle — is newer than `want` and must not be undone by it.
            var result = await tdp.ApplyIfUnchangedAsync(ownership.Generation, want, TdpOwner.Reassert, ct);
            if (result is not TdpApplyResult applied) return ReassertOutcome.Superseded;
            return applied.Verified ? ReassertOutcome.Reasserted : ReassertOutcome.NotHeld;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "TDP reassert: the re-apply failed.");
            return ReassertOutcome.NotHeld;
        }
    }
}
