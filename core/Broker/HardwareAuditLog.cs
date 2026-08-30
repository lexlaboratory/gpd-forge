// GPD Forge — a record of every hardware write this daemon performed. GPL-3.0-or-later.
//
// The audit log was specified with the broker in Phase 1 ("an audit log of every write") and never
// built; `IBroker` has carried that sentence in a comment since. It matters more than it sounds.
//
// This daemon writes to the embedded controller and to power limits. When a handheld runs hot, runs
// loud, or comes back from a suspend behaving oddly, the first question is *what touched it* — and
// GPD Forge is not the only candidate: MotionAssistant, GPD Tool and Adrenalin all write to the same
// machine. Without a record, "GPD Forge did nothing" is a claim rather than a fact, and this project
// has already spent a day on a crash that a timestamp would have located in a minute.
//
// Deliberately in memory and bounded. A write log that fills the disk on a device with one SSD would
// be a worse bug than the one it helps diagnose, and durability is not needed for the question it
// answers, which is always about the recent past. Alerts already persist for the things a user must
// see after a reboot.
using System.Collections.Concurrent;

namespace GpdForge.Broker;

/// <summary>
/// One hardware write. <paramref name="Verified"/> is null when the write path could not read the
/// value back — which is a different fact from a failed verify, and the difference is exactly what
/// makes this log worth keeping.
/// </summary>
public sealed record HardwareWrite(
    DateTimeOffset AtUtc,
    string Subsystem,
    string Operation,
    string Detail,
    bool? Verified);

public sealed class HardwareAuditLog
{
    /// <summary>Kept small on purpose: this answers "what happened just now", and the alert store is
    /// where anything a user must see later already lives.</summary>
    public const int Capacity = 500;

    private readonly ConcurrentQueue<HardwareWrite> _writes = new();

    public void Record(string subsystem, string operation, string detail, bool? verified, DateTimeOffset atUtc)
    {
        _writes.Enqueue(new HardwareWrite(atUtc, subsystem, operation, detail, verified));

        // Trim after enqueueing rather than before, so a burst never drops the write it is recording.
        while (_writes.Count > Capacity && _writes.TryDequeue(out _)) { }
    }

    /// <summary>Newest first, because the question is nearly always about what happened last.</summary>
    public IReadOnlyList<HardwareWrite> Recent(int limit = 100)
        => _writes.Reverse().Take(Math.Clamp(limit, 1, Capacity)).ToArray();

    /// <summary>
    /// A one-line summary for the health page. Counts unverified writes separately from failed ones:
    /// "we could not confirm 12 writes" and "12 writes were rejected" call for different reactions.
    /// </summary>
    public (int Total, int Failed, int Unconfirmed) Tally()
    {
        var all = _writes.ToArray();
        return (all.Length, all.Count(w => w.Verified == false), all.Count(w => w.Verified is null));
    }
}
