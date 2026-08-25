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

    public async Task<TdpApplyResult> ApplyAsync(TdpProfile profile, CancellationToken ct)
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

    private static bool Holds(TdpReadout observed, TdpProfile want, int tol) =>
        Math.Abs(observed.StapmW - want.StapmW) <= tol && Math.Abs(observed.PptW - want.FastW) <= tol;

    private TimeSpan Backoff(int attempt)
    {
        long ms = Math.Min((long)_opt.SettleMs << attempt, _opt.MaxBackoffMs);
        return TimeSpan.FromMilliseconds(ms);
    }
}
