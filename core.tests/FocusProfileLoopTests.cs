// GPD Forge — the auto-profile tick against a restored mode. GPL-3.0-or-later.
//
// Audit round 2 (2026-09-25): since ModeStore, the daemon starts in the USER'S mode (say `gaming`).
// Auto-profiles are on by default and seeded their engine with that mode, but a foreground with no
// rule resolves to `windows` on AC — so three ticks (~4.5 s) after every restart the worker "switched"
// to windows, wrote its 15/20/17 W and saved `windows` over the user's pick on disk. Nothing had
// changed; the engine was just comparing the first target with a mode it never chose.
using GpdForge.Api;
using GpdForge.Profiles;
using GpdForge.Tdp;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class FocusProfileLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpdforge-focus-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class FakeForeground(string? proc) : IForegroundApp
    {
        public string? Proc { get; set; } = proc;
        public string? Current() => Proc;
    }

    private sealed class CountingTdp : ITdpController
    {
        public List<TdpProfile> Writes { get; } = [];
        public Task<TdpApplyResult> ApplyAsync(TdpProfile profile, string owner, CancellationToken ct)
        {
            Writes.Add(profile);
            return Task.FromResult(new TdpApplyResult(profile, new TdpReadout(profile.StapmW, profile.FastW), true, 1));
        }
    }

    private sealed class NoRivals : IPowerControllerDetector
    {
        public bool OthersRunning(out string[] names) { names = []; return false; }
    }

    private static FixedTelemetrySource OnAc() =>
        new(TelemetrySnapshot.Unmeasured with { AcConnected = true, AcUnknown = false });

    private static FixedTelemetrySource OnBattery() =>
        new(TelemetrySnapshot.Unmeasured with { AcConnected = false, AcUnknown = false });

    private (FocusProfileLoop Loop, FakeForeground Fg, CountingTdp Tdp) Build(
        ModeState mode, string? foreground, ITelemetrySource? telemetry = null, TdpIntent? intent = null,
        Func<string, bool>? isRunning = null)
    {
        var fg = new FakeForeground(foreground);
        var tdp = new CountingTdp();
        var rules = new AppRuleStore(Path.Combine(_dir, "rules"));
        var applier = new ProfileApplier(tdp, new NoRivals(), intent: intent);
        var loop = new FocusProfileLoop(fg, telemetry ?? OnAc(), mode, applier, rules, isRunning: isRunning ?? (_ => true));
        return (loop, fg, tdp);
    }

    private static async Task TickAsync(FocusProfileLoop loop, int times)
    {
        for (int i = 0; i < times; i++) await loop.TickAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_restored_mode_survives_a_foreground_no_rule_names()
    {
        var store = new ModeStore(_dir);
        new ModeState(store).Active = "gaming";
        var modeFile = Path.Combine(_dir, "mode.json");
        var savedBefore = File.ReadAllText(modeFile);

        var mode = new ModeState(new ModeStore(_dir));
        Assert.Equal("gaming", mode.Active);
        var (loop, _, tdp) = Build(mode, foreground: "notepad");

        await TickAsync(loop, 6);   // twice the hysteresis

        Assert.Equal("gaming", mode.Active);
        Assert.Equal(savedBefore, File.ReadAllText(modeFile));
        Assert.Empty(tdp.Writes);
    }

    [Fact]
    public async Task A_restored_mode_survives_the_session_agent_starting_to_report()
    {
        // Until the session agent reports, the foreground is null; its first report is not a choice
        // the user made either, as long as it resolves to the same mode as before.
        new ModeState(new ModeStore(_dir)).Active = "gaming";
        var mode = new ModeState(new ModeStore(_dir));
        var (loop, fg, tdp) = Build(mode, foreground: null);

        await TickAsync(loop, 2);
        fg.Proc = "notepad";
        await TickAsync(loop, 4);

        Assert.Equal("gaming", mode.Active);
        Assert.Empty(tdp.Writes);
    }

    [Fact]
    public async Task After_a_restore_a_real_change_of_foreground_still_switches()
    {
        new ModeState(new ModeStore(_dir)).Active = "gaming";
        var mode = new ModeState(new ModeStore(_dir));
        var (loop, fg, tdp) = Build(mode, foreground: "notepad");

        await TickAsync(loop, 2);
        fg.Proc = "ollama";
        await TickAsync(loop, 3);

        Assert.Equal("ai", mode.Active);
        Assert.Single(tdp.Writes);
        Assert.Equal("ai", new ModeStore(_dir).Read());
    }

    [Fact]
    public async Task Without_a_restored_mode_the_foreground_rule_still_wins_from_the_start()
    {
        // Behaviour before the store existed, kept: a fresh install starts in windows and a ruled app
        // already in front switches after the hysteresis.
        var mode = new ModeState(new ModeStore(_dir));
        Assert.False(mode.Restored);
        var (loop, _, tdp) = Build(mode, foreground: "ollama");

        await TickAsync(loop, 3);

        Assert.Equal("ai", mode.Active);
        Assert.Single(tdp.Writes);
    }

    [Fact]
    public async Task Nothing_is_decided_before_the_first_sample()
    {
        new ModeState(new ModeStore(_dir)).Active = "gaming";
        var mode = new ModeState(new ModeStore(_dir));
        var fg = new FakeForeground("notepad");
        var tdp = new CountingTdp();
        var loop = new FocusProfileLoop(fg, new UnsampledSource(), mode,
            new ProfileApplier(tdp, new NoRivals()), new AppRuleStore(Path.Combine(_dir, "rules")));

        Assert.Null(await loop.TickAsync(CancellationToken.None));
        Assert.Equal("gaming", mode.Active);
        Assert.Empty(tdp.Writes);
    }

    // Audit round 3 (2026-09-25): since F0 the session agent reports the real foreground, and the
    // shipped overlay is an Edge --app window. Opening it over a ruled game made `msedge` the
    // foreground, which no rule names, so three ticks later the mode flipped to windows, wrote its
    // preset and ended the manual override the user had just set from that same overlay.
    [Theory]
    [InlineData("msedge")]          // the overlay
    [InlineData("gpd-forge")]       // the Forge window
    [InlineData("explorer")]        // the shell, e.g. the Start menu over the game
    public async Task A_non_game_window_over_a_running_game_does_not_switch_the_mode(string overlay)
    {
        var intent = new TdpIntent();
        var mode = new ModeState();
        var (loop, fg, tdp) = Build(mode, foreground: "retroarch", intent: intent);
        await TickAsync(loop, 3);
        Assert.Equal("gaming", mode.Active);
        tdp.Writes.Clear();

        var manual = TdpIntent.ManualProfile(12, "gaming");
        intent.SetManual("gaming", manual);   // the overlay's TDP stepper
        fg.Proc = overlay;
        await TickAsync(loop, 6);

        Assert.Equal("gaming", mode.Active);
        Assert.Empty(tdp.Writes);
        Assert.Equal(manual, intent.Manual("gaming"));
    }

    [Fact]
    public async Task The_desktop_after_the_game_exits_still_switches_back()
    {
        // Holding the game only makes sense while it is still there underneath. Closed, the shell in
        // front is just the desktop, and the mode follows it back to windows as before.
        bool gameRunning = true;
        var mode = new ModeState();
        var (loop, fg, _) = Build(mode, foreground: "retroarch", isRunning: _ => gameRunning);
        await TickAsync(loop, 3);
        Assert.Equal("gaming", mode.Active);

        gameRunning = false;
        fg.Proc = "explorer";
        await TickAsync(loop, 3);

        Assert.Equal("windows", mode.Active);
    }

    [Fact]
    public async Task A_non_game_window_with_no_game_before_it_resolves_as_itself()
    {
        // Nothing to hold: browsing with no game running is browsing, on whatever the power source says.
        var mode = new ModeState();
        var (loop, _, _) = Build(mode, foreground: "msedge", telemetry: OnBattery());

        await TickAsync(loop, 3);

        Assert.Equal("battery", mode.Active);
    }

    // Audit round 3 (2026-09-25): every writer saved the mode, so a mode auto-profiles picked was
    // restored after a restart as if the user had chosen it, and the engine adopted it: `battery`
    // saved unplugged kept 8 W on a machine that booted on AC, until a ruled app or an AC edge.
    [Fact]
    public async Task An_automatic_mode_is_not_restored_as_the_users_choice()
    {
        var before = new ModeState(new ModeStore(_dir));
        var (unplugged, _, _) = Build(before, foreground: "notepad", telemetry: OnBattery());
        await TickAsync(unplugged, 3);
        Assert.Equal("battery", before.Active);

        var after = new ModeState(new ModeStore(_dir));
        Assert.False(after.Restored);
        var (pluggedIn, _, _) = Build(after, foreground: "notepad");
        await TickAsync(pluggedIn, 3);

        Assert.Equal("windows", after.Active);
    }

    [Fact]
    public async Task An_automatic_switch_after_a_user_pick_is_what_a_restart_starts_from()
    {
        // The user's pick is not resurrected over a later automatic switch either: the file records
        // the latest mode and who chose it, so a restart re-derives from the power source and the app.
        new ModeState(new ModeStore(_dir)).Active = "gaming";
        var mode = new ModeState(new ModeStore(_dir));
        var (loop, fg, _) = Build(mode, foreground: "notepad", telemetry: OnBattery());
        await TickAsync(loop, 2);
        fg.Proc = "ollama";
        await TickAsync(loop, 3);
        Assert.Equal("ai", mode.Active);

        var after = new ModeState(new ModeStore(_dir));

        Assert.False(after.Restored);
        Assert.Equal("windows", after.Active);
    }

    private sealed class UnsampledSource : ITelemetrySource
    {
        public TelemetryReading Latest => TelemetryReading.Unsampled;
        public Task<TelemetryReading> WaitForNewerAsync(long afterSequence, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(Latest);
    }
}
