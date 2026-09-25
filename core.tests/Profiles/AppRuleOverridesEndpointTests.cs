// GPD Forge — /app-rules overrides and GET /profiles/active against the real daemon. GPL-3.0-or-later.
//
// The daemon under test runs in an isolated data directory with every hardware gate closed; these
// requests only touch its own app-rules.json.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GpdForge.Core.Tests;

[Collection(DaemonCollection.Name)]
public class AppRuleOverridesEndpointTests(DaemonUnderTest daemon)
{
    private static JsonElement RuleNamed(JsonElement payload, string match) =>
        payload.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("match").GetString() == match);

    private async Task<JsonElement> SendAsync(HttpMethod method, string route, object body, HttpStatusCode expect = HttpStatusCode.OK)
    {
        var res = await daemon.Client.SendAsync(new HttpRequestMessage(method, route) { Content = JsonContent.Create(body) });
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == expect, $"{method} {route} returned {(int)res.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Overrides_round_trip_and_a_put_without_them_keeps_them()
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");
        var match = "f1game" + Guid.NewGuid().ToString("N")[..6];

        var added = await SendAsync(HttpMethod.Post, "/app-rules", new
        {
            match, mode = "gaming",
            overrides = new { stapmW = 22, frameCapFps = 60, fanMode = "Aggressive", gpu = new { antiLag = true }, freeze = new[] { "Discord.exe" } },
        });
        var rule = RuleNamed(added, match);
        var o = rule.GetProperty("overrides");
        Assert.Equal(22, o.GetProperty("stapmW").GetInt32());
        Assert.Equal(60, o.GetProperty("frameCapFps").GetInt32());
        Assert.Equal("Aggressive", o.GetProperty("fanMode").GetString());
        Assert.True(o.GetProperty("gpu").GetProperty("antiLag").GetBoolean());
        Assert.Equal("discord", o.GetProperty("freeze")[0].GetString());

        // The Profiles page's enable toggle: match/mode/enabled only.
        var id = rule.GetProperty("id").GetString();
        var toggled = await SendAsync(HttpMethod.Put, $"/app-rules/{id}", new { match, mode = "gaming", enabled = false });
        Assert.Equal(22, RuleNamed(toggled, match).GetProperty("overrides").GetProperty("stapmW").GetInt32());

        var cleared = await SendAsync(HttpMethod.Put, $"/app-rules/{id}", new { match, mode = "gaming", enabled = true, overrides = (object?)null });
        Assert.Equal(JsonValueKind.Null, RuleNamed(cleared, match).GetProperty("overrides").ValueKind);

        await daemon.Client.DeleteAsync($"/app-rules/{id}");
    }

    [Theory]
    [InlineData("""{ "stapmW": 60 }""", "bad_stapm")]
    [InlineData("""{ "stapmW": "22" }""", "bad_stapm")]
    [InlineData("""{ "frameCapFps": -1 }""", "bad_frame_cap")]
    [InlineData("""{ "fanMode": "Manual" }""", "bad_fan_mode")]
    [InlineData("""{ "gpu": { "antiLag": true, "chill": true } }""", "bad_gpu")]
    [InlineData("""{ "freeze": ["C:\\evil\\x.exe"] }""", "bad_freeze")]
    public async Task Bad_overrides_are_refused_with_a_code_and_nothing_is_stored(string overrides, string code)
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");
        var match = "badf1" + Guid.NewGuid().ToString("N")[..6];
        var body = $$"""{ "match": "{{match}}", "mode": "gaming", "overrides": {{overrides}} }""";

        var res = await daemon.Client.PostAsync("/app-rules", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var error = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.String, error.GetProperty("error").ValueKind);   // the sentence the UI shows
        var all = JsonDocument.Parse(await daemon.Client.GetStringAsync("/app-rules")).RootElement;
        Assert.DoesNotContain(all.GetProperty("rules").EnumerateArray(), r => r.GetProperty("match").GetString() == match);
    }

    [Fact]
    public async Task A_plain_rule_error_is_coded_too()
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");
        var error = await SendAsync(HttpMethod.Post, "/app-rules", new { match = "x", mode = "turbo" }, HttpStatusCode.BadRequest);
        Assert.Equal("bad_rule", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Nothing_is_active_while_auto_profiles_are_off()   // the fixture runs with GPDFORGE_AUTO_PROFILES=0
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");
        var active = JsonDocument.Parse(await daemon.Client.GetStringAsync("/profiles/active")).RootElement;
        Assert.False(active.GetProperty("active").GetBoolean());
        Assert.Equal(JsonValueKind.Null, active.GetProperty("applied").ValueKind);
        Assert.Equal(0, active.GetProperty("skipped").GetArrayLength());
    }
}
