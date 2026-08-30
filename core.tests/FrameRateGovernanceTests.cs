// GPD Forge — the rule for two features that both govern frame rate. GPL-3.0-or-later.
using GpdForge.Gpu;
using Xunit;

namespace GpdForge.Core.Tests;

public class FrameRateGovernanceTests
{
    [Fact]
    public void A_cap_below_an_active_target_is_refused()
    {
        // The pathological pair: auto-FPS raises power chasing 60 while the driver holds frames at
        // 30. Nothing errors, the machine just runs hot for no extra frames.
        var why = FrameRateGovernance.Conflict(autoFpsEnabled: true, autoFpsTarget: 60, frameCap: 30);

        Assert.NotNull(why);
        // Both numbers are named, because "these settings conflict" leaves the user guessing which
        // two and by how much.
        Assert.Contains("30", why);
        Assert.Contains("60", why);
    }

    [Fact]
    public void A_cap_above_the_target_is_allowed_because_it_is_useful()
    {
        // "Aim for 45, never spike past 60" is a sensible thing to want on a handheld, and the two
        // features cooperate rather than fight.
        Assert.Null(FrameRateGovernance.Conflict(true, 45, 60));
    }

    [Fact]
    public void A_cap_equal_to_the_target_is_allowed()
        => Assert.Null(FrameRateGovernance.Conflict(true, 60, 60));

    [Fact]
    public void A_disabled_auto_fps_never_blocks_a_cap()
    {
        // Its target is not governing anything. Refusing on a stale 120 would enforce a number with
        // no effect on the machine.
        Assert.Null(FrameRateGovernance.Conflict(autoFpsEnabled: false, autoFpsTarget: 120, frameCap: 30));
    }

    [Fact]
    public void No_cap_never_conflicts()
        => Assert.Null(FrameRateGovernance.Conflict(true, 60, null));

    [Fact]
    public void A_fractional_target_is_compared_without_truncation()
    {
        // The controller carries a double. Truncating 59.5 to 59 would quietly permit a 59 cap that
        // still sits below the real target.
        Assert.NotNull(FrameRateGovernance.Conflict(true, 59.5, 59));
        Assert.Null(FrameRateGovernance.Conflict(true, 59.5, 60));
    }
}
