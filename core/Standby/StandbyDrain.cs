// GPD Forge — measured Modern Standby battery drain. GPL-3.0-or-later.
//
// The one number a standby panel is asked for ("how much did it lose overnight?") is also the one
// that is trivial to fake. This measures it or reports nothing: a drain figure only ever comes from
// two battery readings this daemon actually took, separated by a suspend it actually observed.
//
// Detecting the suspend is the whole trick. Wall-clock time alone cannot tell "the box slept for
// 8 h" from "our sampler was starved for 8 h", and on Modern Standby (S0ix) the tick count keeps
// advancing, so Environment.TickCount64 is no help either. QueryUnbiasedInterruptTime does NOT
// advance while the system is in any sleep state, so (wall delta - unbiased delta) is the time
// genuinely spent suspended. Everything below is pure and injectable so it is testable without
// suspending the machine.
using System.Runtime.InteropServices;

namespace GpdForge.Standby;

/// <summary>Sleep-excluding monotonic time. Null when the platform will not answer.</summary>
public interface IUnbiasedClock
{
    TimeSpan? Read();
}

/// <summary>Real clock: <c>QueryUnbiasedInterruptTime</c> (100 ns units), which stops during sleep.</summary>
public sealed partial class Win32UnbiasedClock : IUnbiasedClock
{
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    public TimeSpan? Read()
    {
        try
        {
            return QueryUnbiasedInterruptTime(out ulong ticks) ? TimeSpan.FromTicks((long)ticks) : null;
        }
        catch (Exception) { return null; }   // no fallback: a guessed clock would fabricate drain
    }
}

/// <summary>One battery reading, stamped with both clocks.</summary>
public sealed record StandbySample(DateTimeOffset At, TimeSpan Unbiased, int BatteryPct, bool AcConnected);

/// <summary>A drain figure this daemon measured, with everything needed to justify it.</summary>
public sealed record DrainMeasurement(
    double PctPerHour, double SleptHours, int FromPct, int ToPct, DateTimeOffset At);

/// <summary>
/// Turns a stream of battery samples into at most one drain measurement per observed suspend.
/// Pure: no clock, no WMI, no I/O — the caller supplies both timestamps.
/// </summary>
public sealed class StandbyDrainTracker
{
    /// <summary>
    /// <paramref name="MinSleep"/> keeps a short screen-off blip from being extrapolated into a
    /// %/h figure. <paramref name="AwakeTolerance"/> bounds how much of the gap may have been spent
    /// awake: the drop is divided by the slept hours, so a gap that was largely awake would charge
    /// active use to standby.
    /// </summary>
    public sealed record Options(TimeSpan MinSleep, TimeSpan AwakeTolerance);

    private static readonly Options Defaults = new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5));

    private readonly Options _opt;
    private StandbySample? _previous;

    public StandbyDrainTracker(Options? options = null) => _opt = options ?? Defaults;

    /// <summary>The most recent measurement, or null while none has ever been made.</summary>
    public DrainMeasurement? Last { get; private set; }

    /// <summary>
    /// Records a sample and returns a measurement when this sample closes an observed suspend.
    /// Returns null — never a plausible-looking number — in every other case.
    /// </summary>
    public DrainMeasurement? Observe(DateTimeOffset at, TimeSpan unbiased, int batteryPct, bool acConnected)
    {
        // 0 is what the WMI battery read reports when it fails, so it cannot be trusted as a level;
        // an unusable reading is discarded outright rather than becoming a false baseline.
        if (batteryPct is <= 0 or > 100) return null;

        var previous = _previous;
        _previous = new StandbySample(at, unbiased, batteryPct, acConnected);
        if (previous is null) return null;

        var wall = at - previous.At;
        var awake = unbiased - previous.Unbiased;
        if (wall <= TimeSpan.Zero || awake < TimeSpan.Zero) return null;   // clock stepped backwards

        var slept = wall - awake;
        if (slept < _opt.MinSleep || awake > _opt.AwakeTolerance) return null;

        // A charger anywhere in the window makes the delta say nothing about standby drain.
        if (acConnected || previous.AcConnected) return null;

        int drop = previous.BatteryPct - batteryPct;
        if (drop < 0) return null;   // it gained charge: not a drain

        var measurement = new DrainMeasurement(
            Math.Round(drop / slept.TotalHours, 2),
            Math.Round(slept.TotalHours, 2),
            previous.BatteryPct,
            batteryPct,
            at);
        Last = measurement;
        return measurement;
    }
}
