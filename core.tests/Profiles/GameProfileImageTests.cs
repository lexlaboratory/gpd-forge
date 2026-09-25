// GPD Forge — F4: a game profile's RSR / RIS, requested, checked and put back. GPL-3.0-or-later.
//
// The same rig as GameProfileTests. RSR and RIS are values the user also sets in Adrenalin, so a
// game's are asked for once and, when the game leaves, the driver's own values from before are put
// back — unless the Display page asked for something since, or they were never read.
using GpdForge.Gpu;
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed partial class GameProfileTests
{
    private static readonly RuleOverrides RsrGame =
        new(Gpu: new GpuOverrides(Rsr: true, RsrSharpness: 80, Ris: true));

    private static GpuAgentReport ImageReport(bool rsrSupported = true, bool rsrOn = false, int rsrSharp = 50, bool risOn = false) =>
        new(true, "Ready", "1.0", "ok",
            new GpuSettingsSnapshot(null, null, null,
                new GpuFeatureState(true, risOn, 70, 10, 100), null,
                new GpuFeatureState(rsrSupported, rsrOn, rsrSharp, 0, 100)),
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_games_rsr_and_ris_are_requested_and_the_drivers_own_put_back_when_it_leaves()
    {
        var rig = Build("eldenring", RsrGame);
        rig.Agent.Report(ImageReport(rsrSharp: 40));
        await TickAsync(rig, 3);

        Assert.Equal(new GpuImageRequest(Rsr: true, RsrSharpness: 80, Ris: true), rig.Gpu.Image);
        Assert.Equal(new GpuImageRequest(Rsr: true, RsrSharpness: 80, Ris: true), rig.Active.Current!.Applied.Image);

        rig.Fg.Proc = "notepad";
        await TickAsync(rig, 3);
        Assert.Equal(new GpuImageRequest(Rsr: false, RsrSharpness: 40, Ris: false), rig.Gpu.Image);
    }

    [Fact]
    public async Task A_Display_page_change_mid_game_is_not_undone_when_the_game_leaves()
    {
        var rig = Build("eldenring", RsrGame);
        rig.Agent.Report(ImageReport());
        await TickAsync(rig, 3);

        var users = new GpuImageRequest(Rsr: true, RsrSharpness: 20);
        rig.Gpu.RequestImage(users);   // POST /gpu/image
        rig.Fg.Proc = "notepad";
        await TickAsync(rig, 3);
        Assert.Equal(users, rig.Gpu.Image);
    }

    [Fact]
    public async Task Rsr_the_driver_does_not_offer_is_skipped_and_ris_still_applies()
    {
        var rig = Build("eldenring", RsrGame);
        rig.Agent.Report(ImageReport(rsrSupported: false));
        await TickAsync(rig, 3);

        Assert.Equal(new GpuImageRequest(Ris: true), rig.Gpu.Image);
        Assert.Equal("rsr", Assert.Single(rig.Active.Current!.Skipped).Field);
    }

    [Fact]
    public async Task With_the_agent_silent_throughout_nothing_is_forced_off_when_the_game_leaves()
    {
        var rig = Build("eldenring", RsrGame);   // no report at all
        await TickAsync(rig, 3);
        Assert.NotNull(rig.Gpu.Image);           // desired state: the agent converges once it starts

        rig.Fg.Proc = "notepad";
        await TickAsync(rig, 3);
        Assert.Null(rig.Gpu.Image);              // retired, not "off": the user's own settings stand
    }

    [Fact]
    public async Task The_notice_names_rsr_and_ris_as_the_users_once_the_Display_page_changed_them()
    {
        var rig = Build("eldenring", RsrGame);
        rig.Agent.Report(ImageReport());
        await TickAsync(rig, 3);
        var p = rig.Active.Current!;
        ProfileLiveState Live() => new(null, false, rig.Gpu.CapVersion, null, () => [], rig.Agent.Last, DateTimeOffset.UtcNow, rig.Gpu.ImageVersion);

        var before = ActiveGameProfileWire.From(p, Live());
        Assert.True(before.Applied!.Gpu.Rsr);
        Assert.Equal(80, before.Applied.Gpu.RsrSharpness);

        rig.Gpu.RequestImage(new GpuImageRequest(Ris: false));
        var after = ActiveGameProfileWire.From(p, Live());
        Assert.Null(after.Applied!.Gpu.Rsr);
        Assert.Equal(["rsr", "ris"], after.Superseded);
    }

    [Fact]
    public async Task The_first_report_after_the_game_began_supplies_what_to_put_back()
    {
        var rig = Build("eldenring", RsrGame);
        await TickAsync(rig, 3);                 // agent silent at the start
        rig.Agent.Report(ImageReport(rsrOn: true, rsrSharp: 30, risOn: true));
        await TickAsync(rig, 1);

        rig.Fg.Proc = "notepad";
        await TickAsync(rig, 3);
        Assert.Equal(new GpuImageRequest(Rsr: true, RsrSharpness: 30, Ris: true), rig.Gpu.Image);
    }
}
