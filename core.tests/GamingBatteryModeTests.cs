// GPD Forge — the gaming-on-battery mode, end to end against the real daemon. GPL-3.0-or-later.
//
// The mode's TDP preset is the small half of it. The large half is the frame cap: an uncapped game
// converts every watt it is allowed into frames nobody sees, and this panel reports 60 Hz with no
// other supported mode. Capping stops the work at the source, so the SoC clocks down on its own and
// the TDP ceiling never comes into play.
//
// Which makes the interaction with auto-FPS the thing that has to be right. A cap BELOW an active
// target is the one pathological pairing — the governor climbs forever chasing frames the driver is
// withholding, hot and loud, with no error raised anywhere. The endpoints already refuse it. What
// these tests cover is the back door: arriving at that state through a MODE SWITCH rather than
// through the endpoint that guards it.
using System.Net.Http.Json;
using System.Text.Json;
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

[Collection(DaemonCollection.Name)]
public class GamingBatteryModeTests(DaemonUnderTest daemon)
{
    private async Task<JsonElement> PostAsync(string route, object body)
    {
        var res = await daemon.Client.PostAsJsonAsync(route, body);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"POST {route} returned {(int)res.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task The_catalogue_and_the_daemon_agree_on_the_preset()
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        var profiles = JsonDocument.Parse(await daemon.Client.GetStringAsync("/profiles")).RootElement;
        var preset = profiles.GetProperty(ModeCatalogue.GamingBattery);

        // 15 W sustained, not 25. On the reference device the pack holds 40 Wh and the system draws
        // ~9 W before the SoC does anything, so this lands near 24 W total — about 1.6 h against
        // roughly 1.1 h for `gaming`.
        Assert.Equal(15, preset.GetProperty("stapmW").GetInt32());

        // 90 rather than 95 on purpose: a lower thermal ceiling means the fan spins less, and the
        // fan is part of that ~9 W of overhead.
        Assert.Equal(90, preset.GetProperty("tctlC").GetInt32());

        // Fast/slow keep headroom, unlike the sustained AI preset which is deliberately flattened.
        // A throttled shader-compile spike costs a visible hitch and saves nothing over a session.
        Assert.True(preset.GetProperty("fastW").GetInt32() > preset.GetProperty("stapmW").GetInt32(),
            "gaming-battery should keep boost headroom; only the sustained mode is flattened.");
    }

    [Fact]
    public async Task Selecting_the_mode_requests_its_frame_cap()
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        // Auto-FPS off, so there is nothing for the cap to conflict with.
        await PostAsync("/auto-fps", new { enable = false, targetFps = 60 });

        var result = await PostAsync("/mode", new { name = ModeCatalogue.GamingBattery });

        Assert.Equal(ModeCatalogue.GamingBattery, result.GetProperty("active").GetString());
        Assert.Equal("requested 45 FPS", result.GetProperty("frameCap").GetString());

        // Requested as DESIRED STATE, not as a command: the daemon cannot reach ADLX from session 0
        // (docs/adr/0002), so the user-session agent reconciles it. GET /gpu/desired is what the
        // agent reads, so this is the assertion that the request actually landed somewhere.
        var desired = JsonDocument.Parse(await daemon.Client.GetStringAsync("/gpu/desired")).RootElement;
        Assert.True(desired.GetProperty("requested").GetBoolean());
        Assert.Equal(45, desired.GetProperty("frameCapFps").GetInt32());
    }

    [Fact]
    public async Task A_mode_with_no_recommended_cap_leaves_the_users_cap_alone()
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        // Switching to a mode that has no opinion about frame rate must not silently clear a cap the
        // user set for themselves — quietly removing someone's setting is the same class of mistake
        // as quietly applying one.
        var result = await PostAsync("/mode", new { name = ModeCatalogue.Windows });

        Assert.Equal(ModeCatalogue.Windows, result.GetProperty("active").GetString());
        Assert.True(result.GetProperty("frameCap").ValueKind is JsonValueKind.Null,
            "A mode with no recommended cap should report no cap action at all.");
    }

    [Fact]
    public async Task The_mode_refuses_its_own_cap_rather_than_creating_the_pathological_pairing()
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        // The user is running auto-FPS at 60. The mode wants a 45 FPS cap. Applying it would put the
        // governor into the state where it raises power forever chasing frames the driver is holding
        // back — reached not through POST /gpu/frame-cap, which refuses it, but sideways through a
        // mode switch.
        await PostAsync("/auto-fps", new { enable = true, targetFps = 60 });
        try
        {
            var result = await PostAsync("/mode", new { name = ModeCatalogue.GamingBattery });

            // The MODE still applies — refusing the whole switch would be a worse answer than
            // applying the part that is safe.
            Assert.Equal(ModeCatalogue.GamingBattery, result.GetProperty("active").GetString());

            var cap = result.GetProperty("frameCap").GetString();
            Assert.NotNull(cap);
            Assert.StartsWith("not applied", cap);

            // And it names both numbers, so the user can tell which of the two settings to change.
            Assert.Contains("45", cap);
            Assert.Contains("60", cap);
        }
        finally
        {
            await PostAsync("/auto-fps", new { enable = false, targetFps = 60 });
        }
    }

    [Fact]
    public void The_mode_does_not_run_the_auto_FPS_governor_by_itself()
    {
        // Stated where the decision lives rather than only in the catalogue's own test: this mode's
        // strategy is a cap, and a cap plus a target is the pairing that has to be refused. Wanting
        // both is a coherent thing for a USER to ask for once, and an incoherent thing to ship as a
        // default.
        Assert.False(ModeCatalogue.AutoFpsEligible(ModeCatalogue.GamingBattery));
        Assert.True(ModeCatalogue.AutoFpsEligible(ModeCatalogue.Gaming));
    }
}
