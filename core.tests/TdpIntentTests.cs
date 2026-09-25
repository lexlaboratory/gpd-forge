// GPD Forge — the manual TDP override and what "the profile in force" means. GPL-3.0-or-later.
//
// Until 2026-09-24 a POST /tdp was forgotten the moment anything else touched TDP: the thermal
// guardian's throttle-clear "restore" put the MODE PRESET back, so a user who had set 12 W and hit a
// hot spell came out of it at 15/20/17 W with nothing on screen saying why. The manual write also
// used Tctl 90 whatever the mode, which in `windows` (Tctl 92) lowered the thermal limit as a side
// effect of asking for fewer watts. These tests pin the override's lifetime and both fixes.
using GpdForge.Profiles;
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

public class TdpIntentTests
{
    [Fact]
    public void With_no_override_the_mode_preset_is_in_force()
    {
        var intent = new TdpIntent();
        Assert.Equal(ModeProfiles.For("windows"), intent.Resolve("windows"));
        Assert.Null(intent.Manual("windows"));
    }

    [Fact]
    public void A_manual_value_overrides_the_preset_of_the_mode_it_was_set_in()
    {
        var intent = new TdpIntent();
        var manual = new TdpProfile(12, 12, 12, 92);

        intent.SetManual("windows", manual);

        Assert.Equal(manual, intent.Resolve("windows"));
        Assert.Equal(manual, intent.Manual("windows"));
    }

    [Fact]
    public void The_override_matches_the_mode_name_as_the_preset_table_does_ignoring_case()
    {
        var intent = new TdpIntent();
        var manual = new TdpProfile(12, 12, 12, 92);
        intent.SetManual("Windows", manual);
        Assert.Equal(manual, intent.Resolve("windows"));
    }

    [Fact]
    public void The_override_does_not_follow_the_user_into_another_mode()
    {
        // The override belongs to the mode it was set in. Resolving a different mode returns that
        // mode's preset even if nothing has cleared the override yet — so a mode switch that forgot
        // to go through ProfileApplier still cannot carry 12 W into `gaming`.
        var intent = new TdpIntent();
        intent.SetManual("windows", new TdpProfile(12, 12, 12, 92));

        Assert.Equal(ModeProfiles.For("gaming"), intent.Resolve("gaming"));
        Assert.Null(intent.Manual("gaming"));
    }

    [Fact]
    public void Clearing_the_override_hands_the_mode_back_to_its_preset()
    {
        var intent = new TdpIntent();
        intent.SetManual("windows", new TdpProfile(12, 12, 12, 92));

        intent.ClearManual();

        Assert.Equal(ModeProfiles.For("windows"), intent.Resolve("windows"));
    }

    [Fact]
    public void An_unknown_mode_with_no_override_resolves_to_nothing()
    {
        Assert.Null(new TdpIntent().Resolve("nope"));
    }

    [Fact]
    public void A_manual_profile_is_flat_and_keeps_the_active_modes_thermal_limit()
    {
        // Was `new TdpProfile(w, w, w, 90)` in Program.cs: in `windows` (Tctl 92) asking for fewer
        // watts also lowered the thermal limit by 2 °C, and in `gaming` (95) by 5.
        var windows = TdpIntent.ManualProfile(12, "windows");
        Assert.Equal(new TdpProfile(12, 12, 12, ModeProfiles.For("windows")!.Value.TctlC), windows);

        var gaming = TdpIntent.ManualProfile(20, "gaming");
        Assert.Equal(ModeProfiles.For("gaming")!.Value.TctlC, gaming.TctlC);
    }

    [Fact]
    public void A_manual_profile_under_an_unknown_mode_falls_back_to_the_old_limit()
    {
        Assert.Equal(TdpIntent.FallbackTctlC, TdpIntent.ManualProfile(12, "nope").TctlC);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(25, true)]
    [InlineData(40, true)]
    [InlineData(41, false)]
    [InlineData(0, false)]
    [InlineData(-3, false)]
    public void The_manual_band_is_the_preset_tables_stapm_band(int w, bool ok)
    {
        // Same 5–40 W as ModeProfiles.Set. It matters more now than it did: an override is
        // re-asserted every 30 s, so an out-of-band value would not be one bad write but a standing one.
        Assert.Equal(ok, TdpIntent.IsManualInRange(w));
    }
}

public class ProfileApplierOverrideTests
{
    private sealed class FakeTdp : ITdpController
    {
        public List<(TdpProfile Profile, string Owner)> Calls { get; } = [];
        public Task<TdpApplyResult> ApplyAsync(TdpProfile profile, string owner, CancellationToken ct)
        {
            Calls.Add((profile, owner));
            return Task.FromResult(new TdpApplyResult(profile, new TdpReadout(profile.StapmW, profile.FastW), true, 1));
        }
    }

    private sealed class FakeDetector(bool others) : IPowerControllerDetector
    {
        public bool OthersRunning(out string[] names)
        {
            names = others ? ["MotionAssistant"] : [];
            return others;
        }
    }

    [Fact]
    public async Task Applying_a_mode_ends_the_manual_override()
    {
        var intent = new TdpIntent();
        intent.SetManual("windows", new TdpProfile(12, 12, 12, 92));
        var tdp = new FakeTdp();

        await new ProfileApplier(tdp, new FakeDetector(false), intent: intent).ApplyAsync("windows", CancellationToken.None);

        // Re-selecting the SAME mode counts: picking a mode is asking for its preset.
        Assert.Null(intent.Manual("windows"));
        Assert.Equal(ModeProfiles.For("windows"), tdp.Calls.Single().Profile);
        Assert.Equal(TdpOwner.Mode, tdp.Calls.Single().Owner);
    }

    [Fact]
    public async Task A_mode_change_ends_the_override_even_when_it_yields_to_a_rival()
    {
        // The mode changed whether or not GPD Forge got to write it; an override left behind would
        // resurface the moment the rival exits and the reassert or a restore picked it up.
        var intent = new TdpIntent();
        intent.SetManual("windows", new TdpProfile(12, 12, 12, 92));
        var tdp = new FakeTdp();

        var outcome = await new ProfileApplier(tdp, new FakeDetector(true), intent: intent)
            .ApplyAsync("windows", CancellationToken.None);

        Assert.Equal(ApplyOutcome.SkippedConflict, outcome);
        Assert.Null(intent.Manual("windows"));
        Assert.Empty(tdp.Calls);
    }
}
