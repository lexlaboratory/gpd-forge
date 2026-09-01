// GPD Forge — what the thermal guardian does when it cannot see. GPL-3.0-or-later.
//
// These exist because of how the nullable telemetry migration could have failed SILENTLY. Making
// CpuTempC a `double?` broke nothing at compile time in this file: in C# `null >= 90` is false, so
// every threshold below would still have compiled and every one would have quietly decided "not
// hot". The guardian would have stopped protecting the device and nothing would have said so.
//
// Worse, and less obvious: the release path is also a comparison. `currentThrottleW is not null &&
// null <= 86` is false too, so a throttle already applied would never have been cleared — the
// machine would sit at 12 W indefinitely with no explanation available anywhere.
using GpdForge.Guardian;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class GuardianBlindTests
{
    private static TelemetrySnapshot Snap(double? cpuTempC, int? batteryPct = 80, bool ac = true) =>
        new(CpuTempC: cpuTempC, GpuTempC: null, PackageW: null, CpuClockMhz: null, FanRpm: null,
            FanDutyPct: null, Fps: null, Fps1PctLow: null, BatteryPct: batteryPct, DischargeW: null,
            AcConnected: ac, TdpVerified: true);

    [Fact]
    public void With_no_temperature_the_guardian_says_it_cannot_protect_the_device()
    {
        var d = GuardianEvaluator.Evaluate(Snap(null), new GuardianConfig(), currentThrottleW: null);

        Assert.Equal("warn", d.Severity);
        Assert.NotNull(d.Alert);
        Assert.Contains("unreadable", d.Alert);

        // It does not invent a throttle either — there is no evidence of heat, only an absence of
        // evidence, and throttling on that would punish every machine without the hardware gate open.
        Assert.Null(d.ThrottleToW);
    }

    [Fact]
    public void With_no_temperature_an_existing_throttle_is_HELD_not_silently_kept_forever()
    {
        // The choice when the sensor is lost is between holding (slow, safe) and releasing (fast,
        // unprotected). It holds — the last evidence said the part was hot and nothing has
        // contradicted it — but the decision is visible rather than being an accident of `null <= 86`
        // evaluating to false.
        var d = GuardianEvaluator.Evaluate(Snap(null), new GuardianConfig(), currentThrottleW: 12);

        Assert.Equal(12, d.ThrottleToW);
        Assert.False(d.ClearThrottle);
        Assert.NotNull(d.Alert);
        Assert.Contains("holding 12 W", d.Alert);
    }

    [Fact]
    public void A_real_temperature_still_throttles_exactly_as_before()
    {
        // The regression guard for the change itself: the blind path must not have swallowed the
        // working one.
        var c = new GuardianConfig();
        var critical = GuardianEvaluator.Evaluate(Snap(c.TempCriticalC + 1), c, null);

        Assert.Equal(c.ThrottleFloorW, critical.ThrottleToW);
        Assert.Equal("critical", critical.Severity);
    }

    [Fact]
    public void A_real_temperature_still_clears_a_throttle_once_it_cools()
    {
        var c = new GuardianConfig();
        var cooled = GuardianEvaluator.Evaluate(
            Snap(c.TempThrottleC - c.ClearHysteresisC - 1), c, currentThrottleW: 12);

        Assert.True(cooled.ClearThrottle);
        Assert.Null(cooled.ThrottleToW);
    }

    [Fact]
    public void An_unreadable_battery_does_not_raise_a_critical_alert()
    {
        // The most dangerous zero in the old telemetry: a failed WMI battery query returned 0, and
        // the guardian raises CRITICAL below 8 %. Every failed read announced an emergency on a
        // machine that might have been at 90 %.
        var d = GuardianEvaluator.Evaluate(Snap(60, batteryPct: null, ac: false), new GuardianConfig(), null);

        Assert.Equal("ok", d.Severity);
        Assert.Null(d.Alert);
    }

    [Fact]
    public void A_genuinely_low_battery_still_alerts()
    {
        var c = new GuardianConfig();
        var d = GuardianEvaluator.Evaluate(Snap(60, batteryPct: c.BatteryCriticalPct - 1, ac: false), c, null);

        Assert.Equal("critical", d.Severity);
        Assert.Contains("Battery", d.Alert);
    }

    [Fact]
    public void A_disabled_guardian_stays_quiet_even_with_no_temperature()
    {
        // The disabled check comes first on purpose: someone who turned the guardian off should not
        // be told hourly that it cannot see.
        var d = GuardianEvaluator.Evaluate(Snap(null), new GuardianConfig(Enabled: false), null);

        Assert.Equal("ok", d.Severity);
        Assert.Null(d.Alert);
    }
}
