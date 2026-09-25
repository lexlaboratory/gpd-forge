// GPD Forge — PresentMon lines in, the target's frames out. GPL-3.0-or-later.
//
// Pure glue between the parser, the row clock, the frame buffer and the target choice, so the whole
// path from "a line of CSV" to "the game's FPS and frame times" runs in tests from fixtures, with no
// process and no real clock. PresentMonFrameRateProbe only adds the process and the clock.
//
// Threading: Accept runs on the probe's stdout pump; the reads run on the telemetry sampler (and the
// session recorder in the same tick). The header, the row clock and the previous target are touched
// under their own lock; the buffer locks itself.
namespace GpdForge.Telemetry;

public sealed class PresentMonFeed
{
    /// <summary>
    /// FPS and the 1% low are over the last 2 s: long enough for a stable mean, short enough that the
    /// reading tracks the game rather than lagging behind it (unchanged from before the buffer).
    /// </summary>
    public static readonly TimeSpan FpsSpan = TimeSpan.FromSeconds(2);

    // Every app on the machine lands in one buffer, so capacity is sized for all of them over 10 s:
    // a 240 fps game plus a 144 Hz compositor plus a few browsers is well under 8 000 rows. 32 768
    // frames is ~1.3 MB, and it only binds if something presents absurdly fast.
    private const int Capacity = 32_768;

    private readonly FrameWindow _frames;
    private readonly FrameClock _clock = new();
    private readonly Lock _gate = new();
    private PresentMonColumns _columns;
    private string? _previousTarget;

    public PresentMonFeed() : this(IFrameTimeSource.Span) { }

    public PresentMonFeed(TimeSpan bufferSpan)
    {
        if (bufferSpan < FpsSpan) throw new ArgumentOutOfRangeException(nameof(bufferSpan), "The buffer must cover at least the FPS span.");
        _frames = new FrameWindow(bufferSpan, Capacity);
    }

    /// <summary>
    /// One line of PresentMon stdout, as it reached us at <paramref name="arrival"/> (a monotonic
    /// time). Headers (re)define the columns and start a new capture timeline; anything before the
    /// first header, and any row that cannot yield an honest frame interval, is ignored.
    /// </summary>
    public void Accept(string line, DateTimeOffset arrival)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        PresentMonRow row;
        DateTimeOffset at;
        lock (_gate)
        {
            // The header arrives again whenever a capture (re)starts, so keep re-resolving it rather
            // than assuming the first one holds forever — and restart the row clock with it.
            if (PresentMonCsv.TryParseHeader(line, out var parsed))
            {
                _columns = parsed;
                _clock.Reset();
                return;
            }
            if (!PresentMonCsv.TryParseRow(line, _columns, out row)) return;
            at = _clock.Stamp(row.TimeMs, arrival);
        }
        _frames.Add(row.Application, row.FrameTimeMs, at);
    }

    /// <summary>FPS and 1% low of the target over the last <see cref="FpsSpan"/>.</summary>
    public bool TryRead(DateTimeOffset now, string? foreground, Func<string, bool> isRuleMatched, out FpsSample sample)
    {
        sample = default;
        var target = ResolveTarget(now, foreground, isRuleMatched);
        return target is not null && _frames.TryAggregate(now, target, FpsSpan, out sample);
    }

    /// <summary>The target's frame times over the whole buffer (10 s by default), oldest first.</summary>
    public bool TryGetFrameTimes(DateTimeOffset now, string? foreground, Func<string, bool> isRuleMatched, out FrameTimeSeries series)
    {
        series = null!;
        var target = ResolveTarget(now, foreground, isRuleMatched);
        if (target is null) return false;

        var times = _frames.FrameTimes(now, target, _frames.Window);
        if (times.Length == 0) return false;
        series = new FrameTimeSeries(target, Array.AsReadOnly(times));
        return true;
    }

    // "Presenting" means a frame inside the FPS span: an app that last presented 9 s ago is in the
    // buffer but no longer on screen, and must not keep or take the reading.
    private string? ResolveTarget(DateTimeOffset now, string? foreground, Func<string, bool> isRuleMatched)
    {
        ArgumentNullException.ThrowIfNull(isRuleMatched);
        var presenting = _frames.Activity(now, FpsSpan);
        lock (_gate)
        {
            var target = FrameTarget.Choose(presenting, foreground, isRuleMatched, _previousTarget);
            // Remembered only when there is one: a moment with nothing presenting (a loading screen
            // that stops presenting) must not make the next overlay-in-front read lose the game.
            if (target is not null) _previousTarget = target;
            return target;
        }
    }
}
