// GPD Forge — the list of routes the daemon actually registers, read from source. GPL-3.0-or-later.
//
// Why parse the source rather than ask the running host? Because the daemon under test runs as a
// separate PROCESS (see DaemonUnderTest, and the reasons it must), so EndpointDataSource is on the
// wrong side of a process boundary. The alternative — exposing a /routes endpoint — would add
// production surface for a test's benefit, which is a worse trade than a regex over one file.
//
// The parser is fragile by nature, and that fragility is handled rather than ignored: callers assert
// a plausible route COUNT before trusting the result. A parser that silently returns nothing turns a
// coverage check into a test that always passes, which is the worst failure mode available to it.
using System.Text.RegularExpressions;

namespace GpdForge.Core.Tests;

public sealed record RegisteredRoute(string Method, string Path);

public static partial class ProgramRoutes
{
    [GeneratedRegex(@"app\.Map(Get|Post|Put|Delete)\(""(?<path>/[^""]*)""", RegexOptions.Compiled)]
    private static partial Regex MapCall();

    public static IReadOnlyList<RegisteredRoute> Parse()
    {
        var source = File.ReadAllText(FindProgramCs());
        var routes = new List<RegisteredRoute>();

        foreach (Match m in MapCall().Matches(source))
        {
            var method = m.Groups[1].Value.ToUpperInvariant();
            var path = m.Groups["path"].Value;
            routes.Add(new RegisteredRoute(method, path));
        }

        return routes;
    }

    /// <summary>
    /// Walks up from the test assembly to the repository root. Anchored on a file that only exists
    /// at the root and that this repository genuinely depends on — <c>Directory.Build.props</c> holds
    /// the single declared version — so a rename would be a deliberate act, not a silent drift.
    /// </summary>
    private static string FindProgramCs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                var program = Path.Combine(dir.FullName, "core", "Program.cs");
                if (File.Exists(program)) return program;

                throw new FileNotFoundException(
                    $"Found the repository root at {dir.FullName} but no core/Program.cs beneath it. " +
                    "The route-coverage tests cannot run.", program);
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the repository root above {AppContext.BaseDirectory} (looking for " +
            "Directory.Build.props). The route-coverage tests need the source tree, so they cannot " +
            "run from a published test bundle.");
    }
}
