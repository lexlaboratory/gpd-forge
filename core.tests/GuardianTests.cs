// GPD Forge — thermal/battery guardian tests (pure evaluator + stateful service). GPL-3.0-or-later.
using GpdForge.Guardian;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class GuardianTests
{
    private static TelemetrySnapshot Snap(double cpu, int battery = 80, bool ac = true) =>
        new(cpu, 0, 0, 0, 0, 0, 0, 0, battery, 0, ac, true);

    private static readonly GuardianConfig Cfg = new(); // defaults: throttle 90 / critical 96 / floor 12 / nominal 25

    [Fact]
    public void Cool_and_charged_does_nothing()
    {
        var d = GuardianEvaluator.Evaluate(Snap(62), Cfg, null);
        Assert.Null(d.ThrottleToW);
        Assert.False(d.ClearThrottle);
        Assert.Null(d.Alert);
        Assert.Equal("ok", d.Severity);
    }

    [Fact]
    public void Critical_temp_holds_the_floor()
    {
        var d = GuardianEvaluator.Evaluate(Snap(97), Cfg, null);
        Assert.Equal(Cfg.ThrottleFloorW, d.ThrottleToW);
        Assert.Equal("critical", d.Severity);
    }

    [Fact]
    public void Warm_temp_ramps_between_floor_and_ceiling()
    {
        var d = GuardianEvaluator.Evaluate(Snap(93), Cfg, null);
        Assert.NotNull(d.ThrottleToW);
        Assert.InRange(d.ThrottleToW!.Value, Cfg.ThrottleFloorW, Cfg.NominalCeilingW);
        Assert.Equal("warn", d.Severity);
    }

    [Fact]
    public void Ramp_is_monotonic_and_hits_the_endpoints()
    {
        Assert.Equal(Cfg.NominalCeilingW, GuardianEvaluator.RampWatts(90, Cfg));
        Assert.Equal(Cfg.ThrottleFloorW, GuardianEvaluator.RampWatts(96, Cfg));
        Assert.True(GuardianEvaluator.RampWatts(91, Cfg) >= GuardianEvaluator.RampWatts(94, Cfg));
    }

    [Fact]
    public void Clears_throttle_after_cooling_below_hysteresis()
    {
        var d = GuardianEvaluator.Evaluate(Snap(80), Cfg, currentThrottleW: 18);
        Assert.True(d.ClearThrottle);
        Assert.Null(d.ThrottleToW);
    }

    [Fact]
    public void Holds_throttle_while_still_warm()
    {
        var d = GuardianEvaluator.Evaluate(Snap(88), Cfg, currentThrottleW: 18); // 88 > 90-4=86, no clear
        Assert.False(d.ClearThrottle);
        Assert.Equal(18, d.ThrottleToW);
    }

    [Fact]
    public void Battery_critical_only_on_battery()
    {
        Assert.Equal("critical", GuardianEvaluator.Evaluate(Snap(60, battery: 5, ac: false), Cfg, null).Severity);
        Assert.Equal("warn", GuardianEvaluator.Evaluate(Snap(60, battery: 12, ac: false), Cfg, null).Severity);
        Assert.Equal("ok", GuardianEvaluator.Evaluate(Snap(60, battery: 5, ac: true), Cfg, null).Severity); // on AC: ignored
    }

    [Fact]
    public void Disabled_clears_and_stays_silent()
    {
        var d = GuardianEvaluator.Evaluate(Snap(97), Cfg with { Enabled = false }, currentThrottleW: 18);
        Assert.True(d.ClearThrottle);
        Assert.Null(d.ThrottleToW);
        Assert.Null(d.Alert);
    }

    [Fact]
    public void Service_tracks_throttle_state()
    {
        var svc = new GuardianService();
        svc.Observe(Snap(97));
        Assert.True(svc.Throttling);
        Assert.Equal(svc.Config.ThrottleFloorW, svc.ThrottledToW);
        Assert.Equal("critical", svc.LastSeverity);
    }

    [Fact]
    public void Service_gates_throttle_but_still_alerts_when_auto_throttle_off()
    {
        var svc = new GuardianService();
        svc.Configure(new GuardianConfig(AutoThrottle: false));
        var d = svc.Observe(Snap(97));
        Assert.Null(d.ThrottleToW);        // gated: no power change requested
        Assert.False(svc.Throttling);
        Assert.Equal("critical", svc.LastSeverity); // but the alert surfaced
    }

    [Fact]
    public void Service_clears_throttle_when_disabled_mid_throttle()
    {
        var svc = new GuardianService();
        svc.Observe(Snap(97));
        Assert.True(svc.Throttling);
        svc.Configure(new GuardianConfig(Enabled: false));
        var d = svc.Observe(Snap(97));
        Assert.True(d.ClearThrottle);
        Assert.False(svc.Throttling);
    }
}
