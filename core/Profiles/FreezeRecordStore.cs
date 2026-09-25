// GPD Forge — the PIDs a game froze, kept on disk until they are thawed. GPL-3.0-or-later.
//
// F5 (2026-09-25). A suspended process outlives the daemon that suspended it: kill the service mid-game
// and OneDrive stays frozen until reboot. The PIDs are written at the freeze, removed at the thaw, and
// resumed at the next start (GameFreezer.RecoverAtStartup) — the same discipline as CapRestoreStore:
// atomic replace on write, and a corrupt record dropped rather than trusted.
using System.Text.Json;

namespace GpdForge.Profiles;

public sealed record FrozenName(string Name, IReadOnlyList<int> Pids);

public sealed record FreezeRecord(IReadOnlyList<FrozenName> Names);

public sealed class FreezeRecordStore
{
    public const string FileName = "game-freeze.json";

    private readonly Lock _gate = new();
    private readonly string _filePath;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public FreezeRecordStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _filePath = Path.Combine(directory, FileName);
    }

    public FreezeRecord? Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath)) return null;
            try
            {
                var r = JsonSerializer.Deserialize<FreezeRecord>(File.ReadAllText(_filePath), _json);
                // Only well-formed entries: a bare process name and positive PIDs. Anything else was not
                // written by this daemon, and resuming on a guess is not recovery.
                if (r?.Names is { } names && names.All(n => n is { Name.Length: > 0, Pids: not null } && n.Pids.All(p => p > 0)))
                    return r;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
            DeleteQuietly();
            return null;
        }
    }

    public void Write(FreezeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var temp = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temp, JsonSerializer.Serialize(record, _json));
                try { if (File.Exists(_filePath)) File.Replace(temp, _filePath, null); else File.Move(temp, _filePath); }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
            }
            // A record that cannot be written costs only the crash case; the in-memory thaw still works.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Clear()
    {
        lock (_gate) DeleteQuietly();
    }

    private void DeleteQuietly()
    {
        try { if (File.Exists(_filePath)) File.Delete(_filePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
