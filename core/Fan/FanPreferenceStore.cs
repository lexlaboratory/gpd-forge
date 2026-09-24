// GPD Forge — the user's fan preference, kept across restarts. GPL-3.0-or-later.
//
// FanState used to live only in memory, so every reboot, service restart or reinstall silently
// handed the fan back to firmware (seen three times on 2026-09-24). Same file discipline as the
// other stores under %ProgramData%\GPD Forge: atomic replace on write, a corrupt file is moved
// aside rather than trusted, and anything read back is validated before it can reach the EC.

using System.Text.Json;

namespace GpdForge.Fan;

public sealed record FanPreference(string Mode, int ManualDuty)
{
    public static readonly FanPreference Default = new("Auto", 128);
}

public sealed class FanPreferenceStore
{
    private readonly Lock _gate = new();
    private readonly string _filePath;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public FanPreferenceStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _filePath = Path.Combine(directory, "fan.json");
    }

    public FanPreference Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath)) return FanPreference.Default;
            try
            {
                var p = JsonSerializer.Deserialize<FanPreference>(File.ReadAllText(_filePath), _json);
                if (p is null) return FanPreference.Default;
                return new FanPreference(
                    FanControlPolicy.IsValidMode(p.Mode) ? p.Mode : FanPreference.Default.Mode,
                    Math.Clamp(p.ManualDuty, 0, 255));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                var corrupt = _filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                try { File.Move(_filePath, corrupt); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return FanPreference.Default;
            }
        }
    }

    public void Write(FanPreference preference)
    {
        lock (_gate)
        {
            var temp = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(preference, _json));
            try { if (File.Exists(_filePath)) File.Replace(temp, _filePath, null); else File.Move(temp, _filePath); }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
        }
    }
}
