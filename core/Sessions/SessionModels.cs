// GPD Forge — play-session model. GPL-3.0-or-later.
//
// A "session" is one continuous stretch during which a single application presented frames. The only
// trustworthy evidence that a game is running is that it is presenting, and that evidence comes from
// the PresentMon probe (core/Telemetry/PresentMonFrameRateProbe.cs), which also names the presenting
// process. When that probe is absent — the GPDFORGE_ENABLE_FPS gate is closed, PresentMon is not
// installed, or Smart App Control blocked it — there is no evidence, so there are NO sessions. We
// never manufacture one out of "a game was probably running", and we never write a 0 where a reading
// was missing: every unavailable metric here is a null, and the UI says so.
using GpdForge.Telemetry;

namespace GpdForge.Sessions;

/// <summary>One second of observation, already normalized: any reading that is unavailable is null,
/// never zero. Constructed from a telemetry snapshot + the frame probe's sample by <see cref="From"/>.</summary>
public readonly record struct SessionTick(
    DateTimeOffset At,
    string? App,
    double? Fps,
    double? Fps1PctLow,
    double? CpuTempC,
    double? PackageW,
    int? BatteryPct,
    bool AcConnected,
    // Plan F2 (2026-09-25). Optional so every earlier construction keeps its meaning: each is null when
    // it was not measured this tick, exactly like the readings above.
    double? Fps01PctLow = null,
    double? StuttersPerMin = null,
    double? DischargeW = null,
    string? Mode = null,
    int? FrameCapFps = null)
{
    /// <summary>
    /// Normalizes a worker tick. <see cref="TelemetrySnapshot"/> carries 0 for "not available"
    /// (its fields are non-nullable value types), so the zeros are translated back into nulls here —
    /// this is the single place that decision is made, and everything downstream can then trust that
    /// a value present means a value measured. A frame sample with no process name is discarded
    /// entirely: an unattributable frame rate cannot open a session for "unknown".
    /// </summary>
    /// <param name="pacing">The 10 s frame-pacing metrics of the same target (plan F2), when measured.</param>
    /// <param name="mode">The active mode this tick; <paramref name="frameCap"/> the requested driver frame cap.</param>
    public static SessionTick From(in TelemetrySnapshot snapshot, FpsSample? frames, DateTimeOffset at,
        FramePacingMetrics? pacing = null, string? mode = null, int? frameCap = null)
    {
        string? app = null;
        double? fps = null, low = null;
        if (frames is FpsSample s && !string.IsNullOrWhiteSpace(s.Process) && s.Fps > 0)
        {
            app = s.Process.Trim();
            fps = s.Fps;
            low = s.Fps1PctLow > 0 ? s.Fps1PctLow : null;
        }

        return new SessionTick(
            at,
            app,
            fps,
            low,
            snapshot.CpuTempC > 0 ? snapshot.CpuTempC : null,
            snapshot.PackageW > 0 ? snapshot.PackageW : null,
            snapshot.BatteryPct > 0 ? snapshot.BatteryPct : null,
            snapshot.AcConnected,
            // Pacing belongs to the app the FPS reading named; without that reading there is no app
            // to attribute it to.
            Fps01PctLow: app is not null ? pacing?.Fps01PctLow : null,
            StuttersPerMin: app is not null ? pacing?.StuttersPerMin : null,
            // A drain read while plugged in is not the game's cost (the charger is paying), so it is
            // not carried; the energy then falls back to the package power.
            DischargeW: !snapshot.AcConnected && snapshot.DischargeW > 0 ? snapshot.DischargeW : null,
            Mode: string.IsNullOrWhiteSpace(mode) ? null : mode,
            FrameCapFps: frameCap is > 0 ? frameCap : null);
    }
}

/// <summary>
/// A finished play session. Every metric is nullable because every sensor behind it is optional on
/// this hardware; null means "not measured", and is rendered as such rather than as a zero.
/// </summary>
public sealed record GameSession(
    Guid Id,
    string App,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    double DurationSeconds,
    int Samples,
    // Ticks during which the app was presenting but the probe produced no aggregate. Kept so the UI
    // can qualify an average built on partial data instead of implying full coverage.
    int SamplesWithoutFps,
    double? FpsAvg,
    double? Fps1PctLow,
    double? FpsMax,
    double? CpuTempAvgC,
    double? CpuTempMaxC,
    double? PackageAvgW,
    // True only when the session ran entirely on battery. A session that was plugged in for part of
    // its life has no meaningful drain figure, so it reports none.
    bool OnBattery,
    int? BatteryStartPct,
    int? BatteryEndPct,
    int? BatteryUsedPct,
    IReadOnlyList<double> FpsTrend,
    // Plan F2 (2026-09-25), appended with defaults so sessions stored before it still load (as nulls).
    // 0.1 % low: the worst per-window reading, the same conservative rule as Fps1PctLow.
    double? Fps01PctLow = null,
    // Mean of the per-second stutters/min readings (each is already a rate over the last 10 s).
    double? StuttersPerMin = null,
    // Energy the session cost, integrated over its ticks; EnergySource says what was integrated:
    // "battery" (the whole-system drain, only when the session ran entirely on battery) or "package".
    double? EnergyWh = null,
    string? EnergySource = null,
    // The mode and requested frame cap the session spent most of its ticks in.
    string? Mode = null,
    int? FrameCapFps = null);

/// <summary>Per-app rollup across sessions — the "by game" view and the Games page.</summary>
/// <param name="PackageAvgW">Duration-weighted package power (F1, 2026-09-25): what the game costs
/// next to what it delivers, so a per-game TDP can be judged against it. Null when no session read it.</param>
public sealed record GameSummary(
    string App,
    int Sessions,
    double TotalSeconds,
    DateTimeOffset LastPlayedUtc,
    double? FpsAvg,
    double? FpsBest,
    double? Fps1PctLow,
    double? CpuTempMaxC,
    double? PackageAvgW);

/// <summary>
/// The thresholds that decide where one session ends and the next begins. Defaults are tuned for a
/// handheld; each is justified where it is defined.
/// </summary>
public sealed record SessionPolicy
{
    /// <summary>How long the presenting app may go quiet before the session is considered over.
    /// Loading screens, shader compilation, alt-tabbing to the desktop and brief PresentMon dropouts
    /// routinely produce 10-30 s with no presents; splitting a session on those would turn one
    /// evening of play into a dozen fragments. 60 s is comfortably clear of that noise floor and
    /// still files a finished session within a minute of quitting.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Sessions shorter than this are dropped instead of stored. Below a minute you are
    /// looking at a launcher splash, a menu, a video player or a browser tab that presented briefly —
    /// not a play session — and on a handheld those would be the overwhelming majority of rows.</summary>
    public TimeSpan MinDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Samples kept for the per-session trend line. A three-hour session at 1 Hz is ~10 800
    /// readings; storing them all would put megabytes of JSON on a handheld's system drive for a
    /// figure that is 120 px wide. The series is downsampled to this many points at close time.</summary>
    public int TrendPoints { get; init; } = 120;

    public static SessionPolicy Default { get; } = new();
}
