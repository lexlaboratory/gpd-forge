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
namespace GpdForge.Tdp;

public sealed class SerializedTdpController(ITdpController inner, TdpState state) : ITdpController
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<TdpApplyResult> ApplyAsync(TdpProfile profile, string owner, CancellationToken ct) =>
        (await ApplyCoreAsync(profile, owner, expectedGeneration: null, ct))!.Value;

    /// <summary>
    /// Applies only if <see cref="TdpState.Generation"/> still equals <paramref name="generation"/> once
    /// this write holds the gate: no write started or finished, and no mode apply yielded, since the
    /// caller read it. Null when something did — the caller's view is stale and writing it would undo
    /// the newer decision.
    /// </summary>
    public Task<TdpApplyResult?> ApplyIfUnchangedAsync(
        long generation, TdpProfile profile, string owner, CancellationToken ct) =>
        ApplyCoreAsync(profile, owner, generation, ct);

    private async Task<TdpApplyResult?> ApplyCoreAsync(
        TdpProfile profile, string owner, long? expectedGeneration, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (expectedGeneration is long expected && state.Generation != expected) return null;

            state.BeginWrite();
            try { return await inner.ApplyAsync(profile, owner, ct); }
            finally { state.EndWrite(); }
        }
        finally { _gate.Release(); }
    }
}
