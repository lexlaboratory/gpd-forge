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
    private readonly TimeSpan coalesceWindow;
    private readonly JsonSerializerOptions json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private List<AlertEvent> events;

    /// <param name="coalesceWindow">Silence window: a repeat of an open alert folds into it and only
    /// bumps its count. The window is measured from the LAST occurrence, so a phenomenon that keeps
    /// firing stays one alert for as long as it lasts, and a genuine recurrence after this much
    /// quiet opens a new one.</param>
    public AlertStore(string directory, IAlertClock? clock = null, int maxEvents = 500, TimeSpan? retention = null, TimeSpan? coalesceWindow = null)
    {
        if (maxEvents < 1) throw new ArgumentOutOfRangeException(nameof(maxEvents));
        this.clock = clock ?? new SystemAlertClock();
        this.maxEvents = maxEvents;
        this.retention = retention ?? TimeSpan.FromDays(30);
        this.coalesceWindow = coalesceWindow ?? TimeSpan.FromMinutes(10);
        if (this.retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        if (this.coalesceWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(coalesceWindow));
        Directory.CreateDirectory(directory);
        filePath = Path.Combine(directory, "alerts.json");
        events = Load();
        TrimAndPersistIfNeeded();
    }

    public IReadOnlyList<AlertEvent> List(bool unreadOnly = false, int? limit = null)
    {
        lock (gate)
        {
            // Ordered by LAST occurrence: an alert that is still firing has to stay at the top, even
            // if it was first raised before quieter alerts that came after it.
            IEnumerable<AlertEvent> query = events.Where(x => !unreadOnly || !x.Acknowledged).OrderByDescending(x => x.LastSeenUtc);
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

    /// <summary>
    /// Records an occurrence. The guardian republishes every tick for as long as a condition holds,
    /// so a repeat of an alert that is still open updates it (count, last-seen, latest reading)
    /// instead of adding another row — one continuous phenomenon, one alert.
    /// </summary>
    public AlertEvent Publish(AlertCategory category, AlertSeverity severity, string title, string message, string? technicalData = null, string? dedupeKey = null)
    {
        if (!Enum.IsDefined(category)) throw new ArgumentOutOfRangeException(nameof(category));
        if (!Enum.IsDefined(severity)) throw new ArgumentOutOfRangeException(nameof(severity));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("An alert needs a title", nameof(title));
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("An alert needs a message", nameof(message));
        var key = string.IsNullOrWhiteSpace(dedupeKey) ? null : dedupeKey.Trim();

        lock (gate)
        {
            var now = clock.UtcNow;
            // `events` is kept newest-first by Trim, so the first hit is the most recent occurrence.
            var index = events.FindIndex(x => !x.Acknowledged
                && now >= x.LastSeenUtc && now - x.LastSeenUtc <= coalesceWindow
                && IsSameOccurrence(x, category, severity, title, message, key));
            if (index >= 0)
            {
                var merged = events[index] with
                {
                    Count = events[index].Count + 1,
                    LastSeenUtc = now,
                    Message = message,
                    TechnicalData = technicalData
                };
                events[index] = merged;
                TrimAndPersist();
                return merged;
            }

            var item = new AlertEvent(Guid.NewGuid(), now, severity, category, title, message, technicalData, false, key, 1, now);
            events.Add(item);
            TrimAndPersist();
            return item;
        }
    }

    /// <summary>
    /// Whether a new publication is another sighting of <paramref name="candidate"/>. Category and
    /// severity are part of the identity on purpose: a critical must never be swallowed by an
    /// earlier warning that happens to carry the same dedupe key. Without a key the full text is the
    /// identity, so byte-identical repeats still collapse while genuinely different ones do not.
    /// </summary>
    private static bool IsSameOccurrence(AlertEvent candidate, AlertCategory category, AlertSeverity severity, string title, string message, string? key)
    {
        if (candidate.Category != category || candidate.Severity != severity) return false;
        return key is not null
            ? string.Equals(candidate.DedupeKey, key, StringComparison.Ordinal)
            : candidate.DedupeKey is null
              && string.Equals(candidate.Title, title, StringComparison.Ordinal)
              && string.Equals(candidate.Message, message, StringComparison.Ordinal);
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
        try
        {
            var raw = JsonSerializer.Deserialize<List<AlertEvent?>>(File.ReadAllText(filePath), json) ?? [];
            return raw.Where(x => x is not null).Select(x => Normalize(x!)).ToList();
        }
        catch
        {
            var corrupt = filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            try { File.Move(filePath, corrupt); } catch { }
            return [];
        }
    }

    /// <summary>Files written before coalescing existed carry no count/last-seen, which would
    /// deserialize as 0 and 0001-01-01 and then sort to the bottom and never age out correctly.</summary>
    private static AlertEvent Normalize(AlertEvent x) => x with
    {
        Count = x.Count < 1 ? 1 : x.Count,
        LastSeenUtc = x.LastSeenUtc < x.TimestampUtc ? x.TimestampUtc : x.LastSeenUtc
    };

    private void TrimAndPersistIfNeeded()
    {
        var before = events.Count;
        Trim();
        if (before != events.Count) Persist();
    }

    private void TrimAndPersist() { Trim(); Persist(); }

    private void Trim()
    {
        // Aged on the last occurrence, not the first: an alert still firing must not expire mid-event.
        var cutoff = clock.UtcNow - retention;
        events = events.Where(x => x.LastSeenUtc >= cutoff)
            .OrderByDescending(x => x.LastSeenUtc).Take(maxEvents).ToList();
    }

    private void Persist()
    {
        var temp = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(events, json));
        try { if (File.Exists(filePath)) File.Replace(temp, filePath, null); else File.Move(temp, filePath); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
}
