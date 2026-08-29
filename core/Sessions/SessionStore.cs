// GPD Forge — session persistence. GPL-3.0-or-later.
//
// Same shape as core/Alerts/AlertStore.cs, for the same reasons: a small indented JSON file under
// %ProgramData%\GPD Forge\, an atomic replace on every write, a defensive load that quarantines a
// corrupt file instead of taking the daemon down with it, normalization of rows written by an older
// build, and hard retention bounds — this runs on a handheld's system drive and must never grow
// without limit.
using System.Text.Json;

namespace GpdForge.Sessions;

public interface ISessionClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemSessionClock : ISessionClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class SessionStore
{
    private readonly Lock _gate = new();
    private readonly string _filePath;
    private readonly ISessionClock _clock;
    private readonly int _maxSessions;
    private readonly TimeSpan _retention;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private List<GameSession> _sessions;

    /// <param name="maxSessions">200 sessions is over a year of daily play and a file well under a
    /// megabyte; past that the oldest are dropped.</param>
    /// <param name="retention">90 days: long enough to answer "is this game running worse than it
    /// used to?", short enough that the file never becomes an archive nobody asked for.</param>
    public SessionStore(string directory, ISessionClock? clock = null, int maxSessions = 200, TimeSpan? retention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSessions, 1);
        _clock = clock ?? new SystemSessionClock();
        _maxSessions = maxSessions;
        _retention = retention ?? TimeSpan.FromDays(90);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_retention, TimeSpan.Zero, nameof(retention));

        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "sessions.json");
        _sessions = Load();
        TrimAndPersistIfNeeded();
    }

    /// <summary>Newest first. <paramref name="app"/> filters case-insensitively (process names come
    /// off the wire with inconsistent casing).</summary>
    public IReadOnlyList<GameSession> List(string? app = null, int? limit = null)
    {
        lock (_gate)
        {
            IEnumerable<GameSession> query = _sessions.OrderByDescending(x => x.StartedUtc);
            if (!string.IsNullOrWhiteSpace(app))
                query = query.Where(x => string.Equals(x.App, app.Trim(), StringComparison.OrdinalIgnoreCase));
            if (limit is > 0) query = query.Take(limit.Value);
            return query.ToArray();
        }
    }

    public GameSession? Get(Guid id)
    {
        lock (_gate) return _sessions.FirstOrDefault(x => x.Id == id);
    }

    public IReadOnlyList<GameSummary> PerGame()
    {
        lock (_gate) return SessionMath.PerGame(_sessions);
    }

    public GameSession Add(GameSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            _sessions.Add(session);
            Trim();
            Persist();
            return session;
        }
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            if (_sessions.RemoveAll(x => x.Id == id) == 0) return false;
            Persist();
            return true;
        }
    }

    private List<GameSession> Load()
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            var raw = JsonSerializer.Deserialize<List<GameSession?>>(File.ReadAllText(_filePath), _json) ?? [];
            return raw.Where(x => x is not null).Select(x => Normalize(x!)).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var corrupt = _filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            try { File.Move(_filePath, corrupt); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return [];
        }
    }

    /// <summary>A row from an older build (or a hand-edited file) can be missing anything. Rather
    /// than trusting it or dropping it, restore the invariants the rest of the code relies on: a real
    /// id, a non-empty app name, a non-negative duration consistent with the timestamps, and a trend
    /// series that is never null.</summary>
    private static GameSession Normalize(GameSession s)
    {
        double span = Math.Max(0, (s.EndedUtc - s.StartedUtc).TotalSeconds);
        return s with
        {
            Id = s.Id == Guid.Empty ? Guid.NewGuid() : s.Id,
            App = string.IsNullOrWhiteSpace(s.App) ? "unknown" : s.App.Trim(),
            DurationSeconds = s.DurationSeconds > 0 ? s.DurationSeconds : span,
            Samples = Math.Max(0, s.Samples),
            SamplesWithoutFps = Math.Max(0, s.SamplesWithoutFps),
            FpsTrend = s.FpsTrend ?? []
        };
    }

    private void TrimAndPersistIfNeeded()
    {
        int before = _sessions.Count;
        Trim();
        if (before != _sessions.Count) Persist();
    }

    private void Trim()
    {
        // Aged on the start time: that is when the session belongs to, and it never moves.
        var cutoff = _clock.UtcNow - _retention;
        _sessions = _sessions.Where(x => x.StartedUtc >= cutoff)
            .OrderByDescending(x => x.StartedUtc).Take(_maxSessions).ToList();
    }

    private void Persist()
    {
        var temp = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(_sessions, _json));
        try { if (File.Exists(_filePath)) File.Replace(temp, _filePath, null); else File.Move(temp, _filePath); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
    }
}
