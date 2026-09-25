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

    private (FocusProfileLoop Loop, FakeForeground Fg, CountingTdp Tdp) Build(ModeState mode, string? foreground)
    {
        var fg = new FakeForeground(foreground);
        var tdp = new CountingTdp();
        var rules = new AppRuleStore(Path.Combine(_dir, "rules"));
        var loop = new FocusProfileLoop(fg, OnAc(), mode, new ProfileApplier(tdp, new NoRivals()), rules);
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

    private sealed class UnsampledSource : ITelemetrySource
    {
        public TelemetryReading Latest => TelemetryReading.Unsampled;
        public Task<TelemetryReading> WaitForNewerAsync(long afterSequence, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(Latest);
    }
}
