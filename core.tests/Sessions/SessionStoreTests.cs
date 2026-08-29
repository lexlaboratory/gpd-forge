// GPD Forge — session persistence + per-game rollup. GPL-3.0-or-later.
using GpdForge.Sessions;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class SessionStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static GameSession Session(string app, DateTimeOffset start, double minutes, double? fps = 60, double? low = 45)
        => new(Guid.NewGuid(), app, start, start.AddMinutes(minutes), minutes * 60, (int)(minutes * 60), 0,
               fps, low, fps, 72, 84, 19, false, null, null, null, [30, 40, 50]);

    [Fact]
    public void Sessions_are_listed_newest_first()
    {
        using var temp = new TempSessionDir();
        var store = new SessionStore(temp.Path);
        store.Add(Session("a.exe", T0, 30));
        store.Add(Session("b.exe", T0.AddHours(2), 30));

        Assert.Equal(new[] { "b.exe", "a.exe" }, store.List().Select(x => x.App));
        Assert.Single(store.List(limit: 1));
    }

    [Fact]
    public void Sessions_survive_a_restart()
    {
        using var temp = new TempSessionDir();
        var added = Session("game.exe", T0, 45);
        new SessionStore(temp.Path).Add(added);

        var reopened = new SessionStore(temp.Path).List();
        Assert.Single(reopened);
        Assert.Equal("game.exe", reopened[0].App);
        Assert.Equal(added.Id, reopened[0].Id);
        Assert.Equal(2700, reopened[0].DurationSeconds);
    }

    [Fact]
    public void Get_returns_one_session_or_null()
    {
        using var temp = new TempSessionDir();
        var store = new SessionStore(temp.Path);
        var one = Session("game.exe", T0, 20);
        store.Add(one);

        Assert.Equal("game.exe", store.Get(one.Id)?.App);
        Assert.Null(store.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Filtering_by_app_is_case_insensitive()
    {
        using var temp = new TempSessionDir();
        var store = new SessionStore(temp.Path);
        store.Add(Session("Game.exe", T0, 10));
        store.Add(Session("other.exe", T0.AddHours(1), 10));

        Assert.Single(store.List(app: "game.exe"));
        Assert.Empty(store.List(app: "missing.exe"));
    }

    [Fact]
    public void Retention_applies_both_age_and_count()
    {
        using var temp = new TempSessionDir();
        var clock = new FakeSessionClock(T0);
        var store = new SessionStore(temp.Path, clock, maxSessions: 2, retention: TimeSpan.FromDays(30));
        store.Add(Session("old.exe", T0.AddDays(-60), 30));
        store.Add(Session("a.exe", T0.AddHours(-3), 30));
        store.Add(Session("b.exe", T0.AddHours(-2), 30));
        store.Add(Session("c.exe", T0.AddHours(-1), 30));

        Assert.Equal(new[] { "c.exe", "b.exe" }, store.List().Select(x => x.App));
    }

    [Fact]
    public void Corrupt_file_is_quarantined_and_the_store_recovers()
    {
        using var temp = new TempSessionDir();
        File.WriteAllText(Path.Combine(temp.Path, "sessions.json"), "{not-json");
        var store = new SessionStore(temp.Path);

        Assert.Empty(store.List());
        Assert.Single(Directory.GetFiles(temp.Path, "sessions.json.corrupt-*"));
        store.Add(Session("fresh.exe", T0, 10));
        Assert.Single(store.List());
    }

    [Fact]
    public void Rows_from_an_older_file_are_normalized_rather_than_trusted()
    {
        using var temp = new TempSessionDir();
        // No Id, no App, no trend, a duration that disagrees with the timestamps: everything a file
        // written by an earlier build (or hand-edited) can plausibly be missing.
        File.WriteAllText(Path.Combine(temp.Path, "sessions.json"), """
        [
          { "StartedUtc": "2026-08-28T12:00:00+00:00", "EndedUtc": "2026-08-28T12:30:00+00:00", "DurationSeconds": -5 },
          null
        ]
        """);
        var store = new SessionStore(temp.Path);
        var only = Assert.Single(store.List());

        Assert.NotEqual(Guid.Empty, only.Id);
        Assert.Equal("unknown", only.App);
        Assert.Equal(1800, only.DurationSeconds);
        Assert.Empty(only.FpsTrend);
    }

    [Fact]
    public void Per_game_rollup_totals_playtime_and_keeps_the_best_run()
    {
        var summaries = SessionMath.PerGame(
        [
            Session("game.exe", T0, 30, fps: 50, low: 40),
            Session("game.exe", T0.AddHours(1), 60, fps: 62, low: 44),
            Session("other.exe", T0.AddHours(3), 10, fps: 120, low: 90),
        ]);

        var game = summaries.Single(x => x.App == "game.exe");
        Assert.Equal(2, game.Sessions);
        Assert.Equal(5400, game.TotalSeconds);
        Assert.Equal(58, game.FpsAvg); // duration-weighted: (50*1800 + 62*3600) / 5400
        Assert.Equal(62, game.FpsBest);
        Assert.Equal(T0.AddHours(1), game.LastPlayedUtc);
        // Most playtime first — the game you actually play heads the list.
        Assert.Equal("game.exe", summaries[0].App);
    }

    [Fact]
    public void Per_game_rollup_stays_null_when_no_session_had_an_fps_reading()
    {
        var summaries = SessionMath.PerGame([Session("game.exe", T0, 30, fps: null, low: null)]);
        Assert.Null(summaries[0].FpsAvg);
        Assert.Null(summaries[0].FpsBest);
        Assert.Equal(1800, summaries[0].TotalSeconds);
    }

    [Fact]
    public void Store_rejects_nonsensical_limits()
    {
        using var temp = new TempSessionDir();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionStore(temp.Path, maxSessions: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionStore(temp.Path, retention: TimeSpan.Zero));
    }

    private sealed class TempSessionDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpd-sessions-" + Guid.NewGuid().ToString("N"));
        public TempSessionDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private sealed class FakeSessionClock(DateTimeOffset now) : ISessionClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
