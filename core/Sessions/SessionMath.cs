// GPD Forge — pure aggregation helpers for play sessions. GPL-3.0-or-later.
//
// No clock, no I/O, no state: everything here is a function of its arguments, so the numbers the UI
// shows are the numbers the tests pin down.

namespace GpdForge.Sessions;

public static class SessionMath
{
    /// <summary>Mean of a series, or null when the series is empty. Never returns 0 for "no data" —
    /// a zero average frame rate and an unmeasured one are completely different statements.</summary>
    public static double? MeanOrNull(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return null;
        double sum = 0;
        foreach (var v in values) sum += v;
        return Round(sum / values.Count);
    }

    /// <summary>
    /// The session's 1% low, in FPS: the mean of the worst 1% (at least one) of the per-window 1%-low
    /// readings collected during the session. This is a percentile of percentiles, not of raw frames —
    /// the raw frame times only ever exist inside the probe's two-second window and are never
    /// retained — so it is deliberately the conservative reading of the stutter the session actually
    /// contained, not a re-derivation of it.
    /// </summary>
    public static double? OnePercentLow(IReadOnlyList<double> perWindowLows)
    {
        ArgumentNullException.ThrowIfNull(perWindowLows);
        if (perWindowLows.Count == 0) return null;
        var sorted = perWindowLows.OrderBy(x => x).ToArray(); // worst (lowest FPS) first
        int take = Math.Max(1, sorted.Length / 100);
        return Round(sorted.Take(take).Average());
    }

    /// <summary>
    /// Reduces a series to at most <paramref name="points"/> values by averaging equal-width buckets,
    /// preserving order. Averaging rather than picking every Nth sample keeps a spike from vanishing
    /// between the samples that survive.
    /// </summary>
    public static IReadOnlyList<double> Downsample(IReadOnlyList<double> values, int points)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThan(points, 1);
        if (values.Count <= points) return values.Select(Round).ToArray();

