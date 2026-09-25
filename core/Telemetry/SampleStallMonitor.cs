// GPD Forge — noticing that the sampler has stopped publishing. GPL-3.0-or-later.
//
// Audit 2026-09-24. The realistic way the sampler fails is not a throw — WmiTelemetryService swallows
// every WMI and LHM error — but a read that HANGS: an LHM Update() or an EC driver stuck under the read
// lock. TelemetrySampler logs throws only, so a hang logged nothing, and ForgeWorker's wait for the next
// sample simply timed out every 2 s, forever, in silence. Everything that loop does stood still with it:
// the thermal guardian, the charge guard, the AC/battery switch, the TDP reassert, history, sessions.
//
// Pure counting, so the "once per outage" rule is testable without waiting out real timeouts.
namespace GpdForge.Telemetry;

public enum StallTransition
{
    /// <summary>Nothing worth saying.</summary>
    None,
    /// <summary>Enough consecutive waits ended with no new sample: log the outage, once.</summary>
    Stalled,
    /// <summary>A sample arrived after an outage was reported: log the recovery, once.</summary>
    Recovered,
}

public sealed class SampleStallMonitor(int warnAfterMisses = SampleStallMonitor.DefaultWarnAfterMisses)
{
    /// <summary>
    /// Three missed waits. At ForgeWorker's 2 s wait that is 6 s without a sample — long enough that a
    /// single slow read (a ryzenadj apply that overran the tick, a WMI provider restart) is not reported
    /// as an outage, short enough that a hung driver is in the log before anyone wonders why the fan
    /// curve is fine and the guardian is not.
    /// </summary>
    public const int DefaultWarnAfterMisses = 3;

    private readonly int _warnAfter = Math.Max(1, warnAfterMisses);
    private bool _reported;

    /// <summary>Consecutive waits that ended without a new sample.</summary>
    public int Misses { get; private set; }

    /// <summary>A wait ended and the sample had not advanced.</summary>
    public StallTransition Missed()
    {
        Misses++;
        if (_reported || Misses < _warnAfter) return StallTransition.None;
        _reported = true;
        return StallTransition.Stalled;
    }

    /// <summary>A new sample arrived.</summary>
    public StallTransition Advanced()
    {
        bool wasReported = _reported;
        Misses = 0;
        _reported = false;
        return wasReported ? StallTransition.Recovered : StallTransition.None;
    }
}
