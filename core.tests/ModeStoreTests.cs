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

    // ---------------------------------------------------------------------------------------------
    // Auto: whether the daemon may switch the mode on its own (2026-10-11). The UI's Auto switch used
    // to live only in the browser, so "Manual: Windows" stayed on screen while auto-profiles moved the
    // handheld to gaming at 25 W behind it.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Auto_is_on_unless_it_was_turned_off()
    {
        Assert.True(new ModeState().Auto);
        Assert.True(new ModeState(new ModeStore(_dir)).Auto);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mode.json"), "{ \"active\": \"gaming\", \"source\": \"user\" }");
        Assert.True(new ModeState(new ModeStore(_dir)).Auto);
    }

    [Fact]
    public void Auto_turned_off_stays_off_after_a_restart()
    {
        var state = new ModeState(new ModeStore(_dir)) { Active = "windows" };
        state.SetAuto(false);

        var after = new ModeState(new ModeStore(_dir));
        Assert.False(after.Auto);
        Assert.Equal("windows", after.Active);
    }

    [Fact]
    public void Turning_auto_off_makes_the_mode_in_force_the_users()
    {
        // Off means "keep what is on now". A mode auto-profiles chose is not restored after a restart,
        // so without this the daemon would come back in windows with nothing left to switch it.
        var state = new ModeState(new ModeStore(_dir));
        state.SwitchAutomatically("gaming");
        state.SetAuto(false);

        var after = new ModeState(new ModeStore(_dir));
        Assert.True(after.Restored);
        Assert.Equal("gaming", after.Active);
        Assert.True(after.ChosenByUser);
    }

    [Fact]
    public void A_mode_write_keeps_auto_as_it_was()
    {
        var state = new ModeState(new ModeStore(_dir));
        state.SetAuto(false);
        state.Active = "battery";
        state.SwitchAutomatically("gaming");

        Assert.False(new ModeState(new ModeStore(_dir)).Auto);
    }

    [Fact]
    public void Picking_a_mode_makes_it_the_users_and_turns_auto_off()
    {
        var state = new ModeState(new ModeStore(_dir));
        state.SwitchAutomatically("gaming");

        state.Pick("windows");

        Assert.Equal(new ModeState.View("windows", Auto: false, ChosenByUser: true), state.Snapshot());
        Assert.Equal(new ModeStore.Entry("windows", ChosenByUser: true, Auto: false), new ModeStore(_dir).ReadEntry());
    }

    [Fact]
    public void An_automatic_switch_is_refused_while_auto_is_off()
    {
        // In the state itself, not only in its callers: the focus loop reads Auto, then switches a few
        // lines later, and a POST /mode landing in between used to be overwritten by that switch.
        var state = new ModeState(new ModeStore(_dir));
        state.Pick("windows");

        Assert.False(state.SwitchAutomatically("gaming"));

        Assert.Equal(new ModeState.View("windows", Auto: false, ChosenByUser: true), state.Snapshot());
        state.SetAuto(true);
        Assert.True(state.SwitchAutomatically("gaming"));
        Assert.Equal("gaming", state.Active);
    }

    [Fact]
    public void With_auto_off_the_saved_mode_is_restored_whoever_chose_it()
    {
        // Nothing switches with Auto off, so the saved mode is the one in force; starting in the
        // default instead would leave the daemon there with nothing to move it.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mode.json"), "{ \"active\": \"gaming\", \"source\": \"auto\", \"auto\": false }");

        var state = new ModeState(new ModeStore(_dir));

        Assert.True(state.Restored);
        Assert.Equal("gaming", state.Active);
        Assert.False(state.Auto);
    }

    [Fact]
    public void Who_chose_the_mode_in_force_is_readable()
    {
        var state = new ModeState();
        state.SwitchAutomatically("gaming");
        Assert.False(state.ChosenByUser);
        state.Active = "windows";
        Assert.True(state.ChosenByUser);
    }
}
