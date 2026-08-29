// GPD Forge — alert coalescing. GPL-3.0-or-later.
//
// Measured on a real 55-minute session: 3291 telemetry samples, 4 of them above 90 °C, and the
// alert centre held 62 near-identical "Thermal guardian — CPU 90°C — easing to 25 W" entries. The
// guardian publishes once per tick for as long as the condition holds, so a continuous phenomenon
// must land as ONE alert that keeps updating, not as one alert per tick.
using GpdForge.Alerts;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class AlertCoalescingTests
{
    private const string GuardianKey = "guardian:warn:CPU 90°C — easing to 25 W";

    [Fact]
    public void Sixty_identical_publishes_collapse_into_one_alert_with_the_real_count()
    {
        using var temp = new AlertTempDir();
        var clock = new AlertFakeClock(DateTimeOffset.UtcNow);
        var store = new AlertStore(temp.Path, clock);

        for (var i = 0; i < 60; i++)
        {
            store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "Thermal guardian",
                "CPU 90°C — easing to 25 W", "cpuTempC=90.1", GuardianKey);
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        var events = store.List();
        Assert.Single(events);
        Assert.Equal(60, events[0].Count);
    }

    [Fact]
    public void Coalesced_alert_tracks_first_and_last_occurrence()
    {
        using var temp = new AlertTempDir();
        var start = DateTimeOffset.UtcNow;
        var clock = new AlertFakeClock(start);
        var store = new AlertStore(temp.Path, clock);

        var first = store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "T", "hot", null, GuardianKey);
        clock.Advance(TimeSpan.FromMinutes(3));
        var second = store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "T", "hot", null, GuardianKey);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(start, second.TimestampUtc);
        Assert.Equal(start.AddMinutes(3), second.LastSeenUtc);
        Assert.Equal(1, first.Count);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void Coalesced_alert_shows_the_latest_message_and_technical_data()
    {
        using var temp = new AlertTempDir();
        var clock = new AlertFakeClock(DateTimeOffset.UtcNow);
        var store = new AlertStore(temp.Path, clock);

        store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "Thermal guardian", "CPU 90°C", "cpuTempC=90.2", GuardianKey);
        clock.Advance(TimeSpan.FromSeconds(30));
        store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "Thermal guardian", "CPU 93°C", "cpuTempC=93.4", GuardianKey);

        var latest = Assert.Single(store.List());
        Assert.Equal("CPU 93°C", latest.Message);
        Assert.Equal("cpuTempC=93.4", latest.TechnicalData);
    }

    [Fact]
    public void A_critical_never_folds_into_a_previous_warning_even_on_the_same_key()
    {
        using var temp = new AlertTempDir();
        var clock = new AlertFakeClock(DateTimeOffset.UtcNow);
        var store = new AlertStore(temp.Path, clock);

        store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "Thermal guardian", "warm", null, "guardian:thermal");
        clock.Advance(TimeSpan.FromSeconds(5));
        var critical = store.Publish(AlertCategory.Thermal, AlertSeverity.Critica, "Thermal guardian", "critical", null, "guardian:thermal");

        var events = store.List();
        Assert.Equal(2, events.Count);
        Assert.Equal(critical.Id, events[0].Id);
        Assert.Equal(AlertSeverity.Critica, events[0].Severity);
    }

    [Fact]
    public void Different_categories_never_coalesce_together()
    {
        using var temp = new AlertTempDir();
        var store = new AlertStore(temp.Path, new AlertFakeClock(DateTimeOffset.UtcNow));

        store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "Same", "same", null, "shared-key");
        store.Publish(AlertCategory.Hardware, AlertSeverity.Aviso, "Same", "same", null, "shared-key");

        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void A_recurrence_after_the_silence_window_opens_a_fresh_alert()
    {
        using var temp = new AlertTempDir();
        var clock = new AlertFakeClock(DateTimeOffset.UtcNow);
        var store = new AlertStore(temp.Path, clock, coalesceWindow: TimeSpan.FromMinutes(10));

        var first = store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "T", "hot", null, GuardianKey);
        clock.Advance(TimeSpan.FromMinutes(11));
        var second = store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "T", "hot", null, GuardianKey);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, store.List().Count);
        Assert.Equal(1, second.Count);
    }

    [Fact]
    public void The_silence_window_is_measured_from_the_last_occurrence_not_the_first()
    {
        using var temp = new AlertTempDir();
        var clock = new AlertFakeClock(DateTimeOffset.UtcNow);
        var store = new AlertStore(temp.Path, clock, coalesceWindow: TimeSpan.FromMinutes(10));

        var first = store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "T", "hot", null, GuardianKey);
        for (var i = 0; i < 12; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "T", "hot", null, GuardianKey);
        }

        var only = Assert.Single(store.List());
        Assert.Equal(first.Id, only.Id);
        Assert.Equal(13, only.Count);
    }

    [Fact]
    public void An_acknowledged_alert_is_never_silently_reused()
    {
        using var temp = new AlertTempDir();
        var clock = new AlertFakeClock(DateTimeOffset.UtcNow);
        var store = new AlertStore(temp.Path, clock);

        var first = store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "T", "hot", null, GuardianKey);
        Assert.True(store.Acknowledge(first.Id));
        clock.Advance(TimeSpan.FromSeconds(5));
        var second = store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "T", "hot", null, GuardianKey);

        Assert.NotEqual(first.Id, second.Id);
        Assert.False(second.Acknowledged);
        Assert.Equal(1, second.Count);
    }

    [Fact]
    public void Identical_publishes_without_a_dedupe_key_also_coalesce()
    {
        using var temp = new AlertTempDir();
        var clock = new AlertFakeClock(DateTimeOffset.UtcNow);
        var store = new AlertStore(temp.Path, clock);

        for (var i = 0; i < 5; i++)
        {
            store.Publish("service", "info", "Daemon restarted", "The service came back up");
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        var only = Assert.Single(store.List());
        Assert.Equal(5, only.Count);
        Assert.Null(only.DedupeKey);
    }

    [Fact]
    public void Genuinely_different_keyless_alerts_stay_separate()
    {
        using var temp = new AlertTempDir();
        var store = new AlertStore(temp.Path, new AlertFakeClock(DateTimeOffset.UtcNow));

        store.Publish("hardware", "aviso", "Fan", "Fan 1 stalled");
        store.Publish("hardware", "aviso", "Fan", "Fan 2 stalled");

        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void An_ongoing_alert_sorts_above_older_untouched_ones()
    {
        using var temp = new AlertTempDir();
        var clock = new AlertFakeClock(DateTimeOffset.UtcNow);
        var store = new AlertStore(temp.Path, clock);

        var ongoing = store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "Thermal", "hot", null, GuardianKey);
        clock.Advance(TimeSpan.FromMinutes(1));
        store.Publish("service", "info", "Quiet", "nothing to see");
        clock.Advance(TimeSpan.FromMinutes(1));
        store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "Thermal", "still hot", null, GuardianKey);

        Assert.Equal(ongoing.Id, store.List()[0].Id);
    }

    [Fact]
    public void Retention_keeps_an_alert_that_is_still_recurring()
    {
        using var temp = new AlertTempDir();
        var clock = new AlertFakeClock(DateTimeOffset.UtcNow);
        var store = new AlertStore(temp.Path, clock, retention: TimeSpan.FromMinutes(30),
            coalesceWindow: TimeSpan.FromMinutes(10));

        store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "T", "hot", null, GuardianKey);
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            store.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "T", "hot", null, GuardianKey);
        }

        var only = Assert.Single(store.List());
        Assert.Equal(21, only.Count);
    }

    [Fact]
    public void Legacy_files_without_count_or_last_seen_are_normalised_on_load()
    {
        using var temp = new AlertTempDir();
        var stamp = DateTimeOffset.UtcNow.AddMinutes(-1);
        File.WriteAllText(Path.Combine(temp.Path, "alerts.json"), $$"""
        [
          {
            "Id": "11111111-1111-1111-1111-111111111111",
            "TimestampUtc": "{{stamp:O}}",
            "Severity": "Aviso",
            "Category": "Thermal",
            "Title": "Legacy",
            "Message": "from an older build",
            "TechnicalData": null,
            "Acknowledged": false,
            "DedupeKey": "legacy"
          }
        ]
        """);

        var store = new AlertStore(temp.Path, new AlertFakeClock(DateTimeOffset.UtcNow));

        var loaded = Assert.Single(store.List());
        Assert.Equal(1, loaded.Count);
        Assert.Equal(loaded.TimestampUtc, loaded.LastSeenUtc);
    }

    [Fact]
    public void Publish_rejects_missing_text()
    {
        using var temp = new AlertTempDir();
        var store = new AlertStore(temp.Path, new AlertFakeClock(DateTimeOffset.UtcNow));

        Assert.Throws<ArgumentException>(() => store.Publish(AlertCategory.System, AlertSeverity.Info, "  ", "body"));
        Assert.Throws<ArgumentException>(() => store.Publish(AlertCategory.System, AlertSeverity.Info, "title", "  "));
    }

    [Fact]
    public void Coalescing_survives_a_restart_of_the_store()
    {
        using var temp = new AlertTempDir();
        var clock = new AlertFakeClock(DateTimeOffset.UtcNow);
        var first = new AlertStore(temp.Path, clock)
            .Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "T", "hot", null, GuardianKey);

        clock.Advance(TimeSpan.FromMinutes(1));
        var reopened = new AlertStore(temp.Path, clock);
        var second = reopened.Publish(AlertCategory.Thermal, AlertSeverity.Aviso, "T", "hot", null, GuardianKey);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.Count);
        Assert.Single(reopened.List());
    }
}

internal sealed class AlertTempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpd-alerts-" + Guid.NewGuid().ToString("N"));
    public AlertTempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

internal sealed class AlertFakeClock(DateTimeOffset now) : IAlertClock
{
    public DateTimeOffset UtcNow { get; private set; } = now;
    public void Advance(TimeSpan amount) => UtcNow += amount;
}
