// GPD Forge — F5 "freeze in background while playing": what is frozen, and that it is ALWAYS thawed. GPL-3.0-or-later.
//
// The feature's failure mode is a process left suspended, so most of these tests are exit paths: the
// game leaving the front, the mode changing, a clean stop, an exception in Begin or End, and a crash
// (no code runs; the next start resumes from disk). The OS calls are faked (FreezerServiceTests' own
// pattern): no real process is touched.
using GpdForge.Api;
using GpdForge.Fan;
using GpdForge.Gpu;
using GpdForge.Profiles;
using GpdForge.SystemControl;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed partial class GameProfileTests
{
    private sealed class FakeSuspender : IProcessSuspender
    {
        public List<int> Suspended { get; } = [];
        public List<int> Resumed { get; } = [];
        public void Suspend(int pid) => Suspended.Add(pid);
        public void Resume(int pid) => Resumed.Add(pid);
        /// <summary>PIDs suspended and not yet resumed, counting repeats — what the OS would still hold frozen.</summary>
        public IReadOnlyList<int> StillFrozen()
        {
            var left = new List<int>(Suspended);
            foreach (var pid in Resumed) left.Remove(pid);
            return left;
        }
    }

    private sealed class FakeLister(params ProcessRef[] procs) : IProcessLister
    {
        public Func<string, bool> Throws { get; set; } = _ => false;
        public IReadOnlyList<ProcessRef> ByName(string name) =>
            Throws(name) ? throw new InvalidOperationException("enumeration failed")
                : procs.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>A logger that throws on Information: the one way to make GameProfileApplier's Begin and
    /// End fail from outside, after they have done their work.</summary>
    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information) throw new InvalidOperationException("log sink failed");
        }
    }

    private static readonly ProcessRef[] Background =
    [
        new(100, "ollama"), new(101, "ollama"), new(200, "OneDrive"), new(300, "explorer"), new(400, "Discord"),
    ];

    private (GameFreezer Freezer, FreezerService Service, FakeSuspender Os, FakeLister Lister) Freezer(FreezeRecordStore? store = null)
    {
        var os = new FakeSuspender();
        var lister = new FakeLister(Background);
        var service = new FreezerService(os, lister);
        return (new GameFreezer(service, os, lister, store ?? new FreezeRecordStore(StateDir)), service, os, lister);
    }

    private static AppRule FreezeRule(params string[] freeze) =>
        new(Guid.NewGuid(), "eldenring", "gaming", true, new RuleOverrides(Freeze: freeze));

    private static GameProfileApplier Applier(GameFreezer freezer, ActiveGameProfileState active, ILogger<GameProfileApplier>? logger = null) =>
        new(new TdpIntent(), new FanState(), new FanOverride(), new GpuDesiredState(), new GpuAgentState(),
            new AutoFpsState(), active, logger: logger, gpuGateOpen: () => true, freezer: freezer);

    // --- policy ----------------------------------------------------------------------------------

    [Fact]
    public void Plan_normalises_dedupes_and_drops_protected_and_already_frozen_names()
    {
        var plan = GameFreezer.Plan(["Ollama.exe", "ollama", " OneDrive ", "explorer", "svchost", "discord", ""], ["discord"]);
        Assert.Equal(["ollama", "onedrive"], plan);
    }

    [Fact]
    public void Suggested_defaults_are_the_measured_heavy_apps_and_none_is_protected()
    {
        Assert.Equal(["ollama", "lm studio", "onedrive", "googledrivefs"], GameFreezer.Suggested);
        Assert.DoesNotContain(GameFreezer.Suggested, FreezerService.IsProtected);
    }

    [Fact]
    public void Freeze_suspends_only_running_listed_processes_and_reports_what_it_froze()
    {
        var (f, _, os, _) = Freezer();
        var frozen = f.Freeze(["ollama", "lm studio", "onedrive", "explorer"]);

        Assert.Equal(["ollama", "onedrive"], frozen);          // lm studio is not running; explorer is protected
        Assert.Equal([100, 101, 200], os.Suspended.Order());
        Assert.DoesNotContain(300, os.Suspended);
    }

    [Fact]
    public void A_name_the_user_froze_by_hand_is_neither_claimed_nor_thawed_by_the_game()
    {
        var (f, service, os, _) = Freezer();
        service.FreezeByName("discord");                    // the Monitor page's Freezer card
        f.Freeze(["discord", "ollama"]);
        f.Thaw();

        Assert.Contains("discord", service.Frozen, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(400, os.Resumed);
        Assert.Equal([100, 101], os.Resumed.Order());
    }

    [Fact]
    public void An_enumeration_failure_part_way_thaws_what_was_already_frozen()
    {
        var (f, _, os, lister) = Freezer();
        lister.Throws = n => n == "onedrive";
        // FreezerService lets the lister's exception through; GameFreezer must not leave ollama suspended.
        var frozen = f.Freeze(["ollama", "onedrive"]);

        Assert.Empty(frozen);
        Assert.Empty(f.Held);
        Assert.Empty(os.StillFrozen());
    }

    // --- the profile's exit paths ------------------------------------------------------------------

    [Fact]
    public void The_profile_freezes_on_Begin_reports_it_and_thaws_on_End()
    {
        var (f, _, os, _) = Freezer();
        var active = new ActiveGameProfileState();
        var games = Applier(f, active);

        games.Begin(FreezeRule("ollama", "onedrive", "lm studio"), "eldenring", "gaming");
        Assert.Equal(["ollama", "onedrive"], active.Current!.Frozen);
        Assert.Equal(["ollama", "onedrive", "lm studio"], active.Current.Freeze);   // what the rule asked, as before

        games.End("gaming");
        Assert.Empty(os.StillFrozen());
        Assert.Empty(f.Held);
    }

    [Fact]
    public void A_Begin_that_fails_after_freezing_leaves_nothing_suspended()
    {
        var (f, _, os, _) = Freezer();
        var games = Applier(f, new ActiveGameProfileState(), new ThrowingLogger<GameProfileApplier>());

        Assert.Throws<InvalidOperationException>(() => games.Begin(FreezeRule("ollama", "onedrive"), "eldenring", "gaming"));

        Assert.Equal([100, 101, 200], os.Suspended.Order());   // it did freeze...
        Assert.Empty(os.StillFrozen());                          // ...and thawed before the exception left
    }

    [Fact]
    public void An_End_whose_logging_throws_still_thaws()
    {
        var (f, _, os, _) = Freezer();
        var active = new ActiveGameProfileState();
        var logger = new SwitchableLogger<GameProfileApplier>();
        var games = Applier(f, active, logger);
        games.Begin(FreezeRule("ollama"), "eldenring", "gaming");

        logger.Throw = true;
        Assert.Throws<InvalidOperationException>(() => games.End("gaming"));
        Assert.Empty(os.StillFrozen());
    }

    [Fact]
    public void A_clean_stop_thaws()
    {
        var (f, _, os, _) = Freezer();
        var games = Applier(f, new ActiveGameProfileState());
        games.Begin(FreezeRule("ollama"), "eldenring", "gaming");

        games.Shutdown("gaming");
        Assert.Empty(os.StillFrozen());
    }

    [Fact]
    public async Task Leaving_the_game_thaws_and_coming_back_freezes_again()
    {
        var (f, _, os, _) = Freezer();
        var rig = Build("eldenring", new RuleOverrides(StapmW: 22, Freeze: ["ollama"]), freezer: f);
        await TickAsync(rig, 3);
        Assert.Equal([100, 101], os.StillFrozen().Order());
        Assert.Equal(["ollama"], rig.Active.Current!.Frozen);

        rig.Fg.Proc = "notepad";
        await TickAsync(rig, 3);
        Assert.Empty(os.StillFrozen());

        rig.Fg.Proc = "eldenring";
        await TickAsync(rig, 3);
        Assert.Equal([100, 101], os.StillFrozen().Order());
    }

    [Fact]
    public async Task A_mode_the_user_picks_over_the_game_thaws()
    {
        var (f, _, os, _) = Freezer();
        var rig = Build("eldenring", new RuleOverrides(StapmW: 22, Freeze: ["ollama"]), freezer: f);
        await TickAsync(rig, 3);
        Assert.NotEmpty(os.StillFrozen());

        rig.Mode.Active = "windows";   // by hand: the engine never overrides it, and the profile ends
        await TickAsync(rig, 1);
        Assert.Empty(os.StillFrozen());
    }

    [Fact]
    public void A_crash_mid_game_is_recovered_at_the_next_start()
    {
        var (f, _, os, _) = Freezer();
        f.Freeze(["ollama", "onedrive"]);
        // The daemon dies: no Thaw. A new process starts with the same data directory.
        var os2 = new FakeSuspender();
        var restarted = new GameFreezer(new FreezerService(os2, new FakeLister(Background)), os2,
            new FakeLister(Background), new FreezeRecordStore(StateDir));

        Assert.Equal(3, restarted.RecoverAtStartup());
        Assert.Equal([100, 101, 200], os2.Resumed.Order());
        Assert.Null(new FreezeRecordStore(StateDir).Read());   // once, not at every start
        Assert.Equal(0, restarted.RecoverAtStartup());
    }

    [Fact]
    public void Recovery_leaves_a_reused_pid_alone()
    {
        var store = new FreezeRecordStore(StateDir);
        store.Write(new FreezeRecord([new FrozenName("ollama", [100, 999])]));
        var os = new FakeSuspender();
        // PID 999 now belongs to something else; 100 is still ollama.
        var lister = new FakeLister(new ProcessRef(100, "ollama"), new ProcessRef(999, "notepad"));
        var f = new GameFreezer(new FreezerService(os, lister), os, lister, store);

        Assert.Equal(1, f.RecoverAtStartup());
        Assert.Equal([100], os.Resumed);
    }

    [Fact]
    public void A_thaw_removes_the_record_so_a_clean_run_recovers_nothing()
    {
        var (f, _, _, _) = Freezer();
        f.Freeze(["ollama"]);
        Assert.NotNull(new FreezeRecordStore(StateDir).Read());
        f.Thaw();
        Assert.Null(new FreezeRecordStore(StateDir).Read());
    }

    [Fact]
    public void A_corrupt_record_is_dropped()
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(Path.Combine(StateDir, FreezeRecordStore.FileName), "{\"names\":[{\"name\":\"x\",\"pids\":[-4]}]}");
        Assert.Null(new FreezeRecordStore(StateDir).Read());
        Assert.False(File.Exists(Path.Combine(StateDir, FreezeRecordStore.FileName)));
    }

    private sealed class SwitchableLogger<T> : ILogger<T>
    {
        public bool Throw { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Throw && logLevel == LogLevel.Information) throw new InvalidOperationException("log sink failed");
        }
    }
}
