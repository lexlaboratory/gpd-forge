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
    private readonly List<double> _lows01 = [];
    private readonly List<double> _stutterRates = [];
    private readonly Dictionary<string, int> _modes = new(StringComparer.OrdinalIgnoreCase);
    // Keyed by cap, with 0 standing for "uncapped": an uncapped stretch competes like any cap.
    private readonly Dictionary<int, int> _caps = [];

    // Energy, integrated tick by tick (plan F2). Two integrals because which one the session reports is
    // only known at close time (whether it stayed on battery throughout).
    private double _packageWh, _dischargeWh;
    private bool _sawPackage, _sawDischarge;
    private DateTimeOffset? _lastTickAt;

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
        if (tick.Fps01PctLow is double low01) _lows01.Add(low01);
        if (tick.StuttersPerMin is double rate) _stutterRates.Add(rate);
        if (tick.Mode is string mode) _modes[mode] = _modes.GetValueOrDefault(mode) + 1;
        _caps[tick.FrameCapFps ?? 0] = _caps.GetValueOrDefault(tick.FrameCapFps ?? 0) + 1;
        Integrate(tick);

        if (tick.AcConnected) _sawAc = true;
        if (tick.BatteryPct is int pct)
        {
            _batteryFirst ??= pct;
            _batteryLast = pct;
        }
    }

    /// <summary>Energy is the tick's power over the time since the previous tick, with that step capped
    /// at <see cref="MaxEnergyStep"/>: a sleep/resume or a stalled sampler leaves a gap, and billing the
    /// whole gap at the last reading would invent energy nobody spent.</summary>
    private static readonly TimeSpan MaxEnergyStep = TimeSpan.FromSeconds(5);

    private void Integrate(SessionTick tick)
    {
        var previous = _lastTickAt;
        _lastTickAt = tick.At;
        if (previous is not DateTimeOffset prev || tick.At <= prev) return;
        var step = tick.At - prev;
        double hours = (step < MaxEnergyStep ? step : MaxEnergyStep).TotalHours;
        if (tick.PackageW is double package) { _packageWh += package * hours; _sawPackage = true; }
        if (tick.DischargeW is double drain) { _dischargeWh += drain * hours; _sawDischarge = true; }
    }

    public GameSession Build(int trendPoints)
    {
        bool onBattery = !_sawAc;
        // A drain figure only means something if the charger never intervened AND the battery gauge
        // actually moved downwards. A charge mid-session, or a gauge that ticked up, yields none.
        int? used = onBattery && _batteryFirst is int start && _batteryLast is int end && start >= end
            ? start - end
            : null;

        // The battery drain is what the battery paid for the whole machine, so it is the better figure —
        // but only when no charger was ever paying part of it. Otherwise the APU's package power.
        (double? energyWh, string? energySource) = onBattery && _sawDischarge
            ? (Math.Round(_dischargeWh, 2), "battery")
            : _sawPackage ? (Math.Round(_packageWh, 2), "package") : ((double?)null, (string?)null);
        int topCap = _caps.Count == 0 ? 0 : _caps.MaxBy(kv => kv.Value).Key;

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
            FpsTrend: SessionMath.Downsample(_fps, trendPoints),
            Fps01PctLow: SessionMath.OnePercentLow(_lows01),
            StuttersPerMin: SessionMath.MeanOrNull(_stutterRates),
            EnergyWh: energyWh,
            EnergySource: energySource,
            Mode: _modes.Count == 0 ? null : _modes.MaxBy(kv => kv.Value).Key,
            FrameCapFps: topCap > 0 ? topCap : null);
    }
}
