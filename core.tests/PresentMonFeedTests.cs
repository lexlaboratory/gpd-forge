// GPD Forge — PresentMon feed tests: whose frames, and when they happened. GPL-3.0-or-later.
//
// The two bugs these pin, both from docs/superpowers/plans/2026-09-24-rendimiento-en-juego-plan.md:
// the reading followed "whichever app had the most rows" (Steam's 2 fps UI could not win, but dwm or
// a browser could — and a game in the foreground had no say), and frames were stamped with the time
// the line reached us, so a burst of buffered stdout looked like a burst of frames.
using System.Globalization;
using GpdForge.Profiles;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class FrameClockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Maps_row_times_onto_the_clock_using_the_least_delayed_row()
    {
        var clock = new FrameClock();
        // Row at capture 1000 ms arrives 300 ms late; row at 1500 ms arrives only 50 ms late. The
        // second one bounds the offset better, and every later row uses it.
        Assert.Equal(T0, clock.Stamp(1000, T0));
        clock.Stamp(1500, T0.AddMilliseconds(250));    // offset improves by 250 ms
        Assert.Equal(T0.AddMilliseconds(750), clock.Stamp(2000, T0.AddSeconds(5)));
    }

    [Fact]
    public void A_row_is_never_stamped_after_it_arrived()
    {
        var clock = new FrameClock();
        clock.Stamp(0, T0);
        var stamped = clock.Stamp(10_000, T0.AddSeconds(3)); // "happened" 10 s in but arrived at 3 s
        Assert.True(stamped <= T0.AddSeconds(3));
    }

    [Fact]
    public void A_row_with_no_time_is_stamped_on_arrival()
    {
        var clock = new FrameClock();
        clock.Stamp(1000, T0);
        Assert.Equal(T0.AddSeconds(2), clock.Stamp(null, T0.AddSeconds(2)));
    }

    [Fact]
    public void Reset_forgets_the_offset_for_a_new_capture()
    {
        // A new header means a new capture whose times restart at zero; keeping the old offset would
        // stamp its first frames in the past.
        var clock = new FrameClock();
        clock.Stamp(50_000, T0);
        clock.Reset();
        Assert.Equal(T0.AddSeconds(1), clock.Stamp(10, T0.AddSeconds(1)));
    }
}

public class FrameTargetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static AppActivity A(string app, int frames) => new(app, frames, T0);
    private static bool NoRules(string _) => false;

    [Fact]
    public void The_foreground_app_wins_when_it_is_presenting()
    {
        var apps = new[] { A("steamwebhelper.exe", 4), A("game.exe", 280), A("dwm.exe", 400) };
        Assert.Equal("game.exe", FrameTarget.Choose(apps, "game", NoRules, previous: null));
        // ...even when it presents the fewest frames: foreground is the user's statement of intent.
        Assert.Equal("steamwebhelper.exe", FrameTarget.Choose(apps, "steamwebhelper", NoRules, previous: null));
    }

    [Fact]
    public void Foreground_names_match_without_the_exe_and_ignoring_case()
    {
        var apps = new[] { A("EldenRing.exe", 100) };
        Assert.Equal("EldenRing.exe", FrameTarget.Choose(apps, "eldenring", NoRules, previous: null));
    }

    [Fact]
    public void Falls_back_to_a_rule_matched_app_when_the_foreground_is_not_presenting()
    {
        var rules = ModeRules.Default();
        var apps = new[] { A("dwm.exe", 400), A("retroarch.exe", 120) };
        Assert.Equal("retroarch.exe",
            FrameTarget.Choose(apps, "explorer", p => rules.ModeFor(p) is not null, previous: null));
    }

    [Fact]
    public void Among_rule_matched_apps_the_game_beats_its_launcher()
    {
        // The default rules name "steam", so steamwebhelper is rule-matched too. Frame count is the
        // tie-break only among apps a rule already names — never across everything presenting.
        var rules = ModeRules.Default();
        var apps = new[] { A("steamwebhelper.exe", 4), A("retroarch.exe", 280) };
        Assert.Equal("retroarch.exe", FrameTarget.Choose(apps, null, p => rules.ModeFor(p) is not null, previous: null));
    }

    [Fact]
    public void Keeps_the_previous_target_while_an_overlay_is_in_front()
    {
        // The Forge overlay or the Steam QAM takes the foreground but the game keeps rendering under
        // it; the FPS readout must stay on the game rather than go blank.
        var apps = new[] { A("dwm.exe", 400), A("game.exe", 280) };
        Assert.Equal("game.exe", FrameTarget.Choose(apps, "GPD Forge", NoRules, previous: "game.exe"));
    }

    [Fact]
    public void Never_falls_back_to_the_busiest_app()
    {
        // Nothing in front presents, no rule names anything, no previous target: that is "no
        // reading", not "whatever presents most" — which would be dwm or a browser tab.
        var apps = new[] { A("dwm.exe", 400), A("chrome.exe", 120) };
        Assert.Null(FrameTarget.Choose(apps, "explorer", NoRules, previous: null));
        Assert.Null(FrameTarget.Choose(apps, null, NoRules, previous: "gone.exe"));
    }

    [Fact]
    public void Unknown_processes_are_never_targets()
    {
        // Without elevation PresentMon names some processes "<unknown>"; that is not an app.
        var apps = new[] { A("<unknown>", 400) };
        Assert.Null(FrameTarget.Choose(apps, "<unknown>", _ => true, previous: "<unknown>"));
    }
}

