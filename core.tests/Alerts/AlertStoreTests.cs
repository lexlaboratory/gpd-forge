using GpdForge.Alerts;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class AlertStoreTests
{
    [Fact]
    public void Publish_orders_newest_and_deduplicates_inside_window()
    {
        using var temp = new TempDir();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var store = new AlertStore(temp.Path, clock, maxEvents: 500, retention: TimeSpan.FromDays(30));

        var first = store.Publish("thermal", "aviso", "Warm", "CPU warm", dedupeKey: "cpu-warm");
        var duplicate = store.Publish("thermal", "aviso", "Warm", "CPU warm", dedupeKey: "cpu-warm");
        clock.Advance(TimeSpan.FromMinutes(2));
        var second = store.Publish("service", "info", "Recovered", "Service ready");

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(2, store.List().Count);
        Assert.Equal(second.Id, store.List()[0].Id);
        Assert.Equal(first.Id, store.List()[1].Id);
    }

    [Fact]
    public void Retention_applies_age_and_count()
    {
        using var temp = new TempDir();
        var clock = new FakeClock(DateTimeOffset.UtcNow.AddDays(-31));
        var store = new AlertStore(temp.Path, clock, maxEvents: 2, retention: TimeSpan.FromDays(30));
        store.Publish("system", "info", "Old", "old");
        clock.Advance(TimeSpan.FromDays(31));
        store.Publish("system", "info", "A", "a");
        clock.Advance(TimeSpan.FromSeconds(1));
        store.Publish("system", "info", "B", "b");
        clock.Advance(TimeSpan.FromSeconds(1));
        store.Publish("system", "info", "C", "c");

        var events = store.List();
        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "C", "B" }, events.Select(x => x.Title));
    }

    [Fact]
    public void Corrupt_file_is_quarantined_and_store_recovers()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "alerts.json"), "{not-json");
        var store = new AlertStore(temp.Path, new FakeClock(DateTimeOffset.UtcNow));

        Assert.Empty(store.List());
        Assert.Single(Directory.GetFiles(temp.Path, "alerts.json.corrupt-*"));
        store.Publish("system", "info", "Recovered", "fresh");
        Assert.Single(store.List());
    }

    [Fact]
    public void Acknowledge_and_delete_are_idempotent()
    {
        using var temp = new TempDir();
        var store = new AlertStore(temp.Path, new FakeClock(DateTimeOffset.UtcNow));
        var item = store.Publish("system", "info", "Test", "test");

        Assert.True(store.Acknowledge(item.Id));
        Assert.False(store.Acknowledge(item.Id));
        Assert.True(store.Delete(item.Id));
        Assert.False(store.Delete(item.Id));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpd-alerts-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private sealed class FakeClock(DateTimeOffset now) : IAlertClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;
        public void Advance(TimeSpan amount) => UtcNow += amount;
    }
}
