// GPD Forge — the mode list stays in one place. GPL-3.0-or-later.
//
// Before ModeCatalogue existed, five modes were enumerated in seven places that did not know about
// each other. Adding one meant finding all seven, and missing one failed QUIETLY: a mode with a TDP
// preset and no GPU profile applies whatever Radeon settings the previous mode left behind; a mode
// missing from AppRulePolicy simply cannot be selected by a rule, and says nothing about why.
//
// Consolidating solved it once. These tests are what stop it happening again — including across the
// language boundary, where a C# catalogue cannot enforce anything on its own.
using System.Text.RegularExpressions;
using GpdForge.Gpu;
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

public class ModeCatalogueTests
{
    [Fact]
    public void Every_mode_has_a_TDP_preset()
    {
        var missing = ModeCatalogue.Ids.Where(id => ModeProfiles.For(id) is null).ToList();
        Assert.True(missing.Count == 0,
            $"Modes with no TDP preset: {string.Join(", ", missing)}. Selecting one would apply " +
            "whatever the previous mode left on the silicon.");
    }

    [Fact]
    public void Every_selectable_mode_has_a_GPU_profile()
    {
        // Standby is excluded on purpose: it is a system state applied by the resume restore, not
        // something a user picks, and it has no Radeon opinion.
        var missing = ModeCatalogue.All
            .Where(m => m.SelectableByAppRule && GpuModeProfiles.For(m.Id) is null)
            .Select(m => m.Id)
            .ToList();

        Assert.True(missing.Count == 0,
            $"Selectable modes with no GPU profile: {string.Join(", ", missing)}. The Radeon settings " +
            "of the PREVIOUS mode would stay applied, which is worse than having none.");
    }

    [Fact]
    public void The_app_rule_policy_reads_from_the_catalogue()
    {
        Assert.Equal(ModeCatalogue.SelectableIds.Order(), AppRulePolicy.SelectableModes.Order());
    }

    [Fact]
    public void Standby_is_not_selectable_by_an_app_rule()
    {
        // A rule that put the machine into standby mode when an app took focus would be a trap.
        Assert.DoesNotContain(ModeCatalogue.Standby, AppRulePolicy.SelectableModes);
        Assert.False(AppRulePolicy.IsValidMode(ModeCatalogue.Standby));
    }

    [Fact]
    public void Exactly_one_mode_is_sustained_and_it_is_the_one_ModeProfiles_shapes()
    {
        var sustained = ModeCatalogue.All.Where(m => m.Sustained).Select(m => m.Id).ToList();
        Assert.Single(sustained);
        Assert.Equal(ModeProfiles.SustainedMode, sustained[0]);
    }

    [Fact]
    public void Mode_ids_are_url_and_storage_safe()
    {
        // Ids appear in URLs (POST /profiles/{mode}) and inside saved user rules, so a stray capital
        // or space would break a route or, worse, a rule someone saved months ago.
        foreach (var id in ModeCatalogue.Ids)
            Assert.Matches("^[a-z][a-z0-9-]*$", id);
    }

    [Fact]
    public void A_mode_with_a_frame_cap_does_not_also_run_the_auto_FPS_governor()
    {
        // The one pathological pairing, asserted at the level where it is decided rather than only
        // at the endpoints that reject it: a cap BELOW an active target makes the governor raise
        // power forever chasing frames the driver is withholding — hot, loud, no extra frames, and
        // no error anywhere. A mode that shipped with both would create that state by default.
        var both = ModeCatalogue.All
            .Where(m => m.RecommendedFrameCapFps is not null && m.AutoFpsEligible)
            .Select(m => m.Id)
            .ToList();

        Assert.True(both.Count == 0,
            $"These modes ask for a frame cap AND allow the auto-FPS governor: {string.Join(", ", both)}. " +
            "That is the pairing the API rejects, and shipping it as a default would mean the mode " +
            "arrives already in the state the rules exist to prevent.");
    }

    // ---------------------------------------------------------------------------------------------
    // Across the language boundary. A C# catalogue cannot enforce anything on the TypeScript union or
    // the mock daemon, and those are two of the seven places the list used to live.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_TypeScript_ModeId_union_lists_every_mode()
    {
        var source = ReadRepoFile("ui", "src", "types.ts");
        var match = Regex.Match(source, @"export type ModeId\s*=\s*(?<union>[^\r\n]+)");

        Assert.True(match.Success,
            "Could not find `export type ModeId` in ui/src/types.ts. The parser has stopped matching, " +
            "so this test proves nothing until it is fixed.");

        var union = match.Groups["union"].Value;
        var missing = ModeCatalogue.Ids.Where(id => !union.Contains($"'{id}'", StringComparison.Ordinal)).ToList();

        Assert.True(missing.Count == 0,
            $"ModeId does not include: {string.Join(", ", missing)}. The UI would reject the mode the " +
            $"daemon reports. Found: {union.Trim()}");
    }

    [Fact]
    public void The_mock_daemon_knows_every_mode()
    {
        // Without this, a mode ships, the E2E suite selects it against the mock, the mock answers
        // "unknown mode" with a 400, and the failure looks like a UI bug.
        var source = ReadRepoFile("tools", "mock-daemon", "server.mjs");
        var match = Regex.Match(source, @"const MODES = new Set\(\[(?<ids>[^\]]*)\]\)");

        Assert.True(match.Success,
            "Could not find `const MODES = new Set([...])` in the mock daemon. The parser has stopped " +
            "matching, so this test proves nothing until it is fixed.");

        var ids = match.Groups["ids"].Value;
        var missing = ModeCatalogue.Ids.Where(id => !ids.Contains($"'{id}'", StringComparison.Ordinal)).ToList();

        Assert.True(missing.Count == 0,
            $"The mock daemon does not accept: {string.Join(", ", missing)}. Every E2E test that " +
            "selects one would fail with a 400 that reads like a UI bug.");
    }

    [Fact]
    public void The_UI_offers_every_selectable_mode()
    {
        var source = ReadRepoFile("ui", "src", "pages", "shared.tsx");
        Assert.Contains("export const MODES", source);

        var missing = ModeCatalogue.All
            .Where(m => !source.Contains($"id: '{m.Id}'", StringComparison.Ordinal))
            .Select(m => m.Id)
            .ToList();

        Assert.True(missing.Count == 0,
            $"ui/src/pages/shared.tsx does not offer: {string.Join(", ", missing)}. The daemon would " +
            "support a mode with no way to pick it.");
    }

    /// <summary>Walks up to the repository root, anchored on Directory.Build.props — the same
    /// approach ProgramRoutes uses, and it throws loudly rather than returning empty.</summary>
    private static string ReadRepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                var path = Path.Combine([dir.FullName, .. relative]);
                if (File.Exists(path)) return File.ReadAllText(path);
                throw new FileNotFoundException($"Expected {path} beneath the repository root.", path);
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the repository root above {AppContext.BaseDirectory}.");
    }
}
