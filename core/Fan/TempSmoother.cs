// GPD Forge — a short moving average for the fan curve's temperature input. GPL-3.0-or-later.
//
// RAPL package power (and the CPU temp that tracks it) is read once per second with no windowing
// of its own, so a light, bursty workload produces genuine second-to-second swings of ten-plus
// degrees that are sampling noise, not a thermal trend. FanCurve.DutyForTemp deliberately never
// delays a RISE (see FanCurve.cs) — that is a safety choice and stays untouched here. Feeding it
// raw per-tick noise, though, turns every noise spike into an audible duty jump, which is the
// "revs up and down constantly" complaint this smooths away.
//
// This smooths only the READING handed to the curve, not the curve's rise/hold decision: a
// sustained real temperature increase still shows up in the average within a few ticks, well
// inside the thermal guardian's own margin (GuardianEvaluator reacts to the raw instantaneous
// reading and never sees this average at all).

namespace GpdForge.Fan;

/// <summary>Fixed-size moving average over the most recent readings.</summary>
public sealed class TempSmoother(int window = TempSmoother.DefaultWindowSize)
{
    public const int DefaultWindowSize = 4;

    private readonly int _window = Math.Max(1, window);
    private readonly Queue<double> _samples = new();

    /// <summary>Adds a reading and returns the average of the last <c>window</c> readings — or
    /// fewer while the window is still filling, so a cold start responds to what it has rather
    /// than waiting for a full window of history that does not exist yet.</summary>
    public double Add(double tempC)
    {
        _samples.Enqueue(tempC);
        while (_samples.Count > _window) _samples.Dequeue();
        return _samples.Average();
    }

    /// <summary>Drops all history. Call whenever the reading goes untrustworthy or fan control
    /// hands back to firmware, so a stale average never leaks into the next curve-mode session.</summary>
    public void Reset() => _samples.Clear();
}
