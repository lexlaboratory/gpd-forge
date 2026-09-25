// GPD Forge — closed-loop TDP controller. GPL-3.0-or-later.
// The honest replacement for MotionAssistant's blind 30s re-apply: apply → read the PM table back →
// retry with backoff → report verified:false (and let the caller warn) instead of looping forever.
using Microsoft.Extensions.Logging;

namespace GpdForge.Tdp;

public sealed class ClosedLoopTdpController(
    ITdpBackend backend,
    IDelay delay,
    ILogger<ClosedLoopTdpController>? logger = null,
    ClosedLoopTdpController.Options? options = null,
    TdpReadbackRule? rule = null) : ITdpController
{
    public sealed record Options(int MaxAttempts = 4, int ToleranceW = 1, int SettleMs = 250, int MaxBackoffMs = 4000);

    private readonly Options _opt = options ?? new Options();
    // Shared with TdpReasserter in the daemon (a DI singleton), so a row this loop finds never follows
    // a write stops being judged by the reassert too. A private one when constructed bare, as in tests.
    private readonly TdpReadbackRule _rule = rule ?? new TdpReadbackRule();

    public async Task<TdpApplyResult> ApplyAsync(TdpProfile profile, string owner, CancellationToken ct)
    {
        TdpReadout observed = default;

        for (int attempt = 1; attempt <= _opt.MaxAttempts; attempt++)
        {
            await backend.ApplyAsync(profile, ct);
            await delay.WaitAsync(TimeSpan.FromMilliseconds(_opt.SettleMs), ct);
            observed = await backend.ReadAsync(ct);
            _rule.Observe(observed, profile, _opt.ToleranceW);

            if (_rule.Holds(observed, profile, _opt.ToleranceW))
            {
                logger?.LogDebug("TDP verified on attempt {Attempt}: {Observed}", attempt, observed);
                return new TdpApplyResult(profile, observed, true, attempt);
            }

            await delay.WaitAsync(Backoff(attempt), ct);
        }

        // Out of attempts with STAPM and fast holding and only a slow/Tctl row off that has never once
        // followed a write here: that row cannot judge this APU's writes (TdpReadbackRule, audit round 3).
        // Judged again without it rather than reported as a firmware revert — which would fail every
        // later write too and have the 30 s reassert rewrite the limits forever.
        if (_rule.DisownRowsThatNeverTracked(observed, profile, _opt.ToleranceW)
            && _rule.Holds(observed, profile, _opt.ToleranceW))
            return new TdpApplyResult(profile, observed, true, _opt.MaxAttempts);

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
    ///
    /// STAPM and fast are REQUIRED; the slow limit and Tctl are judged only when the table printed
    /// them (audit round 2, 2026-09-24). Before, a firmware that put back only its own slow limit or
    /// Tctl read as holding forever. A missing row stays "not measured" rather than "moved": requiring
    /// it would fail every write on a PM table that does not expose it.
    ///
    /// This static form is the STRICT rule — every printed row judged. The loop itself goes through a
    /// <see cref="TdpReadbackRule"/>, which is the same comparison minus any slow/Tctl row proven not
    /// to follow writes on this APU (audit round 3, 2026-09-24: neither row has been seen on the HX 370).
    /// The reassert (<see cref="TdpReasserter"/>) uses the same rule instance, so a limit the closed
    /// loop calls verified is never one the reassert calls moved.
    /// </summary>
    public static bool Holds(TdpReadout observed, TdpProfile want, int tol) =>
        TdpReadbackRule.Judge(observed, want, tol, judgeSlow: true, judgeTctl: true);

    private TimeSpan Backoff(int attempt)
    {
        long ms = Math.Min((long)_opt.SettleMs << attempt, _opt.MaxBackoffMs);
        return TimeSpan.FromMilliseconds(ms);
    }
}
