// GPD Forge — GET/POST /gpu/clock against the real daemon (hardware gate closed). GPL-3.0-or-later.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GpdForge.Core.Tests;

[Collection(DaemonCollection.Name)]
public sealed class GpuClockEndpointTests(DaemonUnderTest daemon)
{
    private static async Task<JsonElement> Body(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task Get_reports_the_band_and_automatic_without_a_backend_that_can_fix_it()
    {
        Assert.True(daemon.Started, $"Daemon did not start: {daemon.StartupLog}");

        var view = JsonDocument.Parse(await daemon.Client.GetStringAsync("/gpu/clock")).RootElement;

        Assert.False(view.GetProperty("supported").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("forcedMhz").ValueKind);
        Assert.Equal(400, view.GetProperty("minMhz").GetInt32());
        Assert.Equal(2900, view.GetProperty("maxMhz").GetInt32());
        Assert.True(view.GetProperty("resetsOnRestart").GetBoolean());
    }

    [Fact]
    public async Task Zero_is_refused_as_out_of_band_and_never_as_automatic()
    {
        Assert.True(daemon.Started, $"Daemon did not start: {daemon.StartupLog}");

        var res = await daemon.Client.PostAsJsonAsync("/gpu/clock", new { mhz = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("bad_mhz", (await Body(res)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_missing_value_is_refused()
    {
        Assert.True(daemon.Started, $"Daemon did not start: {daemon.StartupLog}");

        var res = await daemon.Client.PostAsJsonAsync("/gpu/clock", new { });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_valid_clock_without_support_says_so_and_claims_nothing()
    {
        Assert.True(daemon.Started, $"Daemon did not start: {daemon.StartupLog}");

        var res = await daemon.Client.PostAsJsonAsync("/gpu/clock", new { mhz = 1200 });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await Body(res);
        Assert.Equal("not_supported", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("forcedMhz").ValueKind);
    }
}
