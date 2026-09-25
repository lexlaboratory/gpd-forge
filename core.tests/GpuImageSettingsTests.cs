// GPD Forge — RSR / RIS as desired state: write order, validation, once-per-request. GPL-3.0-or-later.
//
// F4. ADLX itself runs in the user's session and cannot be unit-tested, so the rules the agent follows
// are pure (GpuImagePlan, GpuImageReconciler, GpuImageRequest) and pinned here: Boost off before RSR
// on, enabled before sharpness, no sharpness after "off", and a request carried out once per version.
using System.Text.Json;
using GpdForge.Gpu;
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

public class GpuImageSettingsTests
{
    private static GpuImageStepKind[] Kinds(GpuImageRequest r) => GpuImagePlan.Steps(r).Select(s => s.Kind).ToArray();

    [Fact]
    public void Rsr_on_turns_Boost_off_first_then_enables_then_sets_sharpness()
    {
        var steps = GpuImagePlan.Steps(new GpuImageRequest(Rsr: true, RsrSharpness: 75));
        Assert.Equal([GpuImageStepKind.BoostOff, GpuImageStepKind.RsrEnabled, GpuImageStepKind.RsrSharpness], steps.Select(s => s.Kind));
        Assert.Equal(1, steps[1].Value);
        Assert.Equal(75, steps[2].Value);
        Assert.Equal(["boost", "rsr", "rsrSharpness"], steps.Select(s => s.Field));
    }

    [Fact]
    public void Turning_a_feature_off_writes_no_sharpness_and_leaves_Boost_alone()
    {
        Assert.Equal([GpuImageStepKind.RsrEnabled], Kinds(new GpuImageRequest(Rsr: false, RsrSharpness: 40)));
        Assert.Equal([GpuImageStepKind.RisEnabled], Kinds(new GpuImageRequest(Ris: false, RisSharpness: 40)));
    }

    [Fact]
    public void Ris_is_enabled_before_its_sharpness_and_a_bare_sharpness_is_written_alone()
    {
        Assert.Equal([GpuImageStepKind.RisEnabled, GpuImageStepKind.RisSharpness], Kinds(new GpuImageRequest(Ris: true, RisSharpness: 80)));
        Assert.Equal([GpuImageStepKind.RisSharpness], Kinds(new GpuImageRequest(RisSharpness: 80)));
    }

    [Fact]
    public void Nothing_asked_means_nothing_written()
    {
        Assert.Empty(GpuImagePlan.Steps(new GpuImageRequest()));
        Assert.Empty(GpuImagePlan.Steps(null));
    }

    [Theory]
    [InlineData(-1, null, null, false)]
    [InlineData(101, null, null, false)]
    [InlineData(0, null, null, true)]
    [InlineData(100, null, null, true)]
    [InlineData(5, 10, 100, false)]   // below a driver range narrower than 0–100
    [InlineData(10, 10, 100, true)]
    public void Sharpness_is_checked_against_0_to_100_and_the_driver_range(int value, int? min, int? max, bool ok)
    {
        Assert.Equal(ok, GpuImageRequest.Reject(value, min, max, "RIS") is null);
    }

    [Fact]
    public void Each_request_is_carried_out_once_and_a_new_one_even_with_the_same_values()
    {
        var r = new GpuImageReconciler();
        var img = new GpuImageRequest(Rsr: true);
        Assert.True(r.ShouldApply(img, 1));
        r.Handled(1);
        Assert.False(r.ShouldApply(img, 1));   // not re-asserted over the user's Adrenalin change
        Assert.True(r.ShouldApply(img, 2));
        Assert.False(r.ShouldApply(null, 3));   // a retired request writes nothing
        Assert.False(r.ShouldApply(new GpuImageRequest(), 3));
    }

    [Fact]
    public void Desired_state_carries_the_image_request_and_an_older_daemon_carries_none()
    {
        var d = DesiredGpuState.Parse("""{"requested":false,"image":{"rsr":true,"rsrSharpness":60,"ris":null},"imageVersion":4}""")!;
        Assert.Equal(new GpuImageRequest(Rsr: true, RsrSharpness: 60), d.Image);
        Assert.Equal(4, d.ImageVersion);

        var old = DesiredGpuState.Parse("""{"requested":false}""")!;
        Assert.Null(old.Image);
        Assert.Null(old.ImageVersion);
    }

    [Fact]
    public void Requesting_moves_the_version_and_an_empty_request_retires_the_last()
    {
        var gpu = new GpuDesiredState();
        gpu.RequestImage(new GpuImageRequest(Ris: true));
        Assert.Equal(1, gpu.ImageVersion);
        gpu.RequestImage(new GpuImageRequest());
        Assert.Null(gpu.Image);
        Assert.Equal(2, gpu.ImageVersion);
    }

    [Fact]
    public void Previous_values_cover_only_what_the_request_touches_and_need_a_supported_reading()
    {
        var asked = new GpuImageRequest(Rsr: true, RsrSharpness: 80);
        var rsr = new GpuFeatureState(true, false, 50, 0, 100);
        Assert.Equal(new GpuImageRequest(Rsr: false, RsrSharpness: 50), asked.PreviousFrom(rsr, null));
        Assert.Null(asked.PreviousFrom(null, null));   // never read: nothing honest to restore
    }

    private static RuleOverridesJson.Result Parse(string json) =>
        RuleOverridesJson.Parse(JsonDocument.Parse(json).RootElement.GetProperty("overrides"));

    [Fact]
    public void A_game_profile_carries_rsr_and_ris_and_refuses_a_bad_sharpness()
    {
        var ok = Parse("""{"overrides":{"gpu":{"rsr":true,"rsrSharpness":70,"ris":false}}}""");
        Assert.Null(ok.Error);
        Assert.Equal(new GpuOverrides(Rsr: true, RsrSharpness: 70, Ris: false), ok.Value!.Gpu);

        Assert.Equal("bad_gpu", Parse("""{"overrides":{"gpu":{"risSharpness":150}}}""").Error!.Code);
        Assert.Equal("bad_gpu", Parse("""{"overrides":{"gpu":{"rsr":"on"}}}""").Error!.Code);
    }

    [Fact]
    public void A_hand_edited_sharpness_is_clamped_on_load()
    {
        var o = RuleOverridesPolicy.Sanitize(new RuleOverrides(Gpu: new GpuOverrides(Rsr: true, RsrSharpness: 400)));
        Assert.Equal(100, o!.Gpu!.RsrSharpness);
    }
}
