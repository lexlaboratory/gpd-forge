// GPD Forge — Auto vs a mode the user picked, over the API. GPL-3.0-or-later.
//
// 2026-10-10 on this device: the user picked windows (12 W); auto-profiles later switched to gaming at
// 25 W because Orca came to the front, and the UI kept saying "Manual: Windows" — its Auto switch lived
// only in the browser, and in manual it stopped asking the daemon. Auto is the daemon's now, GET /mode
// reports it, and says which rule chose the mode when one did.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GpdForge.Api;
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

public class ModeViewTests
{
    private static readonly Guid RuleId = Guid.NewGuid();

    private static AppRuleMatch Match(string mode, Guid? ruleId) =>
        new(ruleId, ruleId is null ? null : "orca", mode, "Orca", true, DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_rule_that_switched_the_mode_is_named()
    {
        var mode = new ModeState();
        mode.SwitchAutomatically("gaming");

        var view = ModeView.From(mode, Match("gaming", RuleId));

        Assert.Equal("gaming", view.Active);
        Assert.True(view.Auto);
        Assert.Equal(new ModeDecision(RuleId, "orca", "Orca"), view.DecidedBy);
    }

    [Fact]
    public void A_mode_the_user_picked_names_no_rule()
    {
        var mode = new ModeState { Active = "gaming" };
        Assert.Null(ModeView.From(mode, Match("gaming", RuleId)).DecidedBy);
    }

    [Fact]
    public void A_rule_that_wants_another_mode_did_not_choose_this_one()
    {
        var mode = new ModeState();
        mode.SwitchAutomatically("windows");
        Assert.Null(ModeView.From(mode, Match("gaming", RuleId)).DecidedBy);
    }

    [Fact]
    public void The_power_default_is_not_a_rule()
    {
        var mode = new ModeState();
        mode.SwitchAutomatically("windows");
        Assert.Null(ModeView.From(mode, Match("windows", ruleId: null)).DecidedBy);
    }

    [Fact]
    public void With_auto_off_no_rule_decides()
    {
        var mode = new ModeState();
        mode.SwitchAutomatically("gaming");
        mode.SetAuto(false);

        var view = ModeView.From(mode, Match("gaming", RuleId));

        Assert.False(view.Auto);
        Assert.Null(view.DecidedBy);
    }
}

[Collection(DaemonCollection.Name)]
public sealed class ModeAutoEndpointTests(DaemonUnderTest daemon)
{
    private async Task<JsonElement> PostOkAsync(string route, object body)
    {
        var res = await daemon.Client.PostAsJsonAsync(route, body);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"POST {route} returned {(int)res.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<JsonElement> GetModeAsync() =>
        JsonDocument.Parse(await daemon.Client.GetStringAsync("/mode")).RootElement.Clone();

    [Fact]
    public async Task Picking_a_mode_turns_auto_off_until_it_is_turned_back_on()
    {
        Assert.True(daemon.Started, $"Daemon did not start: {daemon.StartupLog}");
        try
        {
            await PostOkAsync("/mode", new { name = ModeCatalogue.Windows });

            var picked = await GetModeAsync();
            Assert.Equal(ModeCatalogue.Windows, picked.GetProperty("active").GetString());
            Assert.False(picked.GetProperty("auto").GetBoolean());
            Assert.Equal(JsonValueKind.Null, picked.GetProperty("decidedBy").ValueKind);

            var resumed = await PostOkAsync("/mode/auto", new { enabled = true });
            Assert.True(resumed.GetProperty("auto").GetBoolean());
            Assert.True((await GetModeAsync()).GetProperty("auto").GetBoolean());
        }
        finally
        {
            await daemon.Client.PostAsJsonAsync("/mode/auto", new { enabled = true });
        }
    }

    [Fact]
    public async Task Auto_takes_a_boolean()
    {
        Assert.True(daemon.Started, $"Daemon did not start: {daemon.StartupLog}");
        var res = await daemon.Client.PostAsJsonAsync("/mode/auto", new { });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
