using System.Text.Json;
using GpdForge.Tdp;
using Microsoft.Extensions.Logging;

namespace GpdForge.Profiles;

/// <summary>Durable sparse overlays for mode TDP presets. Catalogue defaults remain authoritative when no override exists.</summary>
public sealed class ModePresetStore
{
    private sealed record SavedProfile(int? StapmW, int? FastW, int? SlowW, int? TctlC);

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly ILogger<ModePresetStore>? _logger;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ModePresetStore(string directory, ILogger<ModePresetStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _logger = logger;
        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not create mode preset directory {Directory}.", directory);
        }
        _filePath = Path.Combine(directory, "mode-presets.json");
    }

    /// <summary>Returns valid catalogue overlays only. Missing, corrupt, or unreadable files mean defaults.</summary>
    public IReadOnlyDictionary<string, TdpProfile> Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath)) return new Dictionary<string, TdpProfile>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, SavedProfile?>>(File.ReadAllText(_filePath), Json);
                if (saved is null) throw new JsonException("The mode preset document must be a JSON object.");
                var valid = new Dictionary<string, TdpProfile>(StringComparer.OrdinalIgnoreCase);
                foreach (var (mode, raw) in saved)
                {
                    var definition = ModeCatalogue.Find(mode);
                    if (definition is null) continue;
                    if (raw?.StapmW is not int stapmW || raw.FastW is not int fastW || raw.SlowW is not int slowW || raw.TctlC is not int tctlC)
                    {
                        _logger?.LogWarning("Ignoring incomplete persisted TDP preset for {Mode}; using its catalogue default.", definition.Id);
                        continue;
                    }
                    var profile = new TdpProfile(stapmW, fastW, slowW, tctlC);
                    if (!ModeProfiles.TryNormalizePersisted(definition.Id, profile, out var normalized))
                    {
                        _logger?.LogWarning("Ignoring invalid persisted TDP preset for {Mode}; using its catalogue default.", definition.Id);
                        continue;
                    }
                    // Last entry wins if differently-cased keys refer to the same catalogue mode.
                    valid[definition.Id] = normalized;
                }
                return valid;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Mode preset file is unreadable; using catalogue defaults.");
                var corrupt = _filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                try { File.Move(_filePath, corrupt); }
                catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogWarning(moveEx, "Could not quarantine corrupt mode preset file {Path}.", _filePath);
                }
                return new Dictionary<string, TdpProfile>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>Atomically replaces the sparse overlay file. Throws on failure so callers cannot claim a save.</summary>
    public void Write(IReadOnlyDictionary<string, TdpProfile> overlays)
    {
        ArgumentNullException.ThrowIfNull(overlays);
        lock (_gate)
        {
            var canonical = new Dictionary<string, TdpProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var (mode, profile) in overlays)
            {
                if (!ModeCatalogue.Exists(mode)) throw new ArgumentException($"Unknown mode '{mode}'.", nameof(overlays));
                canonical[ModeCatalogue.Find(mode)!.Id] = profile;
            }

            var temp = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, canonical, Json);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(_filePath)) File.Replace(temp, _filePath, null);
                else File.Move(temp, _filePath);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogWarning(ex, "Could not remove temporary mode preset file {Path}.", temp);
                }
            }
        }
    }
}
