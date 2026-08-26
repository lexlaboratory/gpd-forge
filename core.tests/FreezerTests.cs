// GPD Forge - Freezer (suspend/resume background processes) tests. GPL-3.0-or-later.
//
// Exercises the freeze/thaw bookkeeping with a fake IProcessSuspender (records Suspend/Resume
// by PID) and a fake IProcessLister (a canned process table), so no real process is ever
// touched. Covers: FreezeByName suspends + tracks, Thaw resumes + forgets, ".exe" tolerance,
// idempotency, and that protected/critical processes are never suspended.
using GpdForge.SystemControl;
using Xunit;

namespace GpdForge.Core.Tests;

public class FreezerServiceTests
{
    /// <summary>Fake OS suspender: records every Suspend/Resume call by PID.</summary>
    private sealed class FakeSuspender : IProcessSuspender
    {
        public List<int> Suspended { get; } = [];
        public List<int> Resumed { get; } = [];
        public void Suspend(int pid) => Suspended.Add(pid);
        public void Resume(int pid) => Resumed.Add(pid);
    }

    /// <summary>Fake process table: returns the canned rows whose name matches the query.</summary>
    private sealed class FakeLister(params ProcessRef[] procs) : IProcessLister
    {
        public IReadOnlyList<ProcessRef> ByName(string name) =>
            procs.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>Fake process table that returns its rows regardless of the query name.</summary>
    private sealed class ConstLister(params ProcessRef[] procs) : IProcessLister
    {
        public IReadOnlyList<ProcessRef> ByName(string name) => procs;
    }

    [Fact]
    public void Frozen_is_empty_initially()
    {
        var svc = new FreezerService(new FakeSuspender(), new FakeLister());
        Assert.Empty(svc.Frozen);
    }

    [Fact]
    public void FreezeByName_suspends_matching_pids_and_tracks_the_name()
    {
        var fake = new FakeSuspender();
        var svc = new FreezerService(fake, new FakeLister(
            new ProcessRef(1000, "chrome"),
            new ProcessRef(1001, "chrome"),
            new ProcessRef(2000, "notepad")));   // different name — must be left alone

        int n = svc.FreezeByName("chrome");

        Assert.Equal(2, n);
        Assert.Equal([1000, 1001], fake.Suspended.OrderBy(x => x));
        Assert.Contains("chrome", svc.Frozen);
        Assert.Empty(fake.Resumed);
    }

    [Fact]
    public void FreezeByName_tolerates_the_exe_suffix()
    {
        var fake = new FakeSuspender();
        var svc = new FreezerService(fake, new FakeLister(new ProcessRef(1000, "chrome")));

        int n = svc.FreezeByName("chrome.exe");

        Assert.Equal(1, n);
        Assert.Contains(1000, fake.Suspended);
        Assert.Contains("chrome", svc.Frozen);
    }

    [Fact]
    public void FreezeByName_is_idempotent_per_pid()
    {
        var fake = new FakeSuspender();
        var svc = new FreezerService(fake, new FakeLister(new ProcessRef(1000, "chrome")));

        Assert.Equal(1, svc.FreezeByName("chrome"));   // suspends 1000
        Assert.Equal(0, svc.FreezeByName("chrome"));   // already frozen -> no new suspend

        Assert.Equal([1000], fake.Suspended);          // Suspend called exactly once
    }

    [Fact]
    public void FreezeByName_of_a_missing_process_freezes_nothing()
    {
        var fake = new FakeSuspender();
        var svc = new FreezerService(fake, new FakeLister());   // empty process table

        Assert.Equal(0, svc.FreezeByName("chrome"));
        Assert.Empty(fake.Suspended);
        Assert.DoesNotContain("chrome", svc.Frozen);
    }

    [Fact]
    public void Thaw_resumes_tracked_pids_and_forgets_the_name()
    {
        var fake = new FakeSuspender();
        var svc = new FreezerService(fake, new FakeLister(
            new ProcessRef(1000, "chrome"),
            new ProcessRef(1001, "chrome")));

        svc.FreezeByName("chrome");
        int n = svc.Thaw("chrome");

        Assert.Equal(2, n);
        Assert.Equal([1000, 1001], fake.Resumed.OrderBy(x => x));
        Assert.DoesNotContain("chrome", svc.Frozen);
    }

    [Fact]
    public void Thaw_of_an_unknown_name_is_a_noop()
    {
        var fake = new FakeSuspender();
        var svc = new FreezerService(fake, new FakeLister());

        Assert.Equal(0, svc.Thaw("chrome"));
        Assert.Empty(fake.Resumed);
    }

    [Fact]
    public void ThawAll_resumes_every_frozen_process_and_clears_the_set()
    {
        var fake = new FakeSuspender();
        var svc = new FreezerService(fake, new FakeLister(
            new ProcessRef(1000, "chrome"),
            new ProcessRef(2000, "discord")));

        svc.FreezeByName("chrome");
        svc.FreezeByName("discord");
        int n = svc.ThawAll();

        Assert.Equal(2, n);
        Assert.Equal([1000, 2000], fake.Resumed.OrderBy(x => x));
        Assert.Empty(svc.Frozen);
    }

    [Theory]
    [InlineData("System")]
    [InlineData("csrss")]
    [InlineData("wininit")]
    [InlineData("winlogon")]
    [InlineData("services")]
    [InlineData("lsass")]
    [InlineData("svchost")]
    [InlineData("dwm")]
    [InlineData("explorer")]
    [InlineData("GpdForge.Service")]
    [InlineData("dotnet")]
    [InlineData("SVCHOST.EXE")]   // case- and extension-insensitive
    public void FreezeByName_never_touches_protected_processes(string name)
    {
        var fake = new FakeSuspender();
        // Even if the OS reported such a process running, the Freezer must refuse it.
        var svc = new FreezerService(fake, new ConstLister(new ProcessRef(4, name)));

        int n = svc.FreezeByName(name);

        Assert.Equal(0, n);
        Assert.Empty(fake.Suspended);
        Assert.DoesNotContain(name, svc.Frozen);
        Assert.True(FreezerService.IsProtected(name));
    }

    [Fact]
    public void A_protected_process_returned_by_the_lister_is_skipped_others_are_frozen()
    {
        var fake = new FakeSuspender();
        // A non-protected query name whose (contrived) results include a protected process.
        var svc = new FreezerService(fake, new ConstLister(
            new ProcessRef(4, "lsass"),     // protected — must be skipped
            new ProcessRef(500, "myapp"))); // ok — must be suspended

        int n = svc.FreezeByName("myapp");

        Assert.Equal(1, n);
        Assert.Equal([500], fake.Suspended);
        Assert.DoesNotContain(4, fake.Suspended);
    }

    [Fact]
    public void IsProtected_is_false_for_ordinary_apps()
    {
        Assert.False(FreezerService.IsProtected("chrome"));
        Assert.False(FreezerService.IsProtected("discord.exe"));
        Assert.False(FreezerService.IsProtected("steam"));
    }
}
