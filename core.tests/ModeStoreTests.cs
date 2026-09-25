// GPD Forge — the active mode survives a restart. GPL-3.0-or-later.
//
// Audit round 2 (2026-09-24): since the startup apply exists, the daemon WRITES the active mode's TDP
// when it starts. The active mode lived only in memory and defaulted to `windows`, so a reboot or a
// service restart while the user was in `gaming` actively wrote windows 15/20/17 W — where before the
// startup apply it had at least left the previous limits alone. The mode is kept on disk now, with the
// same file discipline as the fan preference.
using GpdForge.Api;
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class ModeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpdforge-mode-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_mode_picked_before_a_restart_is_the_active_mode_after_it()
    {
        var before = new ModeState(new ModeStore(_dir));
        before.Active = "gaming";

        var after = new ModeState(new ModeStore(_dir));

        Assert.Equal("gaming", after.Active);
    }

    [Fact]
    public void With_nothing_saved_the_mode_is_windows()
    {
        Assert.Equal(ModeCatalogue.Windows, new ModeState(new ModeStore(_dir)).Active);
        Assert.Equal(ModeCatalogue.Windows, new ModeState().Active);
    }

    [Fact]
    public void A_corrupt_file_is_set_aside_and_the_mode_is_windows()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mode.json"), "{ not json");

        Assert.Equal(ModeCatalogue.Windows, new ModeState(new ModeStore(_dir)).Active);
        Assert.NotEmpty(Directory.GetFiles(_dir, "mode.json.corrupt-*"));
    }

    [Fact]
    public void A_saved_mode_the_catalogue_does_not_know_is_not_trusted()
    {
        // Whatever is read back is about to decide a TDP write at start.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mode.json"), "{ \"active\": \"overdrive\" }");

        Assert.Equal(ModeCatalogue.Windows, new ModeState(new ModeStore(_dir)).Active);
    }

    [Fact]
    public void An_unknown_mode_set_in_memory_is_not_written_over_the_saved_one()
    {
        var state = new ModeState(new ModeStore(_dir)) { Active = "battery" };
        state.Active = "no-such-mode";   // POST /mode takes any name; ProfileApplier answers UnknownMode

        Assert.Equal("no-such-mode", state.Active);
        Assert.Equal("battery", new ModeState(new ModeStore(_dir)).Active);
    }

    [Fact]
    public void A_store_that_cannot_write_keeps_the_mode_in_memory_and_does_not_throw()
    {
        var store = new ModeStore(_dir);
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "mode.json"));   // a directory where the file goes

        var state = new ModeState(store) { Active = "gaming" };

        Assert.Equal("gaming", state.Active);
    }

    [Fact]
    public void A_mode_switched_automatically_is_saved_but_not_restored()
    {
        // Audit round 3 (2026-09-25): an automatic switch is a reaction to the power source or the
        // app in front, both of which may differ at the next start; only a user's pick is restored.
        var state = new ModeState(new ModeStore(_dir));
        state.SwitchAutomatically("battery");

        Assert.Equal("battery", state.Active);
        Assert.Equal(new ModeStore.Entry("battery", ChosenByUser: false), new ModeStore(_dir).ReadEntry());
        var after = new ModeState(new ModeStore(_dir));
        Assert.False(after.Restored);
        Assert.Equal(ModeCatalogue.Windows, after.Active);
    }

    [Fact]
    public void Re_picking_the_mode_an_automatic_switch_chose_makes_it_the_users()
    {
        var state = new ModeState(new ModeStore(_dir));
        state.SwitchAutomatically("gaming");
        state.Active = "gaming";   // POST /mode with the mode already active

        var after = new ModeState(new ModeStore(_dir));
        Assert.True(after.Restored);
        Assert.Equal("gaming", after.Active);
    }

    [Fact]
    public void An_automatic_switch_to_the_users_mode_leaves_it_the_users()
    {
        var state = new ModeState(new ModeStore(_dir)) { Active = "gaming" };
        state.SwitchAutomatically("gaming");

        Assert.True(new ModeState(new ModeStore(_dir)).Restored);
    }

    [Fact]
    public void A_file_saved_before_the_source_was_recorded_is_the_users_choice()
    {
        // Round 2 files carry only the mode; they were written for the user's pick, so they keep it.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mode.json"), "{ \"active\": \"gaming\" }");

        var state = new ModeState(new ModeStore(_dir));
        Assert.True(state.Restored);
        Assert.Equal("gaming", state.Active);
    }
}