public class PresentMonFeedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static bool NoRules(string _) => false;

    /// <summary>Feeds rows as if each arrived exactly when it happened (no stdout buffering).</summary>
    private static PresentMonFeed Live(string header, IEnumerable<(double TimeMs, string Line)> rows)
    {
        var feed = new PresentMonFeed();
        feed.Accept(header, T0);
        foreach (var (t, line) in rows) feed.Accept(line, T0.AddMilliseconds(t));
        return feed;
    }

    [Fact]
    public void Reports_the_foreground_game_not_the_launcher_beside_it()
    {
        // Plan F0 criterion: "PresentMon reports the game and not the launcher (Steam open plus the game)".
        var rows = PresentMonFixtures.Interleave(
            PresentMonFixtures.Stream("steamwebhelper.exe", fps: 2, seconds: 5),
            PresentMonFixtures.Stream("game.exe", fps: 140, seconds: 5));
        var feed = Live(PresentMonFixtures.Header251, rows);

        Assert.True(feed.TryRead(T0.AddSeconds(5), "game", NoRules, out var s));
        Assert.Equal("game.exe", s.Process);
        Assert.Equal(140.0, s.Fps, 0);
    }

    [Fact]
    public void Reports_the_launcher_when_the_launcher_is_what_the_user_is_looking_at()
    {
        var rows = PresentMonFixtures.Interleave(
            PresentMonFixtures.Stream("steamwebhelper.exe", fps: 2, seconds: 5),
            PresentMonFixtures.Stream("game.exe", fps: 140, seconds: 5));
        var feed = Live(PresentMonFixtures.Header251, rows);

        Assert.True(feed.TryRead(T0.AddSeconds(5), "steamwebhelper", NoRules, out var s));
        Assert.Equal("steamwebhelper.exe", s.Process);
        Assert.Equal(2.0, s.Fps, 1);
    }

    [Fact]
    public void Has_no_reading_when_nothing_in_front_presents_and_no_rule_names_a_presenter()
    {
        var feed = Live(PresentMonFixtures.Header251, PresentMonFixtures.Stream("dwm.exe", fps: 144, seconds: 3));
        Assert.False(feed.TryRead(T0.AddSeconds(3), "explorer", NoRules, out _));
    }

    [Fact]
    public void Na_rows_do_not_cost_the_app_its_valid_frames()
    {
        // Every tenth frame carries FrameTime "NA" (with MsBetweenPresents intact); every
        // twentieth has no usable interval at all. The game keeps its reading and its frame count
        // only loses the rows that truly carried nothing.
        var feed = new PresentMonFeed();
        feed.Accept(PresentMonFixtures.HeaderV2FrameTime, T0);
        int accepted = 0;
        for (int i = 1; i <= 200; i++)
        {
            double t = i * 10.0;
            string frameTime = i % 10 == 0 ? "NA" : "10.0";
            string between = i % 20 == 0 ? "NA" : "10.0";
            if (i % 20 != 0) accepted++;
            feed.Accept(string.Create(CultureInfo.InvariantCulture,
                            $"game.exe,4242,0x1,DXGI,0,0,0,Hardware: Independent Flip,{t:F1},{frameTime},{between},NA,NA"),
                        T0.AddMilliseconds(t));
        }

        Assert.True(feed.TryGetFrameTimes(T0.AddSeconds(2), "game", NoRules, out var series));
        Assert.Equal(accepted, series.FrameTimesMs.Count);
        Assert.True(feed.TryRead(T0.AddSeconds(2), "game", NoRules, out var s));
        Assert.Equal(100.0, s.Fps, 1);
    }

    [Fact]
    public void Frames_are_placed_by_their_own_time_not_by_a_bursty_arrival()
    {
        // 12 s of a 100 fps game reach us in ONE burst (stdout was buffered). Stamped on arrival,
        // all 1200 frames would sit inside the 10 s buffer; stamped by row time, only the last 10 s
        // (1000 frames) do. A later burst after a quiet second must not pile up either.
        var feed = new PresentMonFeed();
        feed.Accept(PresentMonFixtures.Header251, T0);
        // One early row that arrives on time anchors the capture clock...
        feed.Accept(PresentMonFixtures.Row251("game.exe", 0, 10), T0);
        // ...then everything else arrives at once, 12 s in.
        var burstAt = T0.AddSeconds(12);
        foreach (var (_, line) in PresentMonFixtures.Stream("game.exe", fps: 100, seconds: 12))
            feed.Accept(line, burstAt);

        Assert.True(feed.TryGetFrameTimes(burstAt, "game", NoRules, out var series));
        Assert.Equal("game.exe", series.Process);
        Assert.InRange(series.FrameTimesMs.Count, 999, 1001);
    }

    [Fact]
    public void The_frame_time_buffer_is_the_last_ten_seconds_of_the_target_only()
    {
        var rows = PresentMonFixtures.Interleave(
            PresentMonFixtures.Stream("dwm.exe", fps: 144, seconds: 15),
            PresentMonFixtures.Stream("game.exe", fps: 60, seconds: 15));
        var feed = Live(PresentMonFixtures.Header251, rows);

        Assert.True(feed.TryGetFrameTimes(T0.AddSeconds(15), "game", NoRules, out var series));
        Assert.Equal("game.exe", series.Process);
        Assert.InRange(series.FrameTimesMs.Count, 599, 601);
        Assert.All(series.FrameTimesMs, ms => Assert.Equal(16.667, ms, 2));
    }

    [Fact]
    public void Has_no_frame_times_once_the_target_stops_presenting()
    {
        var feed = Live(PresentMonFixtures.Header251, PresentMonFixtures.Stream("game.exe", fps: 60, seconds: 3));
        Assert.False(feed.TryGetFrameTimes(T0.AddSeconds(30), "game", NoRules, out _));
        Assert.False(feed.TryRead(T0.AddSeconds(30), "game", NoRules, out _));
    }

    [Fact]
    public void Keeps_reading_the_game_while_the_overlay_has_focus()
    {
        var rows = PresentMonFixtures.Interleave(
            PresentMonFixtures.Stream("dwm.exe", fps: 144, seconds: 4),
            PresentMonFixtures.Stream("game.exe", fps: 90, seconds: 4));
        var feed = Live(PresentMonFixtures.Header251, rows);

        Assert.True(feed.TryRead(T0.AddSeconds(4), "game", NoRules, out _));          // game in front
        Assert.True(feed.TryRead(T0.AddSeconds(4), "GPD Forge", NoRules, out var s)); // overlay opened
        Assert.Equal("game.exe", s.Process);
    }

    [Fact]
    public void Parses_the_real_251_capture()
    {
        var feed = new PresentMonFeed();
        feed.Accept(PresentMonFixtures.Header251, T0);
        foreach (var line in PresentMonFixtures.Rows251) feed.Accept(line, T0.AddMilliseconds(600));

        Assert.True(feed.TryGetFrameTimes(T0.AddMilliseconds(600), "Orca", NoRules, out var series));
        Assert.Equal("Orca.exe", series.Process);
        // Four Orca rows, in capture order; the webview's zero-interval rows are not frames.
        Assert.Equal([82.7006, 85.2106, 16.1922, 16.8957], series.FrameTimesMs.Select(m => Math.Round(m, 4)));
    }

    [Fact]
    public void Rows_before_any_header_are_ignored()
    {
        var feed = new PresentMonFeed();
        foreach (var (t, line) in PresentMonFixtures.Stream("game.exe", fps: 60, seconds: 1))
            feed.Accept(line, T0.AddMilliseconds(t));
        Assert.False(feed.TryRead(T0.AddSeconds(1), "game", NoRules, out _));
    }

    [Fact]
    public void A_new_capture_header_restarts_the_row_clock()
    {
        // PresentMon restarted (the probe relaunches it after an exit): its times start again at 0.
        // Without a reset those frames would map 20 s into the past and be evicted on arrival.
        var feed = Live(PresentMonFixtures.Header251, PresentMonFixtures.Stream("game.exe", fps: 60, seconds: 20));
        var restart = T0.AddSeconds(21);
        feed.Accept(PresentMonFixtures.Header251, restart);
        foreach (var (t, line) in PresentMonFixtures.Stream("game.exe", fps: 60, seconds: 2))
            feed.Accept(line, restart.AddMilliseconds(t));

        // Last 10 s = the old capture's 13..20 s (~421 frames) plus the new capture's 120. Without
        // the reset the new 120 would land at T0+0..2 s and only the old ~421 would remain.
        Assert.True(feed.TryGetFrameTimes(restart.AddSeconds(2), "game", NoRules, out var series));
        Assert.InRange(series.FrameTimesMs.Count, 535, 545);
    }
}

