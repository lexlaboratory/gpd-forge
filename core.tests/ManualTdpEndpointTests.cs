// GPD Forge — POST /tdp and the startup apply, against the real daemon. GPL-3.0-or-later.
//
// The daemon under test runs with every hardware gate closed, so these writes land on the stub
// backend (GET /tdp says `backend: "stub"`); nothing here reaches an SMU.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

[Collection(DaemonCollection.Name)]
public class ManualTdpEndpointTests(DaemonUnderTest daemon)
{
    private async Task<JsonElement> GetAsync(string route) =>
        JsonDocument.Parse(await daemon.Client.GetStringAsync(route)).RootElement.Clone();

    private async Task PostOkAsync(string route, object body)
    {
        var res = await daemon.Client.PostAsJsonAsync(route, body);
        Assert.True(res.IsSuccessStatusCode, $"POST {route} returned {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(41)]
    [InlineData(500)]
    public async Task An_out_of_band_value_is_refused_and_nothing_is_written(int stapmW)
    {
        // docs/api.md has said "400 if stapmW is out of the safe band" since the endpoint existed;
        // the mock daemon did it, the real one handed any number straight to ryzenadj. It matters
        // more now: a manual value is remembered and re-asserted every 30 s.
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");
        int before = (await GetAsync("/audit?limit=500")).GetProperty("total").GetInt32();

        var res = await daemon.Client.PostAsJsonAsync("/tdp", new { stapmW });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var error = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("bad_tdp", error.GetProperty("code").GetString());
        Assert.Equal(before, (await GetAsync("/audit?limit=500")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_manual_write_keeps_the_active_modes_thermal_limit()
    {
        // Was a hardcoded Tctl 90: in `windows` (92) asking for fewer watts lowered the thermal limit.
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");
        await PostOkAsync("/mode", new { name = "windows" });

        await PostOkAsync("/tdp", new { stapmW = 12 });

        var tdp = await GetAsync("/tdp");
        Assert.Equal("manual", tdp.GetProperty("owner").GetString());
        Assert.Equal(12, tdp.GetProperty("stapmW").GetInt32());

        var newest = (await GetAsync("/audit?limit=1")).GetProperty("writes")[0].GetProperty("detail").GetString();
        int tctl = ModeProfiles.For("windows")!.Value.TctlC;
        Assert.StartsWith($"[manual] stapm=12W fast=12W slow=12W tctl={tctl}C", newest);
    }

    [Fact]
    public async Task The_daemon_applies_the_active_mode_when_it_starts()
    {
        // Logged whichever way it goes: applied, or yielded because MotionAssistant / GPD Tool runs on
        // the machine executing this suite. Either line proves the startup apply ran.
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!daemon.StartupLog.Contains("Startup TDP") && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Assert.Contains("Startup TDP", daemon.StartupLog);
    }
}
