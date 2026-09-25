// GPD Forge — /advisor/* against the real daemon (plan F3). GPL-3.0-or-later.
//
// The daemon under test has no sessions and no learned ceiling, so it has nothing to suggest: these
// check the wiring and the refusals. The rules are AdvisorRulesTests; Apply itself is AdvisorServiceTests.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GpdForge.Core.Tests.Advisor;

[Collection(DaemonCollection.Name)]
public class AdvisorEndpointTests(DaemonUnderTest daemon)
{
    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string route, object body)
    {
        var res = await daemon.Client.PostAsJsonAsync(route, body);
        return (res.StatusCode, JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    [Fact]
    public async Task A_game_with_no_evidence_gets_no_suggestions()
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");
        var body = await daemon.Client.GetFromJsonAsync<JsonElement>("/advisor/suggestions?game=EldenRing.exe");
        Assert.Equal("eldenring", body.GetProperty("game").GetString());
        Assert.False(body.GetProperty("live").GetBoolean());
        Assert.Equal(0, body.GetProperty("suggestions").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("applied").ValueKind);
    }

    [Fact]
    public async Task Apply_refuses_a_malformed_or_stale_id_and_writes_nothing()
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");
        var before = await daemon.Client.GetStringAsync("/app-rules");

        var bad = await PostAsync("/advisor/apply", new { id = "nonsense" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal("bad_id", bad.Body.GetProperty("code").GetString());

        var stale = await PostAsync("/advisor/apply", new { id = "cap_refresh:advisorgame:60" });
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("stale_suggestion", stale.Body.GetProperty("code").GetString());

        Assert.Equal(before, await daemon.Client.GetStringAsync("/app-rules"));
    }

    [Fact]
    public async Task Dismiss_answers_with_the_games_state()
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");
        var ok = await PostAsync("/advisor/dismiss", new { id = "stapm_ceiling:advisorgame:22" });
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal("advisorgame", ok.Body.GetProperty("game").GetString());

        var bad = await PostAsync("/advisor/dismiss", new { id = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
    }
}
