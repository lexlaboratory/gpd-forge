// GPD Forge — the foreground app comes from the user's session, not session 0. GPL-3.0-or-later.
//
// The installed daemon is a LocalSystem service in session 0, where GetForegroundWindow is always
// NULL. Audit 2026-09-24: GET /app-rules read three times while the user had windows open, and every
// `lastMatch.process` was null — the FPS target and the auto-profile worker were both blind.
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

public class SessionForegroundAppTests
{
    private sealed class FixedForeground(string? name) : IForegroundApp
    {
        public string? Name { get; set; } = name;
        public string? Current() => Name;
    }

    [Fact]
    public void In_session_0_with_no_agent_report_it_is_the_local_answer_which_is_null()
    {
        var fg = new SessionForegroundApp(new FixedForeground(null), new ManualTimeProvider());

        Assert.Null(fg.Current());
        Assert.Equal(SessionForegroundApp.SourceLocal, fg.Describe().Source);
    }

    [Fact]
    public void A_fresh_agent_report_wins_over_the_local_query()
    {
        var clock = new ManualTimeProvider();
        var fg = new SessionForegroundApp(new FixedForeground(null), clock);

        fg.Report("eldenring");
        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal("eldenring", fg.Current());
        var d = fg.Describe();
        Assert.Equal(SessionForegroundApp.SourceAgent, d.Source);
        Assert.Equal(3000, d.AgeMs);
    }

    [Fact]
    public void An_agent_report_of_nothing_in_front_is_an_answer_not_a_gap()
    {
        // The lock screen, or the desktop with no window: the agent says "nothing", and the local
        // query must not be consulted in its place.
        var fg = new SessionForegroundApp(new FixedForeground("stale-local-answer"), new ManualTimeProvider());

        fg.Report(null);

        Assert.Null(fg.Current());
        Assert.Equal(SessionForegroundApp.SourceAgent, fg.Describe().Source);
    }

    [Fact]
    public void A_report_older_than_the_freshness_window_is_ignored()
    {
        // An agent that went away (log-off, crash) must not keep steering the FPS target and the mode
        // by the name of an app that may have closed long ago.
        var clock = new ManualTimeProvider();
        var local = new FixedForeground("explorer");
        var fg = new SessionForegroundApp(local, clock);

        fg.Report("eldenring");
        clock.Advance(SessionForegroundApp.Freshness + TimeSpan.FromMilliseconds(1));

        Assert.Equal("explorer", fg.Current());
        Assert.Equal(SessionForegroundApp.SourceLocal, fg.Describe().Source);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("eldenring", true)]
    [InlineData("GPD Forge", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(@"C:\Games\eldenring.exe", false)]
    [InlineData("../evil", false)]
    [InlineData("bad\nname", false)]
    public void Only_a_bare_process_name_or_null_is_accepted(string? name, bool valid)
    {
        Assert.Equal(valid, SessionForegroundApp.IsValidProcessName(name));
        var fg = new SessionForegroundApp(new FixedForeground(null), new ManualTimeProvider());
        if (valid) fg.Report(name);
        else Assert.Throws<ArgumentException>(() => fg.Report(name));
    }

    [Fact]
    public void An_overlong_name_is_rejected()
    {
        Assert.False(SessionForegroundApp.IsValidProcessName(new string('a', SessionForegroundApp.MaxProcessNameLength + 1)));
    }
}
