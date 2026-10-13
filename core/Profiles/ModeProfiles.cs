// GPD Forge - per-mode TDP presets. GPL-3.0-or-later.
using GpdForge.Ai;
using GpdForge.Tdp;
using System.Collections.ObjectModel;

namespace GpdForge.Profiles;

public static class ModeProfiles
{
    /// <summary>The mode whose profile is a sustained ceiling and must not retain boost headroom.</summary>
    public const string SustainedMode = ModeCatalogue.Ai;

    private static readonly object Gate = new();
    private static Dictionary<string, TdpProfile> _map = Defaults();

    /// <summary>A thread-safe snapshot. Mutations go through Set or PersistSet.</summary>
    public static IReadOnlyDictionary<string, TdpProfile> Map => Snapshot();

    public static TdpProfile? For(string mode)
    {
        lock (Gate)
            return _map.TryGetValue(mode, out var profile)
                ? string.Equals(mode, SustainedMode, StringComparison.OrdinalIgnoreCase) ? Shape(profile) : profile
                : null;
    }

    public static IReadOnlyDictionary<string, TdpProfile> Snapshot()
    {
        lock (Gate) return new ReadOnlyDictionary<string, TdpProfile>(new Dictionary<string, TdpProfile>(_map, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Rebuild the map from catalogue defaults and persisted user overlays.</summary>
    public static void Initialize(IEnumerable<KeyValuePair<string, TdpProfile>> overlays)
    {
        ArgumentNullException.ThrowIfNull(overlays);
        lock (Gate)
        {
            var next = Defaults();
            foreach (var (mode, profile) in overlays)
            {
                if (!ModeCatalogue.Exists(mode)) continue;
                next[ModeCatalogue.Find(mode)!.Id] = Normalize(ModeCatalogue.Find(mode)!.Id, profile);
            }
            _map = next;
        }
    }

    /// <summary>Update an in-memory profile. Unknown modes are rejected.</summary>
    public static TdpProfile Set(string mode, TdpProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        if (!ModeCatalogue.Exists(mode)) throw new ArgumentException($"Unknown mode '{mode}'.", nameof(mode));
        lock (Gate)
        {
            var normalized = Normalize(mode, profile);
            _map[ModeCatalogue.Find(mode)!.Id] = normalized;
            return normalized;
        }
    }

    /// <summary>Persist the complete sparse overlay before changing memory; I/O failures leave the map untouched.</summary>
    public static TdpProfile PersistSet(ModePresetStore store, string mode, TdpProfile profile)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        if (!ModeCatalogue.Exists(mode)) throw new ArgumentException($"Unknown mode '{mode}'.", nameof(mode));
        lock (Gate)
        {
            var canonical = ModeCatalogue.Find(mode)!.Id;
            var normalized = Normalize(canonical, profile);
            var overlays = new Dictionary<string, TdpProfile>(store.Read(), StringComparer.OrdinalIgnoreCase)
            {
                [canonical] = normalized,
            };
            store.Write(overlays);
            _map[canonical] = normalized;
            return normalized;
        }
    }

    /// <summary>Validate and persist a settings import as one transaction before changing memory.</summary>
    public static IReadOnlyDictionary<string, TdpProfile> PersistMany(ModePresetStore store, IReadOnlyDictionary<string, TdpProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(profiles);
        var normalized = new Dictionary<string, TdpProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mode, profile) in profiles)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mode);
            if (!ModeCatalogue.Exists(mode)) throw new ArgumentException($"Unknown mode '{mode}'.", nameof(profiles));
            var canonical = ModeCatalogue.Find(mode)!.Id;
            normalized[canonical] = Normalize(canonical, profile);
        }

        lock (Gate)
        {
            var overlays = new Dictionary<string, TdpProfile>(store.Read(), StringComparer.OrdinalIgnoreCase);
            foreach (var (mode, profile) in normalized) overlays[mode] = profile;
            store.Write(overlays);
            foreach (var (mode, profile) in normalized) _map[mode] = profile;
            return new Dictionary<string, TdpProfile>(normalized, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, TdpProfile> Defaults() =>
        ModeCatalogue.All.ToDictionary(m => m.Id, m => m.DefaultTdp, StringComparer.OrdinalIgnoreCase);

    private static TdpProfile Normalize(string mode, TdpProfile profile)
    {
        int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
        var safe = new TdpProfile(Clamp(profile.StapmW, 5, 40), Clamp(profile.FastW, 5, 45), Clamp(profile.SlowW, 5, 45), Clamp(profile.TctlC, 60, 95));
        return string.Equals(mode, SustainedMode, StringComparison.OrdinalIgnoreCase) ? Shape(safe) : safe;
    }

    internal static bool TryNormalizePersisted(string mode, TdpProfile profile, out TdpProfile normalized)
    {
        if (profile.StapmW is < 5 or > 40 || profile.FastW is < 5 or > 45 ||
            profile.SlowW is < 5 or > 45 || profile.TctlC is < 60 or > 95)
        {
            normalized = default;
            return false;
        }
        normalized = Normalize(mode, profile);
        return true;
    }

    private static TdpProfile Shape(TdpProfile profile) => ProfileShaper.Shape(profile.StapmW, profile.TctlC);
}
