// GPD Forge — last observed fan-control result. GPL-3.0-or-later.
namespace GpdForge.Fan;

/// <summary>A consistent snapshot of the most recent fan command and its EC readback.</summary>
public readonly record struct FanControlSnapshot(
    int? RequestedDuty,
    int? ObservedDuty,
    bool? Verified,
    string? Error,
    DateTimeOffset? AtUtc,
    string? Mode);

/// <summary>Thread-safe state shared by the worker and status endpoint.</summary>
public sealed class FanControlState
{
    private readonly Lock _gate = new();
    private FanControlSnapshot _snapshot;

    public FanControlSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public void Record(FanControlSnapshot snapshot)
    {
        lock (_gate) _snapshot = snapshot;
    }
}
