// GPD Forge — MotionAssistant .ini profile importer: pure parser + a thin file source. GPL-3.0-or-later.
//
// MotionAssistant keeps its per-profile TDP tuning in INI-like files under
// `C:\Program Files\Motion Assistant\Profiles\*.ini`. The on-disk schema isn't publicly documented,
// so ParseIni is intentionally liberal: `[ProfileName]` sections, `key=value` lines, `;`/`#`
// comments (full-line or trailing), blank/junk lines ignored, several key aliases per field
// (STAPM/TDP/PL1, FastLimit/PL2, SlowLimit/PL3, TctlTemp/Temp/thermal), and every value clamped
// into the same safe band GpdForge.Profiles.ModeProfiles.Set uses. It never throws — a malformed or
// truncated file just yields fewer or default-valued profiles instead of failing the import.
using System.Globalization;
using GpdForge.Tdp;

namespace GpdForge.Import;

/// <summary>One profile recovered from a MotionAssistant .ini file.</summary>
public readonly record struct ImportedProfile(string Name, int StapmW, int FastW, int SlowW, int TctlC)
{
    /// <summary>Convenience bridge to the shape <c>POST /profiles/:mode</c> (and <see
    /// cref="GpdForge.Profiles.ModeProfiles"/>) expect — whatever applies this import reuses it.</summary>
    public TdpProfile ToTdpProfile() => new(StapmW, FastW, SlowW, TctlC);
}

/// <summary>Pure, side-effect-free .ini parsing — trivially unit-testable with sample strings. No
/// file I/O lives here; see <see cref="IIniFileSource"/> for that.</summary>
public static class MotionAssistantImporter
{
    // Mirrors the safe band GpdForge.Profiles.ModeProfiles.Set clamps to. Kept as local constants
    // (rather than a cross-namespace reference) so this parser has zero dependencies beyond text
    // in, profiles out.
    public const int MinStapmW = 5, MaxStapmW = 40;
    public const int MinFastW = 5, MaxFastW = 45;
    public const int MinSlowW = 5, MaxSlowW = 45;
    public const int MinTctlC = 60, MaxTctlC = 95;

    // Fallbacks for a field a profile doesn't set — the "windows" mode baseline (see ModeProfiles),
    // a reasonable general-purpose default for whatever an incomplete profile leaves unspecified.
    public const int DefaultStapmW = 15, DefaultFastW = 20, DefaultSlowW = 17, DefaultTctlC = 92;

    private static readonly string[] StapmKeys = ["stapm", "stapmw", "tdp", "pl1", "sustained", "sustainedw"];
    private static readonly string[] FastKeys = ["fast", "fastw", "fastlimit", "pl2", "boost", "turbo"];
    private static readonly string[] SlowKeys = ["slow", "sloww", "slowlimit", "pl3", "average", "averagew"];
    private static readonly string[] TctlKeys = ["tctl", "tctlc", "tctltemp", "temp", "tempc", "thermal", "thermallimit"];

    /// <summary>
    /// Parse MotionAssistant-style .ini text into one profile per <c>[Section]</c>, in the order
    /// sections first appear. Keys before the first section header, blank lines, comments, and any
    /// line that isn't a recognizable <c>[Section]</c> or <c>key=value</c> are ignored. Missing keys
    /// fall back to defaults; every numeric value is clamped into its safe band. Never throws — the
    /// worst outcome for garbage input is an empty list.
    /// </summary>
    public static IReadOnlyList<ImportedProfile> ParseIni(string text)
    {
        var profiles = new List<ImportedProfile>();
        if (string.IsNullOrWhiteSpace(text)) return profiles;

        var order = new List<string>();
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;

        foreach (var rawLine in text.Split('\n'))
        {
            string line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;

            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                string name = end > 1 ? line[1..end].Trim() : string.Empty;
                if (name.Length == 0) continue; // "[]" / unterminated / blank name -> junk

                current = name;
                if (!sections.ContainsKey(name))
                {
                    sections[name] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    order.Add(name);
                }
                continue;
            }

            if (current is null) continue; // keys before any section header: no profile to attach to

            int eq = line.IndexOf('=');
            if (eq <= 0) continue; // not "key=value" shaped -> junk line
            string key = line[..eq].Trim();
            if (key.Length == 0) continue;
            sections[current][key] = line[(eq + 1)..].Trim();
        }

        foreach (var name in order)
            profiles.Add(BuildProfile(name, sections[name]));
        return profiles;
    }

    private static string StripComment(string line)
    {
        int i = line.IndexOfAny([';', '#']);
        return i < 0 ? line : line[..i];
    }

    private static ImportedProfile BuildProfile(string name, Dictionary<string, string> kv) => new(
        name,
        ReadField(kv, StapmKeys, DefaultStapmW, MinStapmW, MaxStapmW),
        ReadField(kv, FastKeys, DefaultFastW, MinFastW, MaxFastW),
        ReadField(kv, SlowKeys, DefaultSlowW, MinSlowW, MaxSlowW),
        ReadField(kv, TctlKeys, DefaultTctlC, MinTctlC, MaxTctlC));

    private static int ReadField(Dictionary<string, string> kv, string[] aliases, int fallback, int min, int max)
    {
        foreach (var alias in aliases)
            if (kv.TryGetValue(alias, out var raw) && TryParseNumber(raw, out double n))
                return Clamp((int)Math.Round(n), min, max);
        return Clamp(fallback, min, max);
    }

    /// <summary>Tolerant numeric parse: strips a trailing unit like "25W" / "92C" / "92°C".</summary>
    private static bool TryParseNumber(string raw, out double value)
    {
        string s = raw.Trim();
        int end = s.Length;
        while (end > 0 && !char.IsDigit(s[end - 1]) && s[end - 1] != '.') end--;
        return double.TryParse(s[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
}

/// <summary>Enumerates + reads the real MotionAssistant profile .ini files. Abstracted so callers
/// (and their tests) don't need to touch the real filesystem — only <see cref="FileIniSource"/>
/// does, behind this interface.</summary>
public interface IIniFileSource
{
    /// <summary>The directory MotionAssistant stores its per-profile .ini files in.</summary>
    string ProfilesDirectory { get; }

    /// <summary>True if that directory currently exists.</summary>
    bool DirectoryExists();

    /// <summary>Raw text of every <c>*.ini</c> file directly under <see cref="ProfilesDirectory"/>.
    /// Empty if the directory is absent. A single unreadable file is skipped rather than failing
    /// the whole read — never throws.</summary>
    IReadOnlyList<string> ReadAllIniFiles();
}

/// <summary>Real, read-only filesystem source. Not gated behind GPDFORGE_ENABLE_HARDWARE — reading
/// another app's saved profile files is not a hardware/BIOS write, the same trust level as the WMI
/// reads elsewhere in this repo (DisplayService, BatteryService, WmiVramReader).</summary>
public sealed class FileIniSource(string? profilesDirectory = null) : IIniFileSource
{
    public const string DefaultProfilesDirectory = @"C:\Program Files\Motion Assistant\Profiles";

    public string ProfilesDirectory { get; } = profilesDirectory ?? DefaultProfilesDirectory;

    public bool DirectoryExists() => Directory.Exists(ProfilesDirectory);

    public IReadOnlyList<string> ReadAllIniFiles()
    {
        if (!DirectoryExists()) return [];
        var texts = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ProfilesDirectory, "*.ini"))
        {
            try { texts.Add(File.ReadAllText(file)); }
            catch { /* skip an unreadable/locked file rather than failing the whole import */ }
        }
        return texts;
    }
}
