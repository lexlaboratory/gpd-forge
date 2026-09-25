// GPD Forge — F1 audit round 2: a Radeon feature the driver does not offer is not "applied".
// GPL-3.0-or-later.
//
// The same rig as GameProfileTests. Begin refused GPU overrides only when the gate was closed or the
// agent reported ADLX unavailable, and recorded Anti-Lag / Chill as applied as soon as it had asked —
// though the agent already reports, per feature, whether the driver supports it at all, and
// AdlxSettings.SetEnabled returns false for an unsupported one (2026-09-25 audit).
using GpdForge.Gpu;
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed partial class GameProfileTests
{
    private static GpuAgentReport FeatureReport(bool antiLagSupported = true, bool chillSupported = true) =>
        new(true, "Ready", "1.0", "ok",
            new GpuSettingsSnapshot(
                new GpuFeatureState(antiLagSupported, false),
                new GpuFeatureState(chillSupported, false),
                null, null,
                new GpuFeatureState(Supported: true, Enabled: false, Value: 60, Min: 15, Max: 1000)),
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_feature_the_driver_does_not_support_is_skipped_and_the_other_still_applies()
    {
        // Chill on, Anti-Lag off: the pair a rule may state (the driver refuses Chill with Anti-Lag).
        var rig = Build("eldenring", EldenRing with { Gpu = new GpuOverrides(AntiLag: false, Chill: true) });
        rig.Agent.Report(FeatureReport(chillSupported: false));
        await TickAsync(rig, 3);

        Assert.False(rig.Gpu.AntiLag);
        Assert.Null(rig.Gpu.Chill);   // never asked for: the driver would refuse it and say nothing
        var p = rig.Active.Current!;
        Assert.False(p.Applied.AntiLag);
        Assert.Null(p.Applied.Chill);
        var skipped = Assert.Single(p.Skipped);
        Assert.Equal("chill", skipped.Field);
        Assert.Contains("Chill", skipped.Reason);
    }

    [Fact]
    public async Task With_every_asked_feature_unsupported_nothing_is_requested()
    {
        var rig = Build("eldenring", EldenRing with { Gpu = new GpuOverrides(AntiLag: true) });
        rig.Agent.Report(FeatureReport(antiLagSupported: false));
        await TickAsync(rig, 3);

        Assert.Null(rig.Gpu.AntiLag);
        Assert.Null(rig.Active.Current!.Applied.AntiLag);
        Assert.Equal("antiLag", Assert.Single(rig.Active.Current!.Skipped).Field);
    }

    [Fact]
    public async Task A_silent_agent_does_not_refuse_a_feature_desired_state_still_converges()
    {
        var rig = Build("eldenring");   // no report: the game auto-started at logon
        await TickAsync(rig, 3);

        Assert.True(rig.Gpu.AntiLag);
        Assert.True(rig.Active.Current!.Applied.AntiLag);
    }
}
