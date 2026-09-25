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
//   - a write that lands while the read is in flight wins: the re-apply is dropped rather than
//     undoing it.
//
// "What GPD Forge last wrote" is TdpState.Last — whoever wrote it: the mode, a manual override, a
// guardian ceiling, auto-FPS. Re-asserting any other profile (the preset, say) would silently undo
// the owner that is actually in charge. The re-apply is recorded under its own owner,
// TdpOwner.Reassert, so GET /tdp and the audit log say that it happened and why.
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
    /// <summary>Another write landed during the read; the stale re-apply was dropped.</summary>
    Superseded,
    /// <summary>The limit had moved; it was re-applied and read back as holding.</summary>
    Reasserted,
    /// <summary>The limit had moved; it was re-applied and the firmware still did not take it.</summary>
    NotHeld,
}

public sealed class TdpReasserter(
    ITdpBackend backend,
    ITdpController tdp,
    TdpState state,
    IPowerControllerDetector detector,
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
    /// so a wall-clock change cannot stall or burst it. Called from ForgeWorker's tick, which is
    /// also where most other TDP writes happen — so the check never runs concurrently with them.
    /// </summary>
    public async Task<ReassertOutcome?> ReassertIfDueAsync(CancellationToken ct)
    {
        if (_time.GetElapsedTime(_lastCheck) < DefaultInterval) return null;
        _lastCheck = _time.GetTimestamp();
        return await ReassertAsync(ct);
    }

    /// <summary>One check: read back, compare with the last write, re-apply only on a difference.</summary>
    public async Task<ReassertOutcome> ReassertAsync(CancellationToken ct)
    {
        if (state.Last is not TdpSnapshot owned) return ReassertOutcome.NothingOwned;

        if (detector.OthersRunning(out var rivals))
        {
            logger?.LogDebug("TDP reassert skipped: another power controller is active ({Names}).",
                string.Join(", ", rivals));
            return ReassertOutcome.Yielded;
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

        if (ClosedLoopTdpController.Holds(observed, owned.Requested, ToleranceW)) return ReassertOutcome.Holding;

        // The read launched a process. If anything wrote TDP meanwhile, `owned` is stale and writing it
        // would undo that newer write — POST /tdp, a mode switch, a throttle.
        if (!Equals(state.Last, owned)) return ReassertOutcome.Superseded;

        logger?.LogInformation(
            "TDP moved since it was written: wanted STAPM {Want}W / fast {WantFast}W (by {Owner}), read {Stapm}W / {Fast}W. Re-applying.",
            owned.Requested.StapmW, owned.Requested.FastW, owned.Owner, observed.StapmW, observed.PptW);

        try
        {
            var result = await tdp.ApplyAsync(owned.Requested, TdpOwner.Reassert, ct);
            return result.Verified ? ReassertOutcome.Reasserted : ReassertOutcome.NotHeld;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "TDP reassert: the re-apply failed.");
            return ReassertOutcome.NotHeld;
        }
    }
}
