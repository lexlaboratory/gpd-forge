// GPD Forge — one TDP write at a time. GPL-3.0-or-later.
//
// Five things write TDP from their own threads: ForgeWorker's tick, POST /tdp, POST /mode (and
// FocusProfileWorker through the same ProfileApplier), POST /panic, and the resume restore. Until
// 2026-09-24 nothing ordered them. Each write is a closed loop — apply, 250 ms settle, readback,
// retries with backoff — so two of them overlapping did not simply "last one wins": each retry
// re-applied its OWN profile, the two loops fought, and whichever finished last stuck, whatever the
// user had asked for most recently.
//
// That became an active mis-write once the 30 s reassert existed. It read the limits, saw the user's
// POST /tdp still in flight, judged "moved", and re-wrote the stale profile over it. Its guard
// compared TdpState.Last, which the auditing decorator records only AFTER a closed loop returns, so a
// write that had started but not finished was invisible to it.
//
// Here every write takes one gate and bumps TdpState's generation when it starts and when it ends.
// ApplyIfUnchangedAsync checks the generation INSIDE the gate, so "nothing has written since I read"
// and "I write" are one step — no write can slip between the check and the apply.
//
// The gate also covers the reassert's READ (audit round 2, 2026-09-24): its `ryzenadj --info` ran
// outside it, so a POST /tdp arriving mid-read launched a second ryzenadj that drove the SMU mailbox
// at the same moment. ryzenadj has no cross-process lock on the MP1 message and argument registers,
// so the two could interleave. AcquireIfUnchangedAsync hands out the gate itself for one
// read-compare-write, and the whole of it is serialized with every other SMU access.
namespace GpdForge.Tdp;

public sealed class SerializedTdpController(ITdpController inner, TdpState state) : ITdpController
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<TdpApplyResult> ApplyAsync(TdpProfile profile, string owner, CancellationToken ct) =>
        (await ApplyCoreAsync(profile, owner, condition: null, ct))!.Value;

    /// <summary>
    /// Applies only if <see cref="TdpState.Generation"/> still equals <paramref name="generation"/> once
    /// this write holds the gate: no write started or finished, and no mode apply yielded, since the
    /// caller read it. Null when something did — the caller's view is stale and writing it would undo
    /// the newer decision.
    /// </summary>
    public Task<TdpApplyResult?> ApplyIfUnchangedAsync(
        long generation, TdpProfile profile, string owner, CancellationToken ct) =>
        ApplyCoreAsync(profile, owner, () => state.Generation == generation, ct);

    /// <summary>
    /// Applies only if <paramref name="stillWanted"/> is still true once this write holds the gate;
    /// null otherwise. For a write whose reason can expire while it queues — POST /tdp's override
    /// belongs to the mode it was set in, and a mode switch that got the gate first ends it (audit
    /// round 2, 2026-09-24). The gate orders writes; only this re-check makes the later one stand down.
    /// </summary>
    public Task<TdpApplyResult?> ApplyIfAsync(
        Func<bool> stillWanted, TdpProfile profile, string owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stillWanted);
        return ApplyCoreAsync(profile, owner, stillWanted, ct);
    }

    /// <summary>
    /// Takes the write gate and keeps it until the returned lease is disposed — but only if
    /// <paramref name="generation"/> is still current once it is held; null otherwise, with the gate
    /// already released. For a read-compare-write that must be one step against every other writer:
    /// nothing else reaches the SMU while the lease is held, and the lease's own write does not take
    /// the gate a second time (it is not re-entrant).
    /// </summary>
    public async Task<Lease?> AcquireIfUnchangedAsync(long generation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        if (state.Generation != generation)
        {
            _gate.Release();
            return null;
        }
        return new Lease(this);
    }

    private async Task<TdpApplyResult?> ApplyCoreAsync(
        TdpProfile profile, string owner, Func<bool>? condition, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (condition is not null && !condition()) return null;
            return await WriteHoldingGateAsync(profile, owner, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<TdpApplyResult> WriteHoldingGateAsync(TdpProfile profile, string owner, CancellationToken ct)
    {
        state.BeginWrite();
        try { return await inner.ApplyAsync(profile, owner, ct); }
        finally { state.EndWrite(); }
    }

    /// <summary>The write gate, held. Dispose releases it; disposing twice is harmless.</summary>
    public sealed class Lease : IDisposable
    {
        private SerializedTdpController? _owner;

        internal Lease(SerializedTdpController owner) => _owner = owner;

        /// <summary>A write made while the lease holds the gate, recorded like any other.</summary>
        public Task<TdpApplyResult> ApplyAsync(TdpProfile profile, string owner, CancellationToken ct) =>
            (_owner ?? throw new ObjectDisposedException(nameof(Lease))).WriteHoldingGateAsync(profile, owner, ct);

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?._gate.Release();
    }
}
