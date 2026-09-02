// GPD Forge — POST /jobs must not pin the machine awake. GPL-3.0-or-later.
//
// Observed on the live daemon on 2026-09-02, before the fix:
//   GET  /ai   → antiStandby { active: false, holders: 0 }
//   POST /jobs → { id: "job-1", status: "running" }
//   GET  /ai   → antiStandby { active: true,  holders: 1 }
//   ...still 1 twenty seconds later, and POST /ai/anti-standby {enable:false} returned holders: 1.
//
// There was no route back short of restarting the service, because the only thing that releases a
// hold is JobsState.Finish and nothing calls it — its own docstring said so. One request pinned
// Windows awake for the rest of the service's uptime and silently defeated the Standby Doctor, which
// is this project's flagship subsystem. A user who ran one job would find their handheld never
// sleeping again, with nothing on screen explaining it.
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GpdForge.Core.Tests;

[Collection(DaemonCollection.Name)]
public class JobHoldLeakTests(DaemonUnderTest daemon)
{
    private async Task<(bool active, int holders)> AntiStandbyAsync()
    {
        var doc = JsonDocument.Parse(await daemon.Client.GetStringAsync("/ai"));
        var a = doc.RootElement.GetProperty("antiStandby");
        return (a.GetProperty("active").GetBoolean(), a.GetProperty("holders").GetInt32());
    }

    [Fact]
    public async Task Submitting_a_job_does_not_take_a_hold_nothing_can_release()
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        var before = await AntiStandbyAsync();

        var res = await daemon.Client.PostAsJsonAsync("/jobs", new { cmd = "echo test" });
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"POST /jobs returned {(int)res.StatusCode}: {body}");

        // The job is still accepted and recorded — this fix removes the hold, not the endpoint.
        Assert.Contains("\"status\"", body);

        var after = await AntiStandbyAsync();

        Assert.Equal(before.holders, after.holders);
        Assert.False(after.active && !before.active,
            "POST /jobs engaged the anti-standby hold. Nothing in this codebase calls JobsState.Finish, " +
            "so that hold is permanent for the life of the service and the machine will never sleep.");
    }

    [Fact]
    public async Task Several_jobs_still_leave_the_holder_count_where_it_started()
    {
        // The leak compounded: each `running` job added its own id to the holding set. Three requests
        // is enough to make a counter-based regression obvious rather than borderline.
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        var before = await AntiStandbyAsync();
        for (var i = 0; i < 3; i++)
            await daemon.Client.PostAsJsonAsync("/jobs", new { cmd = $"echo {i}" });

        var after = await AntiStandbyAsync();
        Assert.Equal(before.holders, after.holders);
    }

    [Fact]
    public async Task The_manual_toggle_remains_the_only_thing_that_engages_a_hold()
    {
        // The other half: removing the leak must not remove the FEATURE. Anti-standby is a real,
        // user-facing control for local inference runs, and it has a release path — the same toggle.
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        try
        {
            await daemon.Client.PostAsJsonAsync("/ai/anti-standby", new { enable = true });
            var held = await AntiStandbyAsync();
            Assert.True(held.active, "The manual anti-standby toggle no longer engages a hold.");

            await daemon.Client.PostAsJsonAsync("/ai/anti-standby", new { enable = false });
            var released = await AntiStandbyAsync();
            Assert.False(released.active,
                "The manual toggle engaged a hold it cannot release — the same defect in a second place.");
        }
        finally
        {
            await daemon.Client.PostAsJsonAsync("/ai/anti-standby", new { enable = false });
        }
    }
}
