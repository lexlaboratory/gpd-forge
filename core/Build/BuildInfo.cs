// GPD Forge — what this build actually is, read from the binary rather than typed. GPL-3.0-or-later.
//
// On 2026-08-28 the app showed no telemetry while the daemon was healthy the whole time. The cause
// was that `GPD Forge.exe` in Program Files predated the commit that fixed it, and the only way that
// was established was diffing the installed binary against a fresh build hunting for marker strings
// that exist only after the fix. The source was innocent; the artefact was stale, and nothing on
// screen could say so.
//
// This type exists so that question is answerable by looking. It reports:
//   - Version: the product version, from Directory.Build.props via the assembly. Never a literal.
//   - Commit: the source revision the assembly was built from, when the build knew one. .NET appends
//     "+<sha>" to InformationalVersion for a repository build; null when it did not, because an
//     unknown commit must read as unknown rather than as "unversioned" or an empty string.
//   - BuiltUtc: the linker timestamp embedded in the PE header. This is the field that answers "is
//     the thing running older than the fix?" without any string archaeology.
//
// Everything here is derived from the loaded assembly, so it cannot disagree with the binary it
// describes — which is the entire point. A version a human retypes is a claim; this is a reading.
using System.Reflection;
using System.Runtime.InteropServices;

namespace GpdForge.Build;

/// <summary>Identity of the running build. <paramref name="Commit"/> and <paramref name="BuiltUtc"/>
/// are null when the build did not record them — unknown reads as unknown, never as a guess.</summary>
public sealed record BuildIdentity(string Version, string? Commit, DateTimeOffset? BuiltUtc);

public static class BuildInfo
{
    private static BuildIdentity? _cached;

    /// <summary>Identity of the assembly this code lives in. Computed once; it cannot change at runtime.</summary>
    public static BuildIdentity Current => _cached ??= Describe(typeof(BuildInfo).Assembly);

    /// <summary>Describes any assembly. Separated from <see cref="Current"/> so it is unit-testable
    /// against a known assembly without reflection over the test host's own identity.</summary>
    public static BuildIdentity Describe(Assembly asm)
    {
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var (version, commit) = SplitInformational(informational)
            // No informational attribute at all: fall back to the assembly version, which always
            // exists. It is less precise (no commit), so the commit stays null rather than invented.
            ?? (asm.GetName().Version?.ToString(3) ?? "unknown", null);

        return new BuildIdentity(version, commit, ReadLinkerTimestamp(asm));
    }

    /// <summary>
    /// Splits "1.2.3+abcdef" into its version and commit halves. Returns null when there is nothing
    /// usable, so the caller decides the fallback rather than receiving a fabricated one.
    /// </summary>
    public static (string Version, string? Commit)? SplitInformational(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return null;

        var plus = informational.IndexOf('+');
        if (plus < 0) return (informational.Trim(), null);

        var version = informational[..plus].Trim();
        var commit = informational[(plus + 1)..].Trim();
        if (version.Length == 0) return null;
        // An empty or placeholder suffix is not a commit. Report the absence honestly.
        return (version, commit.Length == 0 ? null : commit);
    }

    /// <summary>
    /// The PE header's TimeDateStamp for the assembly's own file, as UTC.
    ///
    /// Deterministic builds (the .NET default) replace this with a content hash rather than a clock,
    /// which would read as a date in 2100 or 1970 — a plausible-looking lie, which this repo does not
    /// ship. So the value is range-checked against the project's own lifetime, and anything outside
    /// it is reported as null. GPD Forge's service build already sets Deterministic=false when
    /// rebuilding to dodge a Smart App Control block, so a real timestamp is usually present.
    /// </summary>
    private static DateTimeOffset? ReadLinkerTimestamp(Assembly asm)
    {
        try
        {
            var path = asm.Location;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            var bytes = new byte[4];
            using (var fs = File.OpenRead(path))
            {
                // e_lfanew at 0x3C points at the PE signature; TimeDateStamp is 8 bytes past it.
                fs.Position = 0x3C;
                if (fs.Read(bytes, 0, 4) != 4) return null;
                var peHeader = BitConverter.ToInt32(bytes, 0);
                if (peHeader <= 0 || peHeader + 8 + 4 > fs.Length) return null;

                fs.Position = peHeader + 8;
                if (fs.Read(bytes, 0, 4) != 4) return null;
            }

            var stamp = DateTimeOffset.FromUnixTimeSeconds(BitConverter.ToUInt32(bytes, 0));
            return IsPlausibleBuildTime(stamp) ? stamp : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Reading our own file can fail (locked, or a single-file/trimmed layout). Unknown, not zero.
            return null;
        }
    }

    /// <summary>
    /// Whether a PE timestamp can be a real build time for this project rather than a deterministic
    /// content hash. Public so the boundary is testable — it is the difference between a useful fact
    /// and a confident fabrication.
    /// </summary>
    public static bool IsPlausibleBuildTime(DateTimeOffset stamp, DateTimeOffset? now = null)
    {
        var upper = (now ?? DateTimeOffset.UtcNow).AddDays(1);      // small clock skew, not a year
        var lower = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);  // predates the repo
        return stamp > lower && stamp < upper;
    }

    /// <summary>The .NET runtime and OS this build is running on — context for a bug report.</summary>
    public static string Runtime => RuntimeInformation.FrameworkDescription;
}
