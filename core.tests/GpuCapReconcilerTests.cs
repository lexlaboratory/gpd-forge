// GPD Forge — when the GPU agent carries out a frame-cap request. GPL-3.0-or-later.
//
// F1 audit round 4 (2026-09-25). The agent wrote FRTC only when the requested VALUE differed from the
// last one it wrote. Measured on the device the same day: GET /gpu/desired {requested:true,
// frameCapFps:60, requestedAtUtc:2026-09-24T23:21Z} against GET /gpu FRTC enabled at 45 — the agent had
// applied 60 once, and the user then set 45 in Adrenalin. A game whose profile says 60 asked for 60
// again, the agent saw 60 == 60 and wrote nothing, and the game ran the whole session at 45 under a
// notice blaming the driver. A NEW request is carried out whatever its value; an OLD one is not
// re-asserted over the user's drift.
using GpdForge.Gpu;
using Xunit;

namespace GpdForge.Core.Tests;

public class GpuCapReconcilerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 23, 21, 2, TimeSpan.Zero);

    private static DesiredGpuState Want(int? fps, long version, DateTimeOffset at) =>
        new(true, fps, null, null, version, at);

    [Fact]
    public void A_new_request_for_the_value_last_applied_is_carried_out_after_the_driver_drifted()
    {
        var r = new GpuCapReconciler();
        var old = Want(60, 1, T0);
        Assert.True(r.ShouldApply(old));
        r.Handled(old);

        // The user sets 45 in Adrenalin; nothing new is asked, so the agent leaves it alone...
        Assert.False(r.ShouldApply(old));

        // ...until a game profile asks for 60 again, fourteen hours later.
        Assert.True(r.ShouldApply(Want(60, 2, T0.AddHours(14))));
    }

    [Fact]
    public void A_new_request_to_turn_the_cap_off_is_carried_out_even_when_off_was_the_last_one()
    {
        var r = new GpuCapReconciler();
        r.Handled(Want(null, 3, T0));
        Assert.True(r.ShouldApply(Want(null, 4, T0.AddMinutes(5))));
    }

    [Fact]
    public void A_refused_request_is_not_retried_every_tick()
    {
        // Handled is recorded whether or not the driver took it: GET /gpu shows the truth, and a cap
        // the driver refuses must not be re-sent every three seconds forever.
        var r = new GpuCapReconciler();
        var ask = Want(500, 7, T0);
        r.Handled(ask);
        Assert.False(r.ShouldApply(ask));
        Assert.False(r.ShouldApply(ask with { }));
    }

    [Fact]
    public void Nothing_requested_is_never_applied()
    {
        Assert.False(new GpuCapReconciler().ShouldApply(new DesiredGpuState(false, null, null, null, 0, null)));
    }

    [Fact]
    public void A_daemon_restart_with_the_same_version_number_is_still_a_new_request()
    {
        // CapVersion lives in memory and restarts at 0, so a version alone can repeat; the timestamp
        // tells the two requests apart.
        var r = new GpuCapReconciler();
        r.Handled(Want(60, 1, T0));
        Assert.True(r.ShouldApply(Want(60, 1, T0.AddHours(2))));
    }

    [Fact]
    public void A_daemon_older_than_the_version_field_falls_back_to_the_value()
    {
        var r = new GpuCapReconciler();
        var legacy = new DesiredGpuState(true, 60, null, null, null, null);
        Assert.True(r.ShouldApply(legacy));
        r.Handled(legacy);
        Assert.False(r.ShouldApply(legacy));
        Assert.True(r.ShouldApply(legacy with { FrameCapFps = 45 }));
    }

    [Fact]
    public void The_desired_body_carries_the_request_identity()
    {
        var d = DesiredGpuState.Parse("""
            {"requested":true,"frameCapFps":60,"requestedAtUtc":"2026-09-24T23:21:02+00:00","capVersion":5,"antiLag":true,"chill":null}
            """);
        Assert.NotNull(d);
        Assert.True(d!.Requested);
        Assert.Equal(60, d.FrameCapFps);
        Assert.Equal(5, d.CapVersion);
        Assert.Equal(T0, d.RequestedAtUtc);
        Assert.True(d.AntiLag);
        Assert.Null(d.Chill);
    }

    [Theory]
    [InlineData("<!doctype html>")]
    [InlineData("{}")]
    public void An_unreadable_desired_body_is_null_never_no_cap(string body)
    {
        Assert.Null(DesiredGpuState.Parse(body));
    }

    [Fact]
    public void A_body_without_the_new_fields_still_parses()
    {
        var d = DesiredGpuState.Parse("""{"requested":true,"frameCapFps":null,"requestedAtUtc":null}""");
        Assert.NotNull(d);
        Assert.Null(d!.CapVersion);
        Assert.Null(d.RequestedAtUtc);
        Assert.Null(d.FrameCapFps);
    }
}
