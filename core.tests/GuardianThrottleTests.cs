// GPD Forge — a thermal throttle may only ever LOWER a limit. GPL-3.0-or-later.
//
// Measured on 2026-09-24 (/audit, 18:04:49): in `windows` mode (15/20/17 W, Tctl 92) the guardian
// "throttled" by applying 25/25/25 W with Tctl 96 — raising sustained power by 10 W and the thermal
// limit by 4°C on a device that was already hot. The ramp's top (NominalCeilingW) is an absolute
// number chosen for `gaming`, and it was applied without looking at what the active mode allowed.
using GpdForge.Guardian;
using GpdForge.Tdp;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class GuardianThrottleTests
{
    private static readonly TdpProfile Windows = new(15, 20, 17, 92);
    private static readonly TdpProfile Gaming = new(25, 33, 28, 95);

    [Fact]
    public void Never_raises_any_limit_of_a_lower_mode()
    {
        var p = GuardianThrottle.Profile(throttleW: 25, mode: Windows, tctlCriticalC: 96);
        Assert.Equal(new TdpProfile(15, 20, 17, 92), p);
    }

    [Fact]
    public void Caps_every_limit_of_a_higher_mode_at_the_throttle()
    {
        var p = GuardianThrottle.Profile(throttleW: 20, mode: Gaming, tctlCriticalC: 96);
        Assert.Equal(new TdpProfile(20, 20, 20, 95), p);
    }

    [Fact]
    public void Tctl_is_the_lower_of_the_mode_and_the_critical_limit()
    {
        var hot = new TdpProfile(25, 33, 28, 98);
        Assert.Equal(96, GuardianThrottle.Profile(20, hot, 96).TctlC);
        Assert.Equal(92, GuardianThrottle.Profile(20, Windows, 96).TctlC);
    }

    [Fact]
    public void Without_a_mode_profile_it_falls_back_to_the_flat_throttle()
    {
        Assert.Equal(new TdpProfile(18, 18, 18, 96), GuardianThrottle.Profile(18, null, 96));
    }
}

public class GuardianSmoothingTests
{
    private static TelemetrySnapshot Snap(double cpu) => new(cpu, 0, 0, 0, 0, 0, 0, 0, 80, 0, true, true);

    private sealed class Clock { public double Now; public double Read() => Now; }

    private static (GuardianService svc, Clock clock) Steady(double tempC, int seconds = 30)
    {
        var clock = new Clock();
        var svc = new GuardianService(clock.Read);
        for (int i = 0; i < seconds; i++) { svc.Observe(Snap(tempC)); clock.Now += 1; }
        return (svc, clock);
    }

    [Fact]
    public void A_single_Tctl_spike_into_the_throttle_band_does_not_throttle()
    {
        // The shape behind "doesn't hold the watts": Tctl in the mid-80s with one tick past 90.
        var (svc, _) = Steady(85);
        var d = svc.Observe(Snap(93));
        Assert.Null(d.ThrottleToW);
        Assert.False(svc.Throttling);
    }

    [Fact]
    public void A_sustained_excursion_still_throttles_within_seconds()
    {
        var (svc, clock) = Steady(85);
        GuardianDecision d = default;
        for (int i = 0; i < 6; i++) { d = svc.Observe(Snap(93)); clock.Now += 1; }
        Assert.NotNull(d.ThrottleToW);
        Assert.True(svc.Throttling);
    }

    [Fact]
    public void The_critical_limit_still_reacts_to_the_raw_reading_instantly()
    {
        var (svc, _) = Steady(80);
        var d = svc.Observe(Snap(97));
        Assert.Equal(svc.Config.ThrottleFloorW, d.ThrottleToW);
    }

    [Fact]
    public void A_single_cool_tick_does_not_release_a_throttle()
    {
        var (svc, clock) = Steady(92);
        Assert.True(svc.Throttling);
        var d = svc.Observe(Snap(80));
        Assert.False(d.ClearThrottle);
        Assert.True(svc.Throttling);
    }
}
