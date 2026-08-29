// GPD Forge — detecting that the machine just came back from a suspend. GPL-3.0-or-later.
//
// StandbyService.RestoreAsync has existed since the Standby Doctor landed, and until now the only
// thing that ever called it was a human pressing a button. That is the wrong shape: the EC comes
// back from suspend uninitialized whether or not anybody is looking at the panel, and the failure it
// produces — hot and silent, because power limits were re-applied against an uninitialized EC — is
// exactly the one you are not awake to press a button for.
//
// The detection reuses the trick StandbyDrain.cs already proves out: QueryUnbiasedInterruptTime does
// not advance while the system is suspended, so wall-clock delta minus unbiased delta is time spent
// asleep. What is deliberately NOT reused is StandbyDrainTracker itself. Its gates are correct for a
// drain figure and wrong for a restore — it ignores anything under 15 minutes, anything on the
// charger, and anything where the battery did not drop, and a resume needs the hardware back in all
// three of those cases. Sharing the type would have meant a restore that quietly skips short sleeps
// and every resume on mains.
//
// Pure and injectable, so a suspend is testable without suspending the machine.
namespace GpdForge.Standby;

public sealed class ResumeDetector
{
    /// <summary>
    /// <paramref name="MinSleep"/> is a floor, not a filter on interest: Modern Standby dips in and
    /// out for seconds at a time, and re-initialising the EC on each of those would be a write storm
    /// against the hardware for no gain. A minute is comfortably above the blips and far below any
    /// sleep long enough for the EC to come back cold.
    /// </summary>
    public sealed record Options(TimeSpan MinSleep);

    private static readonly Options Defaults = new(TimeSpan.FromSeconds(60));

    private readonly Options _opt;
    private DateTimeOffset? _previousAt;
    private TimeSpan _previousUnbiased;

    public ResumeDetector(Options? options = null) => _opt = options ?? Defaults;

    /// <summary>
    /// Records one observation of both clocks. Returns how long the machine was suspended when this
    /// observation closes a suspend, and null in every other case — including the first observation,
    /// ordinary awake polling, sleeps below the floor, and either clock stepping backwards.
    /// </summary>
    public TimeSpan? Observe(DateTimeOffset at, TimeSpan unbiased)
    {
        var previousAt = _previousAt;
        var previousUnbiased = _previousUnbiased;
        _previousAt = at;
        _previousUnbiased = unbiased;

        if (previousAt is null) return null;

        var wall = at - previousAt.Value;
        var awake = unbiased - previousUnbiased;

        // Neither clock can legitimately run backwards. A wall clock that does has been stepped
        // (NTP, a timezone write, a VM restore) and a backwards unbiased clock is not a clock at
        // all; reading either as sleep would invent a suspend that never happened.
        if (wall <= TimeSpan.Zero || awake < TimeSpan.Zero) return null;

        var slept = wall - awake;
        return slept >= _opt.MinSleep ? slept : null;
    }
}
