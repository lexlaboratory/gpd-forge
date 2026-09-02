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

    // ---------------------------------------------------------------------------------------------
    // The four copies the first version of this guard did not cover.
    //
    // On 2026-09-01 the mode list was consolidated into ModeCatalogue and this file was written to
    // stop it scattering again. It checked types.ts, shared.tsx and the mock daemon — and MISSED four
    // more, every one of which was still on five modes a day later. The guard was written to prevent
    // exactly the drift it then allowed, which is worse than having no guard: it produced confidence.
    //
    // Data-driven now, so adding a fifth surface is a row rather than a method.
    // ---------------------------------------------------------------------------------------------

    public static TheoryData<string, string, string> ModeListSurfaces => new()
    {
        // file, a marker that must be present (fails loudly if the shape changed), how a mode appears
        { "mcp/server.mjs",                "const MODES = [",        "'{0}'" },
        { "scripts/forge-hotkeys.ps1",     "$modes = @(",            "'{0}'" },
        { "ui/src/pages/ProfilesPage.tsx", "FALLBACK_MODES",         "'{0}'" },
        { "ui/src/CommandPalette.tsx",     "MODE_IDS",               "'{0}'" },
    };

    [Theory]
    [MemberData(nameof(ModeListSurfaces))]
    public void Every_mode_list_outside_the_catalogue_stays_in_step(string relativePath, string marker, string format)
    {
        var source = ReadRepoFile(relativePath.Split('/'));

        // The guard's own guard: if the declaration is renamed, this must fail LOUDLY rather than
        // scan a file that no longer contains what it thinks it does and report success.
        Assert.True(source.Contains(marker, StringComparison.Ordinal),
            $"{relativePath} no longer contains '{marker}'. The list was renamed or moved, so this " +
            "check is scanning for something that is not there and proves nothing until it is fixed.");

        // Two of these surfaces carry SELECTABLE modes only, and both for the same reason standby is
        // excluded from AppRulePolicy: it is a system state applied by the resume restore, not
        // something a person picks.
        //   forge-hotkeys.ps1  — cycling into standby with a keystroke would be a trap.
        //   ProfilesPage.tsx   — it is the fallback for which modes a per-app RULE may select, which
        //                        is what its own comment says. The first version of this test demanded
        //                        all six here and went red; the file was right and the test was wrong.
        var selectableOnly = relativePath.EndsWith("forge-hotkeys.ps1", StringComparison.Ordinal)
                          || relativePath.EndsWith("ProfilesPage.tsx", StringComparison.Ordinal);
        var expected = selectableOnly ? ModeCatalogue.SelectableIds : ModeCatalogue.Ids;

        var missing = expected
            .Where(id => !source.Contains(string.Format(format, id), StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            $"{relativePath} is missing: {string.Join(", ", missing)}. " +
            "A mode the daemon supports that this surface does not offer is invisible to whoever uses it.");
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
