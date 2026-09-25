// GPD Forge — POST/GET /session/foreground against the real daemon. GPL-3.0-or-later.
//
// The installed service is in session 0 and cannot see the foreground window; the user-session agent
// reports it here (audit, 2026-09-24). These pin the endpoint's validation and that the report is what
// the daemon then answers with.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GpdForge.Core.Tests;

[Collection(DaemonCollection.Name)]
public class SessionForegroundEndpointTests(DaemonUnderTest daemon)
{
    private async Task<JsonElement> GetAsync(string route) =>
        JsonDocument.Parse(await daemon.Client.GetStringAsync(route)).RootElement.Clone();

    [Fact]
    public async Task A_reported_foreground_is_what_the_daemon_answers_with_and_says_where_it_came_from()
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        var res = await daemon.Client.PostAsJsonAsync("/session/foreground", new { process = "eldenring" });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());

        var fg = await GetAsync("/session/foreground");
        Assert.Equal("eldenring", fg.GetProperty("process").GetString());
        Assert.Equal("agent", fg.GetProperty("source").GetString());
        Assert.True(fg.GetProperty("ageMs").GetInt64() >= 0);
    }

    [Fact]
    public async Task Nothing_in_front_is_a_valid_report()
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        var res = await daemon.Client.PostAsJsonAsync("/session/foreground", new { process = (string?)null });

        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"C:\Games\eldenring.exe")]
    [InlineData("a\u0001b")]
    public async Task Anything_but_a_bare_process_name_is_refused(string process)
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        var res = await daemon.Client.PostAsJsonAsync("/session/foreground", new { process });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var error = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("bad_process", error.GetProperty("code").GetString());
    }
}
