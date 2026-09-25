// GPD Forge — places PresentMon rows on the daemon's clock by their own time. GPL-3.0-or-later.
//
// PresentMon's stdout reaches us in bursts: the pipe is buffered, and the ETW consumer itself batches.
// Stamping a frame with the moment its line arrived turned a quiet second followed by a flush into
// "no frames, then hundreds at once" — and a 10 s frame buffer built that way holds whatever arrived
// in the last 10 s, not what was presented in them.
//
// Each row carries its own capture-relative time. What is unknown is the offset between that
// timeline and ours. Every row bounds it from one side — a frame cannot arrive before it happened, so
// offset <= arrival - rowTime — and the tightest bound seen so far is the least-delayed row's. Taking
// the minimum (the classic one-way clock-sync estimate) converges on the true offset plus the
// pipeline's smallest delay, never stamps a frame after its own arrival, and does not care about
// row order. The arrival clock is monotonic (see PresentMonFrameRateProbe), so there is no drift or
// wall-clock step for the minimum to latch onto.
namespace GpdForge.Telemetry;

public sealed class FrameClock
{
    private DateTimeOffset? _offset; // our time at capture time 0

    /// <summary>
    /// Our time for a row whose capture-relative time is <paramref name="rowTimeMs"/>, which reached
    /// us at <paramref name="arrival"/>. A row with no time is stamped on arrival — its interval is
    /// still honest, only its position is unknown.
    /// </summary>
    public DateTimeOffset Stamp(double? rowTimeMs, DateTimeOffset arrival)
    {
        if (rowTimeMs is not double ms || !double.IsFinite(ms)) return arrival;

        var bound = arrival - TimeSpan.FromMilliseconds(ms);
        if (_offset is null || bound < _offset) _offset = bound;
        return _offset.Value + TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>A new capture restarts its times at zero; the old offset no longer applies.</summary>
    public void Reset() => _offset = null;
}