        var result = new double[points];
        for (int i = 0; i < points; i++)
        {
            int start = (int)((long)i * values.Count / points);
            int end = (int)((long)(i + 1) * values.Count / points);
            if (end <= start) end = start + 1;
            double sum = 0;
            for (int j = start; j < end; j++) sum += values[j];
            result[i] = Round(sum / (end - start));
        }
        return result;
    }

    /// <summary>
    /// Rolls sessions up per application, most-played first. Averages are weighted by duration, so a
    /// two-minute run cannot pull the average of a three-hour one around. A game whose sessions never
    /// carried an FPS reading keeps null averages rather than gaining an invented zero.
    /// </summary>
    public static IReadOnlyList<GameSummary> PerGame(IEnumerable<GameSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return sessions
            .GroupBy(s => s.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GameSummary(
                App: g.First().App,
                Sessions: g.Count(),
                TotalSeconds: Round(g.Sum(s => s.DurationSeconds)),
                LastPlayedUtc: g.Max(s => s.StartedUtc),
                FpsAvg: Weighted(g, s => s.FpsAvg),
                FpsBest: MaxOrNull(g, s => s.FpsMax ?? s.FpsAvg),
                Fps1PctLow: Weighted(g, s => s.Fps1PctLow),
                CpuTempMaxC: MaxOrNull(g, s => s.CpuTempMaxC),
                PackageAvgW: Weighted(g, s => s.PackageAvgW),
                WhPerHour: EnergyRate(g).WhPerHour,
                EnergySource: EnergyRate(g).Source,
                Modes: CompareModes(g)))
            .OrderByDescending(x => x.TotalSeconds)
            .ThenByDescending(x => x.LastPlayedUtc)
            .ToArray();
    }

    /// <summary>The modes the plan F6 A/B compares: the full-power preset and its battery twin.</summary>
    public static IReadOnlyList<string> ComparedModes { get; } =
        [GpdForge.Profiles.ModeCatalogue.Gaming, GpdForge.Profiles.ModeCatalogue.GamingBattery];

    /// <summary>Battery play the per-game figures need before they speak: a few minutes average out the
    /// menus and loading screens whose draw is nothing like the game's.</summary>
    public const double MinBatteryEvidenceSeconds = 300;

    private const string BatterySource = "battery";
    private const string PackageSource = "package";

    /// <summary>
    /// Energy per hour of play (= average watts) across <paramref name="sessions"/>: total Wh over total
    /// hours, so a long session counts for what it is. Sessions that ran on battery are used when there
    /// are any, because the whole-system drain is what empties the battery and the package alone leaves
    /// out the screen, RAM and board; else the package energy. Null when no session measured either.
    /// </summary>
    public static (double? WhPerHour, string? Source) EnergyRate(IEnumerable<GameSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var battery = Measured(sessions, BatterySource);
        var basis = battery.Length > 0 ? battery : Measured(sessions, PackageSource);
        return basis.Length == 0 ? (null, null) : (Rate(basis), basis[0].EnergySource);
    }

    /// <summary>
    /// The gaming vs gaming-battery A/B for one game's sessions (plan F6): FPS, 1 % low, watts and FPS
    /// per watt for each compared mode it was played in. The watts are the battery drain only when every
    /// side has battery sessions; otherwise every side uses its package power (which every session reads,
    /// plugged in or not), so the comparison is never drain against package.
    /// </summary>
    public static IReadOnlyList<ModeStats> CompareModes(IEnumerable<GameSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var all = sessions.ToArray();
        var byMode = ComparedModes
            .Select(m => (Mode: m, Sessions: all.Where(s => string.Equals(s.Mode, m, StringComparison.OrdinalIgnoreCase)).ToArray()))
            .Where(x => x.Sessions.Length > 0)
            .ToArray();
        bool onBattery = byMode.Length > 0 && byMode.All(x => Measured(x.Sessions, BatterySource).Length > 0);

        return byMode.Select(x =>
        {
            var watts = onBattery ? Rate(Measured(x.Sessions, BatterySource)) : Weighted(x.Sessions, s => s.PackageAvgW);
            var fps = Weighted(x.Sessions, s => s.FpsAvg);
            return new ModeStats(
                Mode: x.Mode,
                Sessions: x.Sessions.Length,
                TotalSeconds: Round(x.Sessions.Sum(s => s.DurationSeconds)),
                FpsAvg: fps,
                Fps1PctLow: Weighted(x.Sessions, s => s.Fps1PctLow),
                WhPerHour: watts,
                EnergySource: watts is null ? null : onBattery ? BatterySource : PackageSource,
                // Two decimals: a tenth of a frame per watt can be the whole difference between two presets.
                FpsPerWatt: fps is double f && watts is > 0 ? Math.Round(f / watts.Value, 2, MidpointRounding.AwayFromZero) : null);
        }).ToArray();
    }

    /// <summary>
    /// What one game drains from the battery per hour (plan F6), for the overlay's "~1 h 20 m in this
    /// game". Only sessions that ran on battery count, since a plugged-in session's package power is not
    /// the drain. Those in <paramref name="mode"/> are used when there are enough of them, because a game
    /// costs different watts in gaming and gaming-battery; else all of the game's battery play, and the
    /// result's Mode is null. Null below <see cref="MinBatteryEvidenceSeconds"/>.
    /// </summary>
    public static (double WhPerHour, string? Mode)? BatteryRate(IEnumerable<GameSession> sessionsOfGame, string? mode)
    {
        ArgumentNullException.ThrowIfNull(sessionsOfGame);
        var battery = Measured(sessionsOfGame, BatterySource);
        var inMode = mode is null ? [] : battery.Where(s => string.Equals(s.Mode, mode, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (Enough(inMode) && Rate(inMode) is double m) return (m, inMode[0].Mode);
        if (Enough(battery) && Rate(battery) is double any) return (any, null);
        return null;

        static bool Enough(GameSession[] s) => s.Sum(x => x.DurationSeconds) >= MinBatteryEvidenceSeconds;
    }

    private static GameSession[] Measured(IEnumerable<GameSession> sessions, string source) =>
        sessions.Where(s => s.EnergySource == source && s.EnergyWh is > 0 && s.DurationSeconds > 0).ToArray();

    private static double? Rate(GameSession[] basis)
    {
        double hours = basis.Sum(s => s.DurationSeconds) / 3600.0;
        return hours > 0 ? Round(basis.Sum(s => s.EnergyWh ?? 0) / hours) : null;
    }

    /// <summary>The rollups that could be games: drops the known non-game presenters the FPS target
    /// already ignores (<see cref="GpdForge.Telemetry.FrameTarget.NonGamePresenters"/> — dwm, browsers,
    /// launchers). F1 audit round 1 (2026-09-25): dwm.exe headed the Games page on the device.</summary>
    public static IReadOnlyList<GameSummary> GamesOnly(IEnumerable<GameSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        return summaries.Where(g => !GpdForge.Telemetry.FrameTarget.IsNonGame(g.App)).ToArray();
    }

    private static double? Weighted(IEnumerable<GameSession> sessions, Func<GameSession, double?> selector)
    {
        double weight = 0, total = 0;
        foreach (var s in sessions)
        {
            if (selector(s) is not double value) continue;
            // A session with no measured duration still carries a reading; weight it as one sample
            // rather than discarding it.
            double w = s.DurationSeconds > 0 ? s.DurationSeconds : 1;
            weight += w;
            total += value * w;
        }
        return weight > 0 ? Round(total / weight) : null;
    }

    private static double? MaxOrNull(IEnumerable<GameSession> sessions, Func<GameSession, double?> selector)
    {
        double? best = null;
        foreach (var s in sessions)
            if (selector(s) is double value && (best is null || value > best)) best = value;
        return best is double b ? Round(b) : null;
    }

    /// <summary>One decimal is the resolution the sensors and the HUD actually have; more would be
    /// noise, and it keeps the persisted JSON small.</summary>
    internal static double Round(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
