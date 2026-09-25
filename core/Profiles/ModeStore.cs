// GPD Forge — the active mode, kept across restarts. GPL-3.0-or-later.
//
// ModeState lived only in memory and started as `windows`. That was harmless while nothing wrote TDP
// at start; since 2026-09-24 ForgeWorker applies the active mode when the daemon starts, so a reboot or
// a service restart while the user was in `gaming` actively wrote windows 15/20/17 W (audit round 2,
// 2026-09-24). Same file discipline as FanPreferenceStore: atomic replace on write, a corrupt file is
// moved aside rather than trusted, and what is read back is validated before it can pick a TDP.
//
// Audit round 3 (2026-09-25): the file also records WHO chose the mode. Every writer saved it, so a
// mode auto-profiles or the AC switch picked (`battery`, unplugged) came back after a restart as if the
// user had chosen it, and the engine adopted it — 8 W on a machine that booted on AC, until a ruled app
// or an AC edge. A file without the field predates it and was written for a user's pick.
using System.Text.Json;

namespace GpdForge.Profiles;

public sealed class ModeStore
{
    private const string UserSource = "user";
    private const string AutoSource = "auto";

    private sealed record Saved(string? Active, string? Source = null);

    /// <summary>A saved mode and whether the user picked it (POST /mode) rather than an automatic
    /// switch (auto-profiles, the AC/battery switch).</summary>
    public sealed record Entry(string Mode, bool ChosenByUser);

    private readonly Lock _gate = new();
    private readonly string _filePath;
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ModeStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _filePath = Path.Combine(directory, "mode.json");
    }

    /// <summary>The saved mode, or null when there is none, it is unreadable, or the catalogue does not
    /// know it. Null means "use the default", never "no mode".</summary>
    public string? Read() => ReadEntry()?.Mode;

    /// <summary>The saved mode and who chose it; null exactly when <see cref="Read"/> is.</summary>
    public Entry? ReadEntry()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath)) return null;
            try
            {
                var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_filePath), _json);
                if (!ModeCatalogue.Exists(saved?.Active)) return null;
                return new Entry(saved!.Active!, !string.Equals(saved.Source, AutoSource, StringComparison.Ordinal));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                var corrupt = _filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                try { File.Move(_filePath, corrupt); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return null;
            }
        }
    }

    /// <summary>Saves <paramref name="mode"/> and who chose it. Throws on an I/O failure; the caller
    /// decides whether a mode that cannot be saved is worth more than a log line (ModeState: it is
    /// not).</summary>
    public void Write(string mode, bool chosenByUser = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        lock (_gate)
        {
            var temp = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(new Saved(mode, chosenByUser ? UserSource : AutoSource), _json));
            try { if (File.Exists(_filePath)) File.Replace(temp, _filePath, null); else File.Move(temp, _filePath); }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
        }
    }
}
