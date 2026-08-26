// GPD Forge — update-check version comparison (pure). GPL-3.0-or-later.
using System.Globalization;

namespace GpdForge.Update;

/// <summary>
/// Dependency-free, semver-ish comparison — good enough for GitHub release tags like "v0.2.0" or
/// "1.3.0". No I/O; deterministic; unit-tested directly.
/// </summary>
public static class VersionCompare
{
    /// <summary>
    /// True if <paramref name="latest"/> is a strictly newer version than <paramref name="current"/>.
    /// Tolerates an optional leading "v"/"V" and 1–4 dot-separated numeric parts — missing trailing
    /// parts are treated as 0, so "1.2" == "1.2.0" and "1.3" &gt; "1.2.9". A trailing pre-
    /// release/build suffix ("1.2.0-beta.1", "1.2.0+5") is ignored; only the numeric release is
    /// compared. Unparseable input on EITHER side is treated as NOT newer — this never throws, and
    /// never claims an update it can't actually verify.
    /// </summary>
    public static bool IsNewer(string? latest, string? current)
    {
        int[]? l = Parse(latest);
        int[]? c = Parse(current);
        if (l is null || c is null) return false;

        for (int i = 0; i < l.Length; i++)
        {
            if (l[i] != c[i]) return l[i] > c[i];
        }
        return false; // every part equal
    }

    private static int[]? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];

        int cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0) s = s[..cut];
        if (s.Length == 0) return null;

        string[] parts = s.Split('.');
        if (parts.Length is 0 or > 4) return null;

        var nums = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (i >= parts.Length) { nums[i] = 0; continue; }
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 0)
                return null;
            nums[i] = n;
        }
        return nums;
    }
}
