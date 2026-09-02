// GPD Forge — one alert per episode, not one per reading. GPL-3.0-or-later.
//
// The corpus below is REAL. It is the shape of C:\ProgramData\GPD Forge\alerts.json on the reference
// device on 2026-09-02: **77 rows, 67 of them with Count == 1**, every one titled "Thermal guardian",
// with dedupe keys like `guardian:warn:Battery 14% — low`, `…15%…`, `…9%…` and one per degree of CPU
// temperature.
//
// AlertStore's coalescing was never broken. It was being handed a key built from the message text,
// and the message embeds the reading — so "CPU 90°C", "CPU 91°C" and "CPU 92°C" were three separate
// alerts about one episode of the CPU being hot. A guard against noise that generates the noise.
//
// These tests drive the real GuardianEvaluator, so they break if a future decision forgets its Kind.
using GpdForge.Alerts;
using GpdForge.Guardian;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class GuardianAlertCoalescingTests
{
    private sealed class FixedClock(DateTimeOffset now) : IAlertClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private static TelemetrySnapshot Snap(double? cpuTempC, int? batteryPct = 80, bool ac = true) =>
        new(cpuTempC, null, null, null, null, null, null, null, batteryPct, null, ac, true);

    /// <summary>Publishes exactly as ForgeWorker does, so the key under test is the shipped one.</summary>
    private static void PublishLikeTheWorker(AlertStore store, GuardianDecision g)
    {
        if (g.Alert is null) return;
        var severity = g.Severity switch { "critical" => AlertSeverity.Critica, "warn" => AlertSeverity.Aviso, _ => AlertSeverity.Info };
        var key = g.Kind ?? $"guardian:{g.Severity}:{g.Alert}";
        var isBattery = g.Kind is GuardianKind.BatteryLow or GuardianKind.BatteryCritical;
        store.Publish(isBattery ? AlertCategory.System : AlertCategory.Thermal, severity,
            isBattery ? "Battery guardian" : "Thermal guardian", g.Alert, null, key);
    }

    private static (AlertStore store, string dir) FreshStore(FixedClock clock)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gpdforge-coalesce-" + Guid.NewGuid().ToString("n"));
        return (new AlertStore(dir, clock), dir);
    }

    [Fact]
    public void One_thermal_episode_that_sweeps_the_ramp_is_one_alert()
    {
        // The device's real pattern: the CPU sits on the throttle threshold and the ramp walks the
        // watts down as the temperature climbs, so every tick produces a different message.
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 29, 14, 0, 0, TimeSpan.Zero));
        var (store, dir) = FreshStore(clock);
        try
        {
            var cfg = new GuardianConfig();
            foreach (var temp in new double[] { 90, 91, 92, 93, 94, 93, 92, 91, 90, 91, 92, 93, 94, 95 })
            {
                PublishLikeTheWorker(store, GuardianEvaluator.Evaluate(Snap(temp), cfg, null));
                clock.UtcNow = clock.UtcNow.AddSeconds(30);
            }

            var rows = store.List();

            // BEFORE this fix that produced 6 rows for these 14 ticks (one per distinct °C/W pairing).
            // Asserting the count rather than "fewer than before" is what makes a regression loud.
            Assert.Single(rows);
            Assert.Equal(14, rows[0].Count);
            Assert.Equal(GuardianKind.ThermalThrottle, rows[0].DedupeKey);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void One_discharge_down_the_percentages_is_one_alert()
    {
        // The live store held `Battery 15% — low` … `Battery 9% — low` as seven separate rows for one
        // continuous discharge.
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 29, 20, 0, 0, TimeSpan.Zero));
        var (store, dir) = FreshStore(clock);
        try
        {
            var cfg = new GuardianConfig();
            foreach (var pct in new[] { 15, 14, 13, 12, 11, 10, 9 })
            {
                PublishLikeTheWorker(store, GuardianEvaluator.Evaluate(Snap(60, pct, ac: false), cfg, null));
                clock.UtcNow = clock.UtcNow.AddMinutes(1);
            }

            var rows = store.List();

            Assert.Single(rows);
            Assert.Equal(7, rows[0].Count);
            Assert.Equal(GuardianKind.BatteryLow, rows[0].DedupeKey);

            // And it is no longer filed as a thermal event. All 77 rows on the device said
            // "Thermal guardian", including every battery one.
            Assert.Equal("Battery guardian", rows[0].Title);
            Assert.Equal(AlertCategory.System, rows[0].Category);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Crossing_from_low_into_critical_opens_a_second_alert()
    {
        // The line this must not cross in the other direction. Coalescing must not merge a warning
        // into a critical — they are different phenomena and the user acts differently on each.
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 29, 21, 0, 0, TimeSpan.Zero));
        var (store, dir) = FreshStore(clock);
        try
        {
            var cfg = new GuardianConfig();
            foreach (var pct in new[] { 14, 12, 10, 7, 5 })
            {
                PublishLikeTheWorker(store, GuardianEvaluator.Evaluate(Snap(60, pct, ac: false), cfg, null));
                clock.UtcNow = clock.UtcNow.AddMinutes(1);
            }

            var rows = store.List();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.DedupeKey == GuardianKind.BatteryLow && r.Count == 3);
            Assert.Contains(rows, r => r.DedupeKey == GuardianKind.BatteryCritical && r.Count == 2);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Thermal_and_battery_episodes_never_merge()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 29, 22, 0, 0, TimeSpan.Zero));
        var (store, dir) = FreshStore(clock);
        try
        {
            var cfg = new GuardianConfig();
            PublishLikeTheWorker(store, GuardianEvaluator.Evaluate(Snap(94), cfg, null));
            clock.UtcNow = clock.UtcNow.AddMinutes(1);
            PublishLikeTheWorker(store, GuardianEvaluator.Evaluate(Snap(60, 12, ac: false), cfg, null));

            var rows = store.List();
            Assert.Equal(2, rows.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Every_alerting_decision_carries_a_kind()
    {
        // The guard on the guard. A decision that returns an alert with no Kind falls back to the old
        // message-derived key and quietly reintroduces the noise, so this walks the evaluator's
        // branches and refuses any that forgot.
        var cfg = new GuardianConfig();
        var cases = new (string name, GuardianDecision d)[]
        {
            ("thermal critical",       GuardianEvaluator.Evaluate(Snap(cfg.TempCriticalC + 1), cfg, null)),
            ("thermal throttle",       GuardianEvaluator.Evaluate(Snap(cfg.TempThrottleC + 1), cfg, null)),
            ("throttle cleared",       GuardianEvaluator.Evaluate(Snap(60), cfg, currentThrottleW: 12)),
            ("battery low",            GuardianEvaluator.Evaluate(Snap(60, cfg.BatteryLowPct - 1, ac: false), cfg, null)),
            ("battery critical",       GuardianEvaluator.Evaluate(Snap(60, cfg.BatteryCriticalPct - 1, ac: false), cfg, null)),
            ("temperature unreadable", GuardianEvaluator.Evaluate(Snap(null), cfg, null)),
        };

        foreach (var (name, d) in cases)
        {
            Assert.True(d.Alert is not null, $"The '{name}' case stopped producing an alert; this test no longer covers it.");
            Assert.False(string.IsNullOrEmpty(d.Kind),
                $"The '{name}' decision alerts without a Kind, so it falls back to a message-derived " +
                "dedupe key and reopens a new alert on every changed reading.");
        }
    }
}
