// GPD Forge — which PM-table rows a readback is judged by. GPL-3.0-or-later.
//
// Audit round 2 (2026-09-24) made "the write holds" cover the slow limit and Tctl as well as STAPM
// and fast, so a firmware that put back only its own slow limit or Tctl no longer read as holding.
// Audit round 3 found the catch: those two rows have never been seen on the HX 370. `ryzenadj --info`
// needs the WinRing0 driver, i.e. elevation, and the one capture attempt was refused before it
// printed a table (core.tests/RyzenAdjFixtures.cs). The live /audit only proves STAPM and PPT FAST.
// If Strix Point prints, say, a fixed firmware Tctl under THM LIMIT CORE, then EVERY write — manual
// ones included — would come back unverified after four retries, and the 30 s reassert would find it
// "moved" and rewrite the limits on every check: the blind re-apply the plan forbids, with extra
// steps.
//
// So a secondary row has to EARN its place in the judgement, on this machine, from evidence:
//
//   - STAPM and fast are always judged — they are proven on the device, and a write without them is
//     not a write;
//   - slow and Tctl start Unknown and are judged (the round-2 rule), so a real revert is still caught;
//   - the first write whose readback shows the row at the value written marks it Tracks: from then on
//     it is judged for the life of the daemon, and a later mismatch is a genuine revert;
//   - a write whose STAPM and fast held on every attempt while an Unknown row never once came back at
//     the value written marks it Untracked, with one warning in the log. It stops being judged: a row
//     that has never followed a write cannot say whether one held.
//
// One instance is shared (DI singleton) by the closed loop and the reassert, so "verified" and
// "holding" are always the same question — the reason Holds became public in round 2.
using Microsoft.Extensions.Logging;

namespace GpdForge.Tdp;

public enum ReadbackRow { Slow, Tctl }

public enum RowTracking { Unknown, Tracks, Untracked }

public sealed class TdpReadbackRule(ILogger<TdpReadbackRule>? logger = null)
{
    private readonly Lock _gate = new();
    private RowTracking _slow;
    private RowTracking _tctl;

    public RowTracking Tracking(ReadbackRow row)
    {
        lock (_gate) return row == ReadbackRow.Slow ? _slow : _tctl;
    }

    /// <summary>
    /// Whether <paramref name="observed"/> matches <paramref name="want"/>: STAPM and fast always and
    /// both required; slow and Tctl whenever the table printed them and they have not been found to
    /// ignore writes on this APU (see the file header). An absent row is "not measured", never "moved".
    /// </summary>
    public bool Holds(TdpReadout observed, TdpProfile want, int tol)
    {
        bool judgeSlow, judgeTctl;
        lock (_gate) { judgeSlow = _slow != RowTracking.Untracked; judgeTctl = _tctl != RowTracking.Untracked; }
        return Judge(observed, want, tol, judgeSlow, judgeTctl);
    }

    /// <summary>The comparison itself, with the secondary rows switched in or out by the caller.</summary>
    public static bool Judge(TdpReadout observed, TdpProfile want, int tol, bool judgeSlow, bool judgeTctl) =>
        RequiredHold(observed, want, tol)
        && (!judgeSlow || observed.PptSlowW is not int slow || Math.Abs(slow - want.SlowW) <= tol)
        && (!judgeTctl || observed.TctlC is not int tctl || Math.Abs(tctl - want.TctlC) <= tol);

    /// <summary>STAPM and fast were read and match. The part every judgement shares.</summary>
    public static bool RequiredHold(TdpReadout observed, TdpProfile want, int tol) =>
        observed.StapmW is int stapm && observed.PptW is int ppt
        && Math.Abs(stapm - want.StapmW) <= tol
        && Math.Abs(ppt - want.FastW) <= tol;

    /// <summary>
    /// Evidence from one readback after a write: a secondary row printed at the value written is
    /// proven to follow writes on this APU. Called by the closed loop on every readback.
    /// </summary>
    public void Observe(TdpReadout observed, TdpProfile want, int tol)
    {
        if (!RequiredHold(observed, want, tol)) return;   // the write itself did not land: no evidence
        lock (_gate)
        {
            if (observed.PptSlowW is int slow && Math.Abs(slow - want.SlowW) <= tol) _slow = RowTracking.Tracks;
            if (observed.TctlC is int tctl && Math.Abs(tctl - want.TctlC) <= tol) _tctl = RowTracking.Tracks;
        }
    }

    /// <summary>
    /// A write ran out of attempts. If STAPM and fast held on the last readback and a row that has
    /// never followed a write is the only thing off, that row is marked Untracked and stops being
    /// judged. Returns true when something was marked, so the caller can judge the readback again.
    /// A row already proven (Tracks) is never demoted: its mismatch is a real revert.
    /// </summary>
    public bool DisownRowsThatNeverTracked(TdpReadout observed, TdpProfile want, int tol)
    {
        if (!RequiredHold(observed, want, tol)) return false;
        bool changed = false;
        lock (_gate)
        {
            if (_slow == RowTracking.Unknown && observed.PptSlowW is int slow && Math.Abs(slow - want.SlowW) > tol)
            {
                _slow = RowTracking.Untracked;
                changed = true;
                logger?.LogWarning(
                    "TDP readback: 'PPT LIMIT SLOW' read {Read} W after {Wanted} W was written, on every attempt, while STAPM and fast held. It has never followed a write on this APU, so it is no longer used to judge whether a write held.",
                    slow, want.SlowW);
            }
            if (_tctl == RowTracking.Unknown && observed.TctlC is int tctl && Math.Abs(tctl - want.TctlC) > tol)
            {
                _tctl = RowTracking.Untracked;
                changed = true;
                logger?.LogWarning(
                    "TDP readback: 'THM LIMIT CORE' read {Read} °C after {Wanted} °C was written, on every attempt, while STAPM and fast held. It has never followed a write on this APU, so it is no longer used to judge whether a write held.",
                    tctl, want.TctlC);
            }
        }
        return changed;
    }
}
