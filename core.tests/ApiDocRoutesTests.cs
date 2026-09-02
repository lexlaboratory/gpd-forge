// GPD Forge — docs/api.md describes routes that exist, and every route is described. GPL-3.0-or-later.
//
// This exists because correcting the document by hand did not hold. On 2026-09-02 a sweep of
// docs/api.md against the real route table found it advertising `GET /jobs/:id` — a route that has
// never been registered — together with three fields on the job record that have never existed
// (`startedAt`, `finishedAt`, `log`), two statuses nothing can produce, and a scheduler that does
// not exist at all: `cmd` is validated for emptiness, stored, and never passed to a process.
//
// The failure mode is specific and worth naming. Nobody reading the daemon would write those claims;
// they were written when the endpoint was planned and never re-checked, and prose has no compiler.
// An external agent integrating against this document would have polled a 404 forever and concluded
// its batch was still running.
//
// So the document is now checked mechanically. Not for wording — for the one thing a machine can
// decide: does the path exist.
using System.Text.RegularExpressions;
using Xunit;

namespace GpdForge.Core.Tests;

public class ApiDocRoutesTests
{
    /// <summary>
    /// Documented paths that deliberately do not appear in Program.cs. Each needs a reason, and the
    /// reason is asserted to still hold below where it can be — an allowlist nobody re-examines is
    /// how the drift got in.
    /// </summary>
    public static readonly (string Route, string Why)[] KnownAbsent =
    [
        ("GET /telemetry/stream",
            "Mock-only SSE for the dev UI. api.md heads the section '(mock only)' and states plainly " +
            "that production has no streaming endpoint, so this is a documented absence, not a claim."),
        ("POST /profiles/ai",
            "A concrete instance of the registered POST /profiles/{mode}, cited in prose about the AI " +
            "flow. The parameterised route is what exists; naming one mode is not a separate route."),
        ("GET /jobs/:id",
            "Appears ONLY inside the paragraph that documents it as a former false claim. The route " +
            "does not exist and the document now says so; this test cannot tell a quoted lie from a " +
            "live one, so the exemption is recorded here instead."),
    ];

    private static readonly Regex RealRoute =
        new(@"app\.Map(Get|Post|Put|Delete|Patch)\(\s*""([^""]+)""", RegexOptions.Compiled);

    // Stops at whitespace, a backtick or a '?': query strings are not part of the path, and treating
    // them as such is what made the first version of this sweep report five false findings.
    private static readonly Regex DocRoute =
        new(@"`(GET|POST|PUT|DELETE|PATCH) (/[^`\s?]*)", RegexOptions.Compiled);

    /// <summary>`:id` in prose and `{id:guid}` in the route table are the same path segment.</summary>
    private static string Normalise(string route) =>
        Regex.Replace(Regex.Replace(route, @"\{[^}]+\}", ":x"), @":[A-Za-z]+(?!x\b)", ":x").TrimEnd('/');

