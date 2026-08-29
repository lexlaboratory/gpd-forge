// GPD Forge — accumulator for one in-flight session. GPL-3.0-or-later.
//
// Holds only what a session needs while it is open: the per-second series it will aggregate at close
// time, plus a handful of running extremes. Not thread-safe on its own — SessionTracker owns the lock.

namespace GpdForge.Sessions;

internal sealed class SessionBuilder
{
    private readonly List<double> _fps = [];
    private readonly List<double> _lows = [];
    private readonly List<double> _temps = [];
    private readonly List<double> _watts = [];

    private double? _tempMax;
    private double? _fpsMax;
    private int? _batteryFirst;
    private int? _batteryLast;
    private int _samples;
    private int _samplesWithoutFps;
    private bool _sawAc;

    public SessionBuilder(string app, SessionTick first)
    {
        App = app;
        StartedAt = first.At;
        LastFrameAt = first.At;
        Add(first, countsAsFrame: first.Fps is > 0);
    }

    public string App { get; }
    public DateTimeOffset StartedAt { get; }

    /// <summary>Timestamp of the most recent tick that actually carried a frame-rate reading — the
    /// idle timeout is measured from here, and it becomes the session's end time.</summary>
    public DateTimeOffset LastFrameAt { get; private set; }

    public void Add(SessionTick tick, bool countsAsFrame)
    {
        _samples++;

        if (countsAsFrame && tick.Fps is double fps)
        {
            LastFrameAt = tick.At;
            _fps.Add(fps);
            if (_fpsMax is null || fps > _fpsMax) _fpsMax = fps;
            // The 1% low is optional even when the mean is present: the probe reports it only once a
            // window holds enough frames to mean anything.
            if (tick.Fps1PctLow is double low) _lows.Add(low);
        }
        else
        {
            _samplesWithoutFps++;
        }

        if (tick.CpuTempC is double temp)
        {
            _temps.Add(temp);
            if (_tempMax is null || temp > _tempMax) _tempMax = temp;
        }
        if (tick.PackageW is double watts) _watts.Add(watts);

        if (tick.AcConnected) _sawAc = true;
        if (tick.BatteryPct is int pct)
        {
            _batteryFirst ??= pct;
            _batteryLast = pct;
        }
    }

    public GameSession Build(int trendPoints)
    {
        bool onBattery = !_sawAc;
        // A drain figure only means something if the charger never intervened AND the battery gauge
        // actually moved downwards. A charge mid-session, or a gauge that ticked up, yields none.
        int? used = onBattery && _batteryFirst is int start && _batteryLast is int end && start >= end
            ? start - end
            : null;

        // Clamped because a backwards clock jump (NTP correction, resume from Modern Standby) must
        // never produce a negative duration.
        double seconds = Math.Max(0, (LastFrameAt - StartedAt).TotalSeconds);

        return new GameSession(
            Id: Guid.NewGuid(),
            App: App,
            StartedUtc: StartedAt,
            EndedUtc: LastFrameAt,
            DurationSeconds: SessionMath.Round(seconds),
            Samples: _samples,
            SamplesWithoutFps: _samplesWithoutFps,
            FpsAvg: SessionMath.MeanOrNull(_fps),
            Fps1PctLow: SessionMath.OnePercentLow(_lows),
            FpsMax: _fpsMax,
            CpuTempAvgC: SessionMath.MeanOrNull(_temps),
            CpuTempMaxC: _tempMax,
            PackageAvgW: SessionMath.MeanOrNull(_watts),
            OnBattery: onBattery,
            BatteryStartPct: onBattery ? _batteryFirst : null,
            BatteryEndPct: onBattery ? _batteryLast : null,
            BatteryUsedPct: used,
            FpsTrend: SessionMath.Downsample(_fps, trendPoints));
    }
}
