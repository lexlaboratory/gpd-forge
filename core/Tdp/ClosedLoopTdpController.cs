// GPD Forge — closed-loop TDP controller. GPL-3.0-or-later.
// The honest replacement for MotionAssistant's blind 30s re-apply: apply → read the PM table back →
// retry with backoff → report verified:false (and let the caller warn) instead of looping forever.
using Microsoft.Extensions.Logging;

namespace GpdForge.Tdp;

public sealed class ClosedLoopTdpController(
    ITdpBackend backend,
    IDelay delay,
    ILogger<ClosedLoopTdpController>? logger = null,
    ClosedLoopTdpController.Options? options = null) : ITdpController
{
    public sealed record Options(int MaxAttempts = 4, int ToleranceW = 1, int SettleMs = 250, int MaxBackoffMs = 4000);

    private readonly Options _opt = options ?? new Options();

    public async Task<TdpApplyResult> ApplyAsync(TdpProfile profile, string owner, CancellationToken ct)
    {
        TdpReadout observed = default;

        for (int attempt = 1; attempt <= _opt.MaxAttempts; attempt++)
        {
            await backend.ApplyAsync(profile, ct);
            await delay.WaitAsync(TimeSpan.FromMilliseconds(_opt.SettleMs), ct);
            observed = await backend.ReadAsync(ct);

            if (Holds(observed, profile, _opt.ToleranceW))
            {
                logger?.LogDebug("TDP verified on attempt {Attempt}: {Observed}", attempt, observed);
                return new TdpApplyResult(profile, observed, true, attempt);
            }

            await delay.WaitAsync(Backoff(attempt), ct);
        }

        logger?.LogWarning(
            "TDP reverted by firmware after {Attempts} attempts — wanted STAPM {Want}W, observed {Got}W",
            _opt.MaxAttempts, profile.StapmW, observed.StapmW);
        return new TdpApplyResult(profile, observed, false, _opt.MaxAttempts);
    }

    /// <summary>
    /// Whether the readback matches what was asked for.
    ///
    /// An ABSENT reading is not a match, and it is spelled out rather than left to nullable
    /// arithmetic: <c>Math.Abs(null - want)</c> is null, and <c>null &lt;= tol</c> is false — so this
    /// would have kept compiling and quietly returned false. That happens to be the safe direction,
    /// but "we could not read it" and "the firmware refused" are different facts and only one of them
    /// should ever be reported as a reverted write.
    /// </summary>
    private static bool Holds(TdpReadout observed, TdpProfile want, int tol) =>
        observed.StapmW is int stapm && observed.PptW is int ppt
        && Math.Abs(stapm - want.StapmW) <= tol
        && Math.Abs(ppt - want.FastW) <= tol;

    private TimeSpan Backoff(int attempt)
    {
        long ms = Math.Min((long)_opt.SettleMs << attempt, _opt.MaxBackoffMs);
        return TimeSpan.FromMilliseconds(ms);
    }
}