public class PresentMonFrameRateProbeTests
{
    private sealed class ThrowingForeground : IForegroundApp
    {
        public string? Current() => throw new InvalidOperationException("no desktop");
    }

    [Fact]
    public void Serves_both_the_fps_summary_and_the_frame_time_buffer()
    {
        // One instance behind both registrations (Program.cs), so F2's metrics and the snapshot's FPS
        // can never describe two different targets.
        using var probe = new PresentMonFrameRateProbe(@"C:\nonexistent\PresentMon.exe");
        Assert.IsAssignableFrom<IFrameRateProbe>(probe);
        Assert.IsAssignableFrom<IFrameTimeSource>(probe);
    }

    [Fact]
    public void A_missing_PresentMon_is_no_reading_rather_than_an_exception()
    {
        using var probe = new PresentMonFrameRateProbe(
            @"C:\nonexistent\PresentMon.exe", foreground: new ThrowingForeground(), rules: ModeRules.Default());
        Assert.False(probe.TryRead(out _));
        Assert.False(probe.TryGetFrameTimes(out _));
    }

    [Fact]
    public void Reads_after_dispose_are_no_reading()
    {
        var probe = new PresentMonFrameRateProbe(@"C:\nonexistent\PresentMon.exe");
        probe.Dispose();
        Assert.False(probe.TryRead(out _));
        Assert.False(probe.TryGetFrameTimes(out _));
    }

    [Fact]
    public void The_frame_time_span_is_ten_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), IFrameTimeSource.Span);
    }
}
