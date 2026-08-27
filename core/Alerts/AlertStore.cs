using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpdForge.Alerts;

public sealed class AlertStore
{
    private readonly object gate = new();
    private readonly string filePath;
    private readonly IAlertClock clock;
    private readonly int maxEvents;
    private readonly TimeSpan retention;
    private readonly JsonSerializerOptions json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private List<AlertEvent> events;

    public AlertStore(string directory, IAlertClock? clock = null, int maxEvents = 500, TimeSpan? retention = null)
    {
        if (maxEvents < 1) throw new ArgumentOutOfRangeException(nameof(maxEvents));
        this.clock = clock ?? new SystemAlertClock();
        this.maxEvents = maxEvents;
        this.retention = retention ?? TimeSpan.FromDays(30);
        Directory.CreateDirectory(directory);
        filePath = Path.Combine(directory, "alerts.json");
        events = Load();
        TrimAndPersistIfNeeded();
    }

    public IReadOnlyList<AlertEvent> List(bool unreadOnly = false, int? limit = null)
    {
        lock (gate)
        {
            IEnumerable<AlertEvent> query = events.Where(x => !unreadOnly || !x.Acknowledged).OrderByDescending(x => x.TimestampUtc);
            if (limit is > 0) query = query.Take(limit.Value);
            return query.ToArray();
        }
    }

    public AlertEvent Publish(string category, string severity, string title, string message, string? technicalData = null, string? dedupeKey = null)
        => Publish(Enum.Parse<AlertCategory>(category, true), ParseSeverity(severity), title, message, technicalData, dedupeKey);

    private static AlertSeverity ParseSeverity(string value) => value.Trim().ToLowerInvariant() switch
    {
        "info" => AlertSeverity.Info,
        "aviso" or "warn" or "warning" => AlertSeverity.Aviso,
        "critica" or "crítica" or "critical" => AlertSeverity.Critica,
        _ => throw new ArgumentException("Unknown alert severity", nameof(value))
    };

    public AlertEvent Publish(AlertCategory category, AlertSeverity severity, string title, string message, string? technicalData = null, string? dedupeKey = null)
    {
        lock (gate)
        {
            if (!string.IsNullOrWhiteSpace(dedupeKey))
            {
                var existing = events.FirstOrDefault(x => x.DedupeKey == dedupeKey && clock.UtcNow - x.TimestampUtc < TimeSpan.FromMinutes(10));
                if (existing is not null) return existing;
            }
            var item = new AlertEvent(Guid.NewGuid(), clock.UtcNow, severity, category, title, message, technicalData, false, dedupeKey);
            events.Add(item);
            TrimAndPersist();
            return item;
        }
    }

    public bool Acknowledge(Guid id)
    {
        lock (gate)
        {
            var index = events.FindIndex(x => x.Id == id && !x.Acknowledged);
            if (index < 0) return false;
            events[index] = events[index] with { Acknowledged = true };
            Persist();
            return true;
        }
    }

    public int AcknowledgeAll()
    {
        lock (gate)
        {
            var count = events.Count(x => !x.Acknowledged);
            if (count == 0) return 0;
            events = events.Select(x => x with { Acknowledged = true }).ToList();
            Persist();
            return count;
        }
    }

    public bool Delete(Guid id)
    {
        lock (gate)
        {
            var removed = events.RemoveAll(x => x.Id == id) > 0;
            if (removed) Persist();
            return removed;
        }
    }

    private List<AlertEvent> Load()
    {
        if (!File.Exists(filePath)) return [];
        try { return JsonSerializer.Deserialize<List<AlertEvent>>(File.ReadAllText(filePath), json) ?? []; }
        catch
        {
            var corrupt = filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            try { File.Move(filePath, corrupt); } catch { }
            return [];
        }
    }

    private void TrimAndPersistIfNeeded()
    {
        var before = events.Count;
        Trim();
        if (before != events.Count) Persist();
    }

    private void TrimAndPersist() { Trim(); Persist(); }

    private void Trim()
    {
        var cutoff = clock.UtcNow - retention;
        events = events.Where(x => x.TimestampUtc >= cutoff)
            .OrderByDescending(x => x.TimestampUtc).Take(maxEvents).ToList();
    }

    private void Persist()
    {
        var temp = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(events, json));
        try { if (File.Exists(filePath)) File.Replace(temp, filePath, null); else File.Move(temp, filePath); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
}
