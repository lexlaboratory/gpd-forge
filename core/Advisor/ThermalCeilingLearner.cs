// GPD Forge — learned thermal ceiling per game (plan F3). GPL-3.0-or-later.
//
// The guardian steps the limit down 3 W at a time while a game runs hot and holds it once the
// temperature stops climbing. Where it comes to rest is the most this game can sustain on this device
// (~22 W at ~88 °C, measured 2026-09-24) — a per-game number, since a lighter game settles higher.
// Learning it lets the advisor propose holding the limit there from the start instead of boosting,
// overheating and being cut back every session.
//
// A throttle counts as settled once it has held one value for SettleSeconds; each settle is one sample
// into an EMA, so one odd evening (a hot room, a charger) moves the estimate without replacing it.
// Stored beside the other state under %ProgramData%\GPD Forge with the same file discipline: atomic
// replace on write, a corrupt file moved aside and not trusted.
using System.Text.Json;
using GpdForge.Profiles;

namespace GpdForge.Advisor;

public sealed class ThermalCeilingLearner
{
    public const string FileName = "thermal-ceilings.json";

    /// <summary>Six guardian dwell windows (10 s each): long enough that a step on its way somewhere
    /// else is not taken for the resting point.</summary>
    public const double SettleSeconds = 60;

    /// <summary>Weight of the newest settle. 0.3 lets three or four sessions move the estimate.</summary>
    public const double Alpha = 0.3;

    private readonly Lock _gate = new();
    private readonly string? _filePath;
    private readonly Dictionary<string, double> _ceilings;
    private string? _game;
    private int? _watts;
    private DateTimeOffset _since;
    private bool _recorded;

    /// <param name="directory">Where to keep the file; null keeps everything in memory (tests).</param>
    public ThermalCeilingLearner(string? directory = null)
    {
        if (directory is not null)
        {
            try { Directory.CreateDirectory(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _filePath = Path.Combine(directory, FileName);
        }
        _ceilings = Load();
    }

    /// <summary>The learned ceiling for this game, or null until one settle has been seen.</summary>
    public double? CeilingFor(string? game)
    {
        var key = AppRulePolicy.Normalize(game);
        lock (_gate) return _ceilings.TryGetValue(key, out var w) ? w : null;
    }

    /// <summary>Called once per worker tick with the game in front and the guardian's throttle.</summary>
    public void Observe(string? game, int? throttledToW, DateTimeOffset now)
    {
        var key = AppRulePolicy.Normalize(game);
        lock (_gate)
        {
            if (key.Length == 0 || throttledToW is not int w || w <= 0)
            {
                _game = null; _watts = null; _recorded = false;
                return;
            }
            if (key != _game || w != _watts)
            {
                _game = key; _watts = w; _since = now; _recorded = false;
                return;
            }
            if (_recorded || (now - _since).TotalSeconds < SettleSeconds) return;

            _recorded = true;
            _ceilings[key] = _ceilings.TryGetValue(key, out var old) ? old + Alpha * (w - old) : w;
            Save();
        }
    }

    private Dictionary<string, double> Load()
    {
        var empty = new Dictionary<string, double>(StringComparer.Ordinal);
        if (_filePath is null || !File.Exists(_filePath)) return empty;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(_filePath)) ?? [];
            // Only what could be a sustained limit is trusted back; anything else is dropped.
            foreach (var (game, w) in raw)
            {
                var key = AppRulePolicy.Normalize(game);
                if (key.Length > 0 && w >= RuleOverridesPolicy.MinStapmW && w <= RuleOverridesPolicy.MaxStapmW) empty[key] = w;
            }
            return empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var corrupt = _filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            try { File.Move(_filePath, corrupt); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return empty;
        }
    }

    private void Save()
    {
        if (_filePath is null) return;
        // Best effort: losing one settle to a full disk must not take the worker tick down with it.
        try { AtomicFile.Write(_filePath, JsonSerializer.Serialize(_ceilings)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Write-then-replace, so a crash mid-write leaves the previous file, never half of one.</summary>
internal static class AtomicFile
{
    public static void Write(string path, string content)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, content);
        try { if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
    }
}
