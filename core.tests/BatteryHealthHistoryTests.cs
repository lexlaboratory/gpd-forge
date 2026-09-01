// GPD Forge — the daily-sampling rules for battery health history. GPL-3.0-or-later.
using GpdForge.Alerts;
using GpdForge.Battery;
using Xunit;

namespace GpdForge.Core.Tests;

public class BatteryHealthHistoryTests
{
    private sealed class FakeClock : IAlertClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class MemoryStore : IBatteryHealthStore
    {
        private List<BatteryHealthSample> _samples = [];
        public int Writes { get; private set; }
        public IReadOnlyList<BatteryHealthSample> Read() => _samples;
        public void Write(IReadOnlyList<BatteryHealthSample> samples)
        {
            Writes++;
            _samples = [.. samples];
        }
    }

    private static BatteryHealthReading Reading(double? health = 91.2, int? full = 40009)
        => new(43890, full, health, null, null, "LION", null);

    [Fact]
    public void The_first_reading_of_a_day_is_recorded()
    {
        var store = new MemoryStore();
        var history = new BatteryHealthHistory(store, new FakeClock());

        Assert.True(history.Observe(Reading()));
        Assert.Single(history.Samples());
    }

    [Fact]
    public void A_second_reading_on_the_same_day_is_not()
    {
        var clock = new FakeClock();
        var store = new MemoryStore();
        var history = new BatteryHealthHistory(store, clock);

        history.Observe(Reading());
        clock.UtcNow = clock.UtcNow.AddHours(6);

        Assert.False(history.Observe(Reading(health: 91.0)));
        Assert.Single(history.Samples());
        Assert.Equal(1, store.Writes);   // and it did not rewrite the file for nothing
    }

    [Fact]
    public void The_window_is_the_calendar_date_not_a_rolling_24_hours()
    {
        // A 24-hour window drifts the sampling moment later every day until it wraps, which produces
        // two samples on one day and none on the next. Twenty-three hours later is a new DATE here,
        // and that is the behaviour wanted.
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 9, 1, 23, 0, 0, TimeSpan.Zero) };
        var store = new MemoryStore();
        var history = new BatteryHealthHistory(store, clock);

        history.Observe(Reading());
        clock.UtcNow = clock.UtcNow.AddHours(2);   // 01:00 on the 2nd — 2 hours later, new day

        Assert.True(history.Observe(Reading(health: 91.1)));
        Assert.Equal(2, history.Samples().Count);
    }

    [Fact]
    public void A_reading_with_no_health_figure_is_refused_rather_than_stored_as_a_null_row()
    {
        // The trend is computed from the oldest and newest samples. A null-health row at either end
        // makes DegradationPoints return null, so storing them would let a run of failed reads
        // silently switch the trend off for as long as it lasted.
        var store = new MemoryStore();
        var history = new BatteryHealthHistory(store, new FakeClock());

        Assert.False(history.Observe(Reading(health: null, full: null)));
        Assert.Empty(history.Samples());
    }

    [Fact]
    public void Degradation_appears_once_there_are_samples_from_two_days()
    {
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero) };
        var store = new MemoryStore();
        var history = new BatteryHealthHistory(store, clock);

        history.Observe(Reading(health: 93.4, full: 41000));
        Assert.Null(history.DegradationPoints());
        Assert.NotNull(history.TrendUnavailableReason());

        clock.UtcNow = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        history.Observe(Reading(health: 91.2, full: 40009));

        Assert.Equal(2.2, history.DegradationPoints());
        Assert.Null(history.TrendUnavailableReason());
    }

    [Fact]
    public void An_empty_history_explains_itself_rather_than_showing_nothing()
    {
        // A card that renders blank is indistinguishable from a card that is broken — the failure
        // this codebase spent 2026-08-28 diagnosing.
        var history = new BatteryHealthHistory(new MemoryStore(), new FakeClock());
        var reason = history.TrendUnavailableReason();

        Assert.NotNull(reason);
        Assert.Contains("one health sample per day", reason);
    }

    [Fact]
    public void A_history_file_written_in_either_casing_still_loads()
    {
        // This file accumulates for years. It is written in PascalCase because the store does not use
        // JsonSerializerDefaults.Web — and the day someone switches to Web defaults as tidying, every
        // existing sample would deserialise to nulls, the history would read as empty, and a
        // two-year trend would vanish with no error anywhere.
        var dir = Path.Combine(Path.GetTempPath(), "gpdforge-health-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "battery-health.json"), """
                [
                  { "atUtc": "2026-01-01T00:00:00+00:00", "fullChargeMwh": 41000, "healthPercent": 93.4 },
                  { "AtUtc": "2026-09-01T00:00:00+00:00", "FullChargeMilliwattHours": 40009, "HealthPercent": 91.2 }
                ]
                """);

            var samples = new FileBatteryHealthStore(dir).Read();

            Assert.Equal(2, samples.Count);
            Assert.Equal(93.4, samples[0].HealthPercent);
            Assert.Equal(91.2, samples[1].HealthPercent);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void A_corrupt_history_file_is_quarantined_rather_than_deleted()
    {
        // Years of samples are not recoverable from anywhere else. Treating a half-written file as
        // "no history" would hide that it ever happened.
        var dir = Path.Combine(Path.GetTempPath(), "gpdforge-health-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "battery-health.json");
            File.WriteAllText(path, "[ { \"AtUtc\": \"2026-09-01");   // truncated mid-write

            var samples = new FileBatteryHealthStore(dir).Read();

            Assert.Empty(samples);
            Assert.False(File.Exists(path));
            Assert.NotEmpty(Directory.GetFiles(dir, "battery-health.json.corrupt-*"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void The_oldest_samples_are_trimmed_first_when_the_cap_is_reached()
    {
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2020, 1, 1, 12, 0, 0, TimeSpan.Zero) };
        var store = new MemoryStore();
        var history = new BatteryHealthHistory(store, clock);

        for (var i = 0; i < BatteryHealthHistory.MaxSamples + 25; i++)
        {
            history.Observe(Reading(health: 100 - i * 0.001));
            clock.UtcNow = clock.UtcNow.AddDays(1);
        }

        var samples = history.Samples();
        Assert.Equal(BatteryHealthHistory.MaxSamples, samples.Count);

        // The newest survived: the recent end is what a user is looking at.
        Assert.Equal(clock.UtcNow.AddDays(-1).Date, samples.Max(s => s.AtUtc).UtcDateTime.Date);
    }
}
