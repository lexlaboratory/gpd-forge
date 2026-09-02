// GPD Forge — decorators that record every hardware write. GPL-3.0-or-later.
//
// Decorators rather than a call to the log at each write site. Both shapes work on the day they are
// written; only one of them still works after someone adds a seventh caller. This project has twice
// shipped logic that was correct and simply not called from where it mattered — ProfileShaper, and
// the GPU applier that briefly had no caller at all — and "remember to log here" is the same class
// of instruction as "remember to call this". Wrapping the interface makes the recording unavoidable
// for anyone holding it.
//
// The decorators never change behaviour: same return values, same exceptions, same ordering. If one
// of these ever swallows an error to keep the log tidy, it has become a liability rather than a
// record.
using GpdForge.Fan;
using GpdForge.Tdp;

namespace GpdForge.Broker;

/// <summary>Records EC fan writes. Wraps any <see cref="IGpdFanController"/>, real or no-op.</summary>
public sealed class AuditingGpdFanController(
    IGpdFanController inner,
    HardwareAuditLog audit,
    Func<DateTimeOffset>? now = null) : IGpdFanController
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    public bool Available => inner.Available;
    public bool IsManual => inner.IsManual;
    public int? ReadDuty() => inner.ReadDuty();   // a read is not a write; nothing to record

    public bool SetManualDuty(int duty0to255)
    {
        var ok = inner.SetManualDuty(duty0to255);
        // SetManualDuty's contract is that true means read-back verified, so the result maps
        // directly onto Verified rather than being an "we tried" flag.
        audit.Record("fan", "SetManualDuty", $"duty={duty0to255} (0..255 user scale)", ok, _now());
        return ok;
    }

    public void SetAuto()
    {
        inner.SetAuto();
        // Verified is null, not true: SetAuto returns void and cannot report failure, so claiming a
        // verified write here would be inventing a confirmation nobody gave us.
        audit.Record("fan", "SetAuto", "handed fan control back to firmware", null, _now());
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>Records TDP writes. Wraps any <see cref="ITdpController"/>.</summary>
public sealed class AuditingTdpController(
    ITdpController inner,
    HardwareAuditLog audit,
    TdpState? state = null,
    string backendName = "unknown",
    Func<DateTimeOffset>? now = null) : ITdpController
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    public async Task<TdpApplyResult> ApplyAsync(TdpProfile profile, string owner, CancellationToken ct)
    {
        var result = await inner.ApplyAsync(profile, owner, ct);
        var at = _now();

        // The owner is IN the audit line, not beside it. A log of hardware writes that cannot say
        // which subsystem asked answers "what happened" and not "why", and on a machine sitting at
        // 12 W the second question is the one being asked.
        audit.Record(
            "tdp",
            "Apply",
            $"[{owner}] stapm={profile.StapmW}W fast={profile.FastW}W slow={profile.SlowW}W tctl={profile.TctlC}C "
            + $"-> observed {result.Observed} after {result.Attempts} attempt(s)",
            result.Verified,
            at);

        // Recorded here, in the decorator, for the same reason the audit line is: every caller is
        // covered by construction rather than by remembering.
        state?.Record(new TdpSnapshot(
            profile, result.Observed, result.Verified, result.Attempts, owner, backendName, at));

        return result;
    }
}
