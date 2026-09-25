// GPD Forge — the frame cap a game profile owes back, kept across restarts. GPL-3.0-or-later.
//
// F1 audit round 4 (2026-09-25). A game's cap reaches the driver through the agent, and FRTC is a
// persisted DRIVER setting: it survives the daemon, the agent and a reboot. The cap to put back when
// the game leaves lived only in memory, so a service restart, an update or a shutdown with a capped
// game in front left the game's cap on the driver for every app. The next game then read it back as
// the user's own (GameProfileApplier.PreviousCap), and the user's cap was never recovered. TDP, fan
// and Anti-Lag / Chill all recover on their own after a restart; the cap was the one override that
// leaked. So the pending restore is written here at Begin, removed at End, and replayed at startup.
//
// Same file discipline as FanPreferenceStore: atomic replace on write, and a corrupt or implausible
// record is dropped rather than trusted — replaying a wrong cap is worse than replaying none.
using System.Text.Json;

namespace GpdForge.Profiles;

/// <summary>The cap to go back to (null = off), or <see cref="Unknown"/> when it was never read.</summary>
public sealed record PendingCapRestore(int? Cap, bool Unknown, string? Match = null);

public sealed class CapRestoreStore
{
    public const string FileName = "game-cap-restore.json";

    /// <summary>FRTC's range as the device reports it is 15–1000; anything outside 1–1000 was not
    /// written by this daemon.</summary>
    private const int MaxPlausibleFps = 1000;

    private readonly Lock _gate = new();
    private readonly string _filePath;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public CapRestoreStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _filePath = Path.Combine(directory, FileName);
    }

    public PendingCapRestore? Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath)) return null;
            try
            {
                var p = JsonSerializer.Deserialize<PendingCapRestore>(File.ReadAllText(_filePath), _json);
                if (p is not null && (p.Unknown || p.Cap is null or (> 0 and <= MaxPlausibleFps))) return p;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
            DeleteQuietly();
            return null;
        }
    }

    public void Write(PendingCapRestore record)
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
            // A record that cannot be written costs only the restart case; it must never cost the game
            // its profile, so the failure is swallowed here and the in-memory restore still works.
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
