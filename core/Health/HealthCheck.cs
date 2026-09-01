// GPD Forge — system health check / anomaly detection (pure logic). GPL-3.0-or-later.
using GpdForge.Telemetry;

namespace GpdForge.Health;

/// <summary>One detected anomaly. <see cref="Level"/> is "warn" or "critical"; <see cref="Code"/> is a
/// stable machine-readable identifier (for the UI / external agents); <see cref="Message"/> is the
/// human-readable explanation shown in the System health card.</summary>
public sealed record HealthIssue(string Level, string Code, string Message);

/// <summary>Overall verdict: <see cref="Status"/> is "ok" / "warn" / "critical" — the MAX severity
/// across <see cref="Issues"/> (empty issues ⇒ "ok").</summary>
public sealed record HealthReport(string Status, IReadOnlyList<HealthIssue> Issues);

/// <summary>Thresholds the pure rules below evaluate against. A record CLASS (not struct) so
/// `new HealthContext()` actually applies these parameter defaults — mirrors GuardianConfig.</summary>
public sealed record HealthContext(
    double FanStuckTempC = 70,   // fan reads 0 rpm AND cpuTempC is above this → warn (parked-fan-while-warm)
    double CriticalTempC = 95,   // cpuTempC at/above this → critical thermal
    double HighDischargeW = 30); // on battery AND dischargeW is above this → warn

/// <summary>
/// Pure, side-effect-free anomaly detection over a telemetry snapshot — trivially unit-testable (same
/// shape as GpdForge.Guardian.GuardianEvaluator). Never touches hardware; the caller (GET /health/check)
/// supplies a real snapshot from ITelemetryService.
/// </summary>
public static class HealthCheck
{
    public static HealthReport Evaluate(TelemetrySnapshot t, HealthContext ctx)
    {
        var issues = new List<HealthIssue>();

        // Since telemetry went nullable (2026-09-01) every rule below is written against an unwrapped
        // value rather than a lifted comparison. That is not style: `null == 0` and `null >= 96` are
        // both false, so an unreadable sensor would make each rule quietly decline to fire and this
        // check would report "ok" for a machine it cannot see at all. A health check that cannot
        // measure and says "healthy" is worse than one that says nothing.
        if (t.CpuTempC is not double temp)
        {
            issues.Add(new HealthIssue("warn", "telemetry_unavailable",
                "CPU temperature is unreadable, so the thermal and fan checks below cannot run. " +
                "Enable hardware access (GPDFORGE_ENABLE_HARDWARE=1, elevated) to restore them."));
        }
        else
        {
            // This literally catches this unit's parked-fan state: 0 rpm while the CPU is already
            // warm means the fan isn't spinning up, not that it's simply idle at a cool temp.
            //
            // `is int rpm` matters here more than anywhere: 0 rpm is a genuine and alarming reading,
            // and before this change "no EC fan source is wired" ALSO produced 0 — so an
            // unconfigured machine looked exactly like one with a dead fan.
            if (t.FanRpm is int rpm && rpm == 0 && temp > ctx.FanStuckTempC)
                issues.Add(new HealthIssue("warn", "fan_not_spinning",
                    $"Fan not spinning while warm — 0 rpm at {temp:0}°C CPU."));

            if (temp >= ctx.CriticalTempC)
                issues.Add(new HealthIssue("critical", "thermal_critical",
                    $"CPU at {temp:0}°C — critical thermal."));
        }

        if (!t.TdpVerified)
            issues.Add(new HealthIssue("warn", "tdp_not_holding",
                "TDP not holding (firmware reverting)."));

        if (!t.AcConnected && t.DischargeW is double watts && watts > ctx.HighDischargeW)
            issues.Add(new HealthIssue("warn", "high_discharge",
                $"High discharge on battery — {watts:0.#} W."));

        string status = "ok";
        foreach (var issue in issues)
        {
            if (issue.Level == "critical") { status = "critical"; break; }
            if (issue.Level == "warn") status = "warn";
        }

        return new HealthReport(status, issues);
    }
}
