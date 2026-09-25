// GPD Forge — Forge Advisor dismiss/apply tests (plan F3). GPL-3.0-or-later.
using GpdForge.Advisor;
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests.Advisor;

public class AdvisorServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpdforge-advisor-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static AdvisorSuggestion Cap(string game, int fps) =>
        new($"cap_refresh:{game}:{fps}", game, "cap_refresh", "t", "d", null, fps);
    private static AdvisorSuggestion Stapm(string game, int w) =>
        new($"stapm_ceiling:{game}:{w}", game, "stapm_ceiling", "t", "d", w, null);

    [Fact]
    public void Dismissed_suggestions_stay_hidden_across_restarts()
    {
        var svc = new AdvisorService(_dir);
        svc.Dismiss("cap_refresh:game:60");
        var visible = new AdvisorService(_dir).Visible([Cap("game", 60), Cap("game", 120)]);
        Assert.Equal(["cap_refresh:game:120"], visible.Select(s => s.Id));
    }

    [Fact]
    public void Apply_creates_the_games_rule_ahead_of_a_broader_one()
    {
        var rules = new AppRuleStore(_dir, seedDefaults: false);
        rules.Add("elden", ModeCatalogue.Gaming);
        var svc = new AdvisorService(_dir);

        var entry = svc.Apply(Cap("eldenring", 60), rules, Now);

        var own = rules.RuleFor("eldenring.exe")!;
        Assert.Equal("eldenring", own.Match);
        Assert.Equal(ModeCatalogue.Gaming, own.Mode);
        Assert.Equal(60, own.Overrides!.FrameCapFps);
        Assert.Equal("cap_refresh:eldenring:60", entry.Id);
        Assert.Single(new AdvisorService(_dir).Applied);
    }

    [Fact]
    public void Apply_merges_into_an_existing_profile_and_keeps_the_rest()
    {
        var rules = new AppRuleStore(_dir, seedDefaults: false);
        var existing = rules.Add("eldenring", ModeCatalogue.Gaming, enabled: false, new RuleOverrides(FrameCapFps: 60, FanMode: "Aggressive"));
        var svc = new AdvisorService(_dir);

        svc.Apply(Stapm("eldenring", 22), rules, Now);

        var rule = rules.List().Single(r => r.Id == existing.Id);
        Assert.True(rule.Enabled);
        Assert.Equal(new RuleOverrides(StapmW: 22, FrameCapFps: 60, FanMode: "Aggressive"), rule.Overrides);
    }

    [Fact]
    public void A_hint_cannot_be_applied()
    {
        var rules = new AppRuleStore(_dir, seedDefaults: false);
        var hint = new AdvisorSuggestion("lower_resolution:g", "g", "lower_resolution", "t", "d", null, null);
        Assert.Throws<InvalidOperationException>(() => new AdvisorService(_dir).Apply(hint, rules, Now));
        Assert.Empty(rules.List());
    }

    [Fact]
    public void Applied_log_is_bounded()
    {
        var rules = new AppRuleStore(_dir, seedDefaults: false);
        var svc = new AdvisorService(_dir);
        for (var i = 0; i < AdvisorService.MaxApplied + 5; i++) svc.Apply(Cap("g", 30 + 10 * (i % 2)), rules, Now.AddSeconds(i));
        Assert.Equal(AdvisorService.MaxApplied, svc.Applied.Count);
        Assert.Equal(Now.AddSeconds(AdvisorService.MaxApplied + 4), svc.Applied[0].AtUtc);
    }
}