    private static Dictionary<string, string> RealRoutes()
    {
        var src = RepoFile.Read("core", "Program.cs");
        Assert.Contains("app.MapGet(", src, StringComparison.Ordinal);   // guard's own guard
        return RealRoute.Matches(src)
            .Select(m => $"{m.Groups[1].Value.ToUpperInvariant()} {m.Groups[2].Value}")
            .GroupBy(Normalise).ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>Every route the document mentions anywhere, prose included. Used for the "does this
    /// path exist" direction, where a mention is enough to be a claim worth checking.</summary>
    private static Dictionary<string, string> MentionedRoutes()
    {
        var doc = RepoFile.Read("docs", "api.md");
        Assert.Contains("## Types", doc, StringComparison.Ordinal);      // guard's own guard
        return DocRoute.Matches(doc)
            .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}")
            .GroupBy(Normalise).ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>
    /// Routes in a DOCUMENTING position — a section heading, or a bullet opening with a code span.
    ///
    /// The distinction is the whole value of the check below, and I got it wrong first: the original
    /// version searched the document as one string, so a route counted as documented if its name
    /// appeared anywhere at all. Falsifying it exposed that — deleting the `GET /tdp` heading left
    /// the test green, because two sentences elsewhere still said "see `GET /tdp`". Tightening it to
    /// this rule immediately found `GET /audit`: registered, mentioned twice in passing, and with no
    /// section anywhere — the endpoint that returns every hardware write the daemon has made.
    /// </summary>
    private static Dictionary<string, string> DocumentedRoutes()
    {
        var lines = RepoFile.Read("docs", "api.md").Split('\n');
        var declaring = lines.Where(l =>
            l.StartsWith("###", StringComparison.Ordinal) || Regex.IsMatch(l, @"^\s*[-*]\s+`"));

        return declaring
            .SelectMany(l => DocRoute.Matches(l).Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}"))
            .GroupBy(Normalise).ToDictionary(g => g.Key, g => g.First());
    }

    [Fact]
    public void Every_route_docs_api_md_advertises_actually_exists()
    {
        var real = RealRoutes();
        var exempt = KnownAbsent.Select(k => Normalise(k.Route)).ToHashSet(StringComparer.Ordinal);

        var ghosts = MentionedRoutes()
            .Where(d => !real.ContainsKey(d.Key) && !exempt.Contains(d.Key))
            .Select(d => d.Value)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(ghosts.Count == 0,
            $"docs/api.md documents {ghosts.Count} route(s) that are not registered in Program.cs: " +
            $"{string.Join(", ", ghosts)}. A client written against this document would get a 404. " +
            "If the absence is deliberate, add it to KnownAbsent with the reason.");
    }

    [Fact]
    public void Every_route_the_daemon_serves_is_documented()
    {
        var documented = DocumentedRoutes();

        var undocumented = RealRoutes()
            .Where(r => !documented.ContainsKey(r.Key))
            .Select(r => r.Value)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        // Two findings here on 2026-09-02: GET /tdp, added earlier the same day with nothing
        // describing it, and GET /audit, which had been serving the daemon's complete hardware-write
        // history for months with no section of its own. Undocumented is a milder fault than
        // invented, but it is the same drift running the other way — and the audit log is exactly
        // the endpoint an undocumented existence keeps unexamined.
        Assert.True(undocumented.Count == 0,
            $"{undocumented.Count} route(s) exist but docs/api.md gives them no section or bullet " +
            $"of their own: {string.Join(", ", undocumented)}. A passing mention in prose does not " +
            "tell a client what the route returns.");
    }

    [Fact]
    public void The_known_absent_list_does_not_outlive_its_reasons()
    {
        var real = RealRoutes();

        // An exemption for a route that HAS since been implemented is stale, and would go on
        // suppressing a real check forever. This is the half of an allowlist that usually rots.
        var implemented = KnownAbsent
            .Where(k => real.ContainsKey(Normalise(k.Route)))
            .Select(k => k.Route)
            .ToList();

        Assert.True(implemented.Count == 0,
            $"These are exempted as non-existent but Program.cs now registers them: " +
            $"{string.Join(", ", implemented)}. Remove them from KnownAbsent.");

        Assert.All(KnownAbsent, k => Assert.True(k.Why.Length > 40,
            $"{k.Route} is exempted without a usable reason."));
    }

    [Fact]
    public void The_jobs_section_still_warns_that_nothing_runs_the_job()
    {
        // The narrow, load-bearing claim: POST /jobs records a command and never executes it. If a
        // job executor is ever written this test goes red, which is the right moment to rewrite the
        // section — rather than the warning quietly becoming the new false statement.
        var program = RepoFile.Read("core", "Program.cs");
        Assert.DoesNotContain("Process.Start(job", program, StringComparison.Ordinal);

        var doc = RepoFile.Read("docs", "api.md");
        Assert.True(doc.Contains("It does not run them", StringComparison.Ordinal),
            "docs/api.md no longer warns that POST /jobs never executes cmd. Nothing in Program.cs " +
            "passes Job.Cmd to a process, so removing the warning restores the original false claim.");
    }
}

/// <summary>Walks up to the repository root, anchored on Directory.Build.props. Throws loudly rather
/// than returning empty — a doc check that silently reads nothing would pass forever.</summary>
internal static class RepoFile
{
    public static string Read(params string[] relative)
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
