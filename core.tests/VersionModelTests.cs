// GPD Forge — the version model: one source of truth, and proof that it stayed one. GPL-3.0-or-later.
//
// The version is declared in Directory.Build.props and copied into ui/package.json and
// ui/src-tauri/tauri.conf.json, because npm and Tauri each insist on their own field and neither can
// read MSBuild. Copies drift. These tests are what makes the drift a failing build instead of a
// support thread about a version number that means nothing.
using System.Text.Json;
using System.Text.RegularExpressions;
using GpdForge.Build;
using Xunit;

namespace GpdForge.Core.Tests;

public class VersionModelTests
{
    /// <summary>Walks up from the test binary to the repository root (the directory holding
    /// Directory.Build.props). Done by searching rather than by counting "..", so it survives a
    /// change of target framework or configuration in the output path.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;

        Assert.True(dir is not null, "Could not find Directory.Build.props above the test binary.");
        return dir!.FullName;
    }

    private static string DeclaredVersion()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var m = Regex.Match(props, @"<GpdForgeVersion>\s*([^<\s]+)\s*</GpdForgeVersion>");
        Assert.True(m.Success, "Directory.Build.props must declare <GpdForgeVersion>.");
        return m.Groups[1].Value;
    }

    private static string JsonVersion(params string[] relativePath)
    {
        var path = Path.Combine([RepoRoot(), .. relativePath]);
        Assert.True(File.Exists(path), $"Expected {path} to exist.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(
            doc.RootElement.TryGetProperty("version", out var v),
            $"{Path.GetFileName(path)} must carry a top-level \"version\".");
        return v.GetString()!;
    }

    [Fact]
    public void The_assembly_reports_the_declared_version_rather_than_a_literal()
    {
        // Proves the build actually consumed Directory.Build.props. If someone reintroduces a
        // hard-coded version, this diverges the moment the declared one is bumped.
        Assert.Equal(DeclaredVersion(), BuildInfo.Describe(typeof(BuildInfo).Assembly).Version);
    }

    [Fact]
    public void The_ui_package_version_matches_the_declared_version()
        => Assert.Equal(DeclaredVersion(), JsonVersion("ui", "package.json"));

    [Fact]
    public void The_tauri_shell_version_matches_the_declared_version()
    {
        // The shell version is what tells a user which window they are looking at. When it drifts from
        // the daemon, the app can report a fix as shipped while running the build that lacks it —
        // exactly the 2026-08-28 failure.
        Assert.Equal(DeclaredVersion(), JsonVersion("ui", "src-tauri", "tauri.conf.json"));
    }
}

public class BuildInfoTests
{
    [Theory]
    [InlineData("1.2.3+abc123", "1.2.3", "abc123")]
    [InlineData("1.2.3", "1.2.3", null)]
    [InlineData("  1.2.3+abc  ", "1.2.3", "abc")]
    public void An_informational_version_splits_into_version_and_commit(string input, string version, string? commit)
    {
        var split = BuildInfo.SplitInformational(input);
        Assert.NotNull(split);
        Assert.Equal(version, split!.Value.Version);
        Assert.Equal(commit, split.Value.Commit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+abc123")]   // a commit with no version is not a version
    public void An_unusable_informational_version_reports_nothing_rather_than_something(string? input)
        => Assert.Null(BuildInfo.SplitInformational(input));

    [Fact]
    public void An_empty_commit_suffix_is_reported_as_absent_not_as_empty_string()
    {
        // "1.2.3+" carries no commit. Returning "" here would put a blank where the UI shows a sha,
        // which reads as "built from nothing" rather than "not recorded".
        var split = BuildInfo.SplitInformational("1.2.3+");
        Assert.NotNull(split);
        Assert.Null(split!.Value.Commit);
    }

    [Fact]
    public void A_deterministic_build_stamp_is_rejected_instead_of_reported_as_a_date()
    {
        // Deterministic builds put a content hash in the PE TimeDateStamp field. Interpreted as unix
        // seconds it yields a confident, plausible-looking, wrong date. The whole value of builtUtc is
        // that it can be trusted, so an implausible one must be null.
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        Assert.False(BuildInfo.IsPlausibleBuildTime(DateTimeOffset.FromUnixTimeSeconds(0), now));
        Assert.False(BuildInfo.IsPlausibleBuildTime(new DateTimeOffset(2103, 4, 1, 0, 0, 0, TimeSpan.Zero), now));
        Assert.False(BuildInfo.IsPlausibleBuildTime(new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero), now));
    }

    [Fact]
    public void A_real_build_time_is_accepted()
    {
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        Assert.True(BuildInfo.IsPlausibleBuildTime(now.AddHours(-3), now));
    }

    [Fact]
    public void The_running_build_reports_a_version_and_never_throws()
    {
        var b = BuildInfo.Current;
        Assert.False(string.IsNullOrWhiteSpace(b.Version));
        Assert.NotEqual("unknown", b.Version);   // the assembly must carry a real version
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Runtime));
    }
}
