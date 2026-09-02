// GPD Forge - profile applier (auto-TDP + conflict guard) tests. GPL-3.0-or-later.
using GpdForge.Profiles;
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

public class ModeProfilesTests
{
    [Theory]
    [InlineData("battery")]
    [InlineData("windows")]
    [InlineData("gaming")]
    [InlineData("ai")]
    [InlineData("standby")]
    public void Every_mode_has_a_preset(string mode)
    {
        Assert.NotNull(ModeProfiles.For(mode));
    }

    [Fact]
    public void Unknown_mode_has_no_preset() => Assert.Null(ModeProfiles.For("nope"));

    [Fact]
    public void Ai_mode_is_sustained()
    {
        var p = ModeProfiles.For("ai")!.Value;
        Assert.Equal(p.StapmW, p.FastW);   // fast == stapm (flat, sustained)
        Assert.Equal(p.StapmW, p.SlowW);
    }

    [Fact]
    public void Set_clamps_to_safe_bounds()
    {
        var saved = ModeProfiles.Set("gaming", new GpdForge.Tdp.TdpProfile(999, 999, 999, 999));
        Assert.True(saved.StapmW <= 40 && saved.FastW <= 45 && saved.TctlC <= 95);
        ModeProfiles.Set("gaming", new GpdForge.Tdp.TdpProfile(25, 33, 28, 95)); // restore default
    }
}

public class ProfileApplierTests
{
    private sealed class FakeTdp(bool verified) : ITdpController
    {
        public int Calls { get; private set; }
        public Task<TdpApplyResult> ApplyAsync(TdpProfile profile, string owner, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new TdpApplyResult(profile, new TdpReadout(profile.StapmW, profile.FastW), verified, 1));
        }
    }

    private sealed class FakeDetector(bool others) : IPowerControllerDetector
    {
        public bool OthersRunning(out string[] names)
        {
            names = others ? ["GPDTool"] : [];
            return others;
        }
    }

    [Fact]
    public async Task Applies_the_mode_tdp_when_sole_controller()
    {
        var tdp = new FakeTdp(verified: true);
        var applier = new ProfileApplier(tdp, new FakeDetector(others: false));

        var outcome = await applier.ApplyAsync("gaming", CancellationToken.None);

        Assert.Equal(ApplyOutcome.AppliedVerified, outcome);
        Assert.Equal(1, tdp.Calls);
    }

    [Fact]
    public async Task Yields_and_does_not_write_when_a_rival_controller_runs()
    {
        var tdp = new FakeTdp(verified: true);
        var applier = new ProfileApplier(tdp, new FakeDetector(others: true));

        var outcome = await applier.ApplyAsync("gaming", CancellationToken.None);

        Assert.Equal(ApplyOutcome.SkippedConflict, outcome);
        Assert.Equal(0, tdp.Calls);   // never fought the other controller
    }

    [Fact]
    public async Task Reports_unverified_when_firmware_reverts()
    {
        var applier = new ProfileApplier(new FakeTdp(verified: false), new FakeDetector(others: false));
        Assert.Equal(ApplyOutcome.AppliedUnverified, await applier.ApplyAsync("windows", CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_mode_does_nothing()
    {
        var tdp = new FakeTdp(verified: true);
        var applier = new ProfileApplier(tdp, new FakeDetector(others: false));
        Assert.Equal(ApplyOutcome.UnknownMode, await applier.ApplyAsync("nope", CancellationToken.None));
        Assert.Equal(0, tdp.Calls);
    }
}
