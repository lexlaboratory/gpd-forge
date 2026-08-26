// GPD Forge — system health check / anomaly detection tests (pure rules). GPL-3.0-or-later.
using GpdForge.Health;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class HealthCheckTests
{
    // Defaults describe a healthy unit: moderate temp, fan spinning, TDP holding, on AC, no discharge.
    private static TelemetrySnapshot Snap(
        double cpuTempC = 60, int fanRpm = 3000, bool tdpVerified = true, bool acConnected = true, double dischargeW = 0) =>
        new(cpuTempC, 0, 0, 0, fanRpm, 0, 0, 0, 80, dischargeW, acConnected, tdpVerified);

    private static readonly HealthContext Ctx = new(); // defaults: fanStuck 70 / critical 95 / highDischarge 30

    [Fact]
    public void Ok_when_nothing_is_wrong()
    {
        var r = HealthCheck.Evaluate(Snap(), Ctx);
        Assert.Equal("ok", r.Status);
        Assert.Empty(r.Issues);
    }

    // --- fan not spinning while warm (the parked-fan state this rule exists to catch) ---

    [Fact]
    public void Fan_parked_while_warm_warns()
    {
        var r = HealthCheck.Evaluate(Snap(cpuTempC: 75, fanRpm: 0), Ctx);
        var issue = Assert.Single(r.Issues);
        Assert.Equal("warn", issue.Level);
        Assert.Equal("fan_not_spinning", issue.Code);
        Assert.Equal("warn", r.Status);
    }

    [Fact]
    public void Fan_parked_while_cool_is_fine()
    {
        var r = HealthCheck.Evaluate(Snap(cpuTempC: 65, fanRpm: 0), Ctx);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Fan_spinning_while_warm_is_fine()
    {
        var r = HealthCheck.Evaluate(Snap(cpuTempC: 85, fanRpm: 2500), Ctx);
        Assert.DoesNotContain(r.Issues, i => i.Code == "fan_not_spinning");
    }

    [Fact]
    public void Fan_rule_boundary_is_strictly_greater_than()
    {
        Assert.DoesNotContain(HealthCheck.Evaluate(Snap(cpuTempC: 70, fanRpm: 0), Ctx).Issues, i => i.Code == "fan_not_spinning");
        Assert.Contains(HealthCheck.Evaluate(Snap(cpuTempC: 70.1, fanRpm: 0), Ctx).Issues, i => i.Code == "fan_not_spinning");
    }

    // --- critical thermal ---

    [Fact]
    public void Critical_temp_flags_critical()
    {
        var r = HealthCheck.Evaluate(Snap(cpuTempC: 96, fanRpm: 3000), Ctx);
        var issue = Assert.Single(r.Issues);
        Assert.Equal("critical", issue.Level);
        Assert.Equal("thermal_critical", issue.Code);
        Assert.Equal("critical", r.Status);
    }

    [Fact]
    public void Critical_temp_boundary_is_inclusive()
    {
        Assert.Contains(HealthCheck.Evaluate(Snap(cpuTempC: 95, fanRpm: 3000), Ctx).Issues, i => i.Code == "thermal_critical");
        Assert.DoesNotContain(HealthCheck.Evaluate(Snap(cpuTempC: 94.9, fanRpm: 3000), Ctx).Issues, i => i.Code == "thermal_critical");
    }

    // --- TDP not holding (firmware reverting) ---

    [Fact]
    public void Tdp_not_verified_warns()
    {
        var r = HealthCheck.Evaluate(Snap(tdpVerified: false), Ctx);
        var issue = Assert.Single(r.Issues);
        Assert.Equal("warn", issue.Level);
        Assert.Equal("tdp_not_holding", issue.Code);
    }

    [Fact]
    public void Tdp_verified_raises_nothing()
    {
        var r = HealthCheck.Evaluate(Snap(tdpVerified: true), Ctx);
        Assert.DoesNotContain(r.Issues, i => i.Code == "tdp_not_holding");
    }

    // --- high discharge on battery ---

    [Fact]
    public void High_discharge_on_battery_warns()
    {
        var r = HealthCheck.Evaluate(Snap(acConnected: false, dischargeW: 35), Ctx);
        var issue = Assert.Single(r.Issues);
        Assert.Equal("warn", issue.Level);
        Assert.Equal("high_discharge", issue.Code);
    }

    [Fact]
    public void High_discharge_on_AC_is_ignored()
    {
        var r = HealthCheck.Evaluate(Snap(acConnected: true, dischargeW: 35), Ctx);
        Assert.DoesNotContain(r.Issues, i => i.Code == "high_discharge");
    }

    [Fact]
    public void Discharge_rule_boundary_is_strictly_greater_than()
    {
        Assert.DoesNotContain(HealthCheck.Evaluate(Snap(acConnected: false, dischargeW: 30), Ctx).Issues, i => i.Code == "high_discharge");
        Assert.Contains(HealthCheck.Evaluate(Snap(acConnected: false, dischargeW: 30.1), Ctx).Issues, i => i.Code == "high_discharge");
    }

    // --- severity aggregation: Status is the MAX severity across Issues ---

    [Fact]
    public void Status_is_warn_when_only_warn_issues_present()
    {
        var r = HealthCheck.Evaluate(Snap(tdpVerified: false, acConnected: false, dischargeW: 35), Ctx);
        Assert.Equal(2, r.Issues.Count);
        Assert.Equal("warn", r.Status);
    }

    [Fact]
    public void Status_is_critical_when_a_critical_issue_coexists_with_warnings()
    {
        // Fan parked-while-warm (warn) + TDP not holding (warn) + at the critical temp (critical), all at once.
        var r = HealthCheck.Evaluate(Snap(cpuTempC: 96, fanRpm: 0, tdpVerified: false), Ctx);
        Assert.Equal(3, r.Issues.Count);
        Assert.Contains(r.Issues, i => i.Level == "warn");
        Assert.Contains(r.Issues, i => i.Level == "critical");
        Assert.Equal("critical", r.Status); // critical wins regardless of how many warns also fired
    }

    // --- HealthContext thresholds are actually respected, not hardcoded ---

    [Fact]
    public void Custom_thresholds_change_what_triggers()
    {
        var tight = new HealthContext(FanStuckTempC: 50, CriticalTempC: 80, HighDischargeW: 10);

        // Wouldn't trigger under the defaults, but does under these tighter custom thresholds.
        Assert.Contains(HealthCheck.Evaluate(Snap(cpuTempC: 60, fanRpm: 0), tight).Issues, i => i.Code == "fan_not_spinning");
        Assert.Contains(HealthCheck.Evaluate(Snap(cpuTempC: 85, fanRpm: 3000), tight).Issues, i => i.Code == "thermal_critical");
        Assert.Contains(HealthCheck.Evaluate(Snap(acConnected: false, dischargeW: 15), tight).Issues, i => i.Code == "high_discharge");

        // The exact same snapshots are clean under the default context.
        Assert.Empty(HealthCheck.Evaluate(Snap(cpuTempC: 60, fanRpm: 0), Ctx).Issues);
        Assert.Empty(HealthCheck.Evaluate(Snap(cpuTempC: 85, fanRpm: 3000), Ctx).Issues);
        Assert.Empty(HealthCheck.Evaluate(Snap(acConnected: false, dischargeW: 15), Ctx).Issues);
    }
}
