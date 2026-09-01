// GPD Forge — the REAL daemon is validated against the shared API contract. GPL-3.0-or-later.
//
// ApiStartupTests proves every route answers with parseable JSON. That is necessary and it is not
// enough: on 2026-08-28 /alerts answered 200 with perfectly valid JSON, and the app was unusable.
// AlertSeverity serialised as the ordinal 1 instead of "Aviso", the UI called .toLowerCase() on a
// number, React unmounted, and the window went black. Status and parseability were both fine. The
// SHAPE was wrong.
//
// The suite could not catch it. tests/e2e runs against the mock daemon, which had always emitted
// names, so a contract was being checked against its own replica. AlertWireFormatTests was written
// afterwards and helps, but note what it does: it builds its own JsonSerializerOptions that
// "mirror the ones Program.cs installs". That is the same trap one level up — deleting the
// converter from Program.cs leaves that test green, because it never reads Program.cs.
//
// This file closes that. It talks to the real daemon over HTTP and compares what actually came off
// the wire to tests/contract/api-contract.json, which the Playwright suite checks the mock against.
// Neither side is compared to the other; both are compared to the contract.
using System.Text.Json;
using Xunit;

namespace GpdForge.Core.Tests;

[Collection(DaemonCollection.Name)]
public class ApiContractTests(DaemonUnderTest daemon)
{
    public static TheoryData<string> GetRoutesWithShape
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var r in ApiContract.Load().Routes)
                if (r.Method == "GET" && r.Shape is not null && !r.Path.Contains('{'))
                    data.Add(r.Path);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(GetRoutesWithShape))]
    public async Task Response_matches_the_declared_shape(string path)
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        var route = ApiContract.Load().Routes.Single(r => r.Method == "GET" && r.Path == path);
        var res = await daemon.Client.GetAsync(path);
        var body = await res.Content.ReadAsStringAsync();

        Assert.True(res.IsSuccessStatusCode,
            $"GET {path} returned {(int)res.StatusCode}. Body: {Truncate(body, 400)}");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException ex)
        {
            Assert.Fail($"GET {path} did not return JSON ({ex.Message}). " +
                        $"Most likely the SPA fallback answered. Body starts: {Truncate(body, 120)}");
            return;
        }

        using (doc)
        {
            var problems = ApiContract.Validate(doc.RootElement, route.Shape!.Value, path);
            Assert.True(problems.Count == 0,
                $"GET {path} does not match the contract in tests/contract/api-contract.json:\n" +
                string.Join("\n", problems.Select(p => "  - " + p)) +
                $"\n\nActual body: {Truncate(body, 600)}");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Route coverage. Without this, the contract only constrains what someone remembered to declare,
    // and a new endpoint ships with no mock and no E2E — exactly how /audit, /firmware and
    // /standby/hibernate reached v0.2.0 untested through the UI.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Every_route_the_daemon_registers_is_declared_in_the_contract()
    {
        var registered = ProgramRoutes.Parse();

        // The guard's own guard. If the parse returns nothing — because someone changed how routes
        // are registered — this test would otherwise pass by finding no discrepancies, which is the
        // most dangerous way for a coverage check to fail.
        Assert.True(registered.Count > 20,
            $"Only {registered.Count} routes were parsed out of core/Program.cs. The parser has " +
            "stopped matching the registration style, so this test proves nothing until it is fixed.");

        var declared = ApiContract.Load().Routes
            .Select(r => $"{r.Method} {Normalise(r.Path)}")
            .ToHashSet(StringComparer.Ordinal);

        var undeclared = registered
            .Select(r => $"{r.Method} {Normalise(r.Path)}")
            .Where(r => !declared.Contains(r))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(undeclared.Count == 0,
            "The daemon registers routes that tests/contract/api-contract.json does not declare, so " +
            "nothing verifies the mock daemon implements them and no E2E test can reach them:\n" +
            string.Join("\n", undeclared.Select(r => "  - " + r)));
    }

    [Fact]
    public void The_contract_declares_no_route_the_daemon_does_not_register()
    {
        var registered = ProgramRoutes.Parse()
            .Select(r => $"{r.Method} {Normalise(r.Path)}")
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(registered.Count > 20, "The Program.cs route parser found almost nothing; see above.");

        var phantom = ApiContract.Load().Routes
            .Where(r => !r.MockOnly)   // mock-only routes are declared as such and are not phantoms
            .Select(r => $"{r.Method} {Normalise(r.Path)}")
            .Where(r => !registered.Contains(r))
            .Order(StringComparer.Ordinal)
            .ToList();

        // A contract entry with no endpoint behind it is worse than a missing one: the mock will be
        // built to serve it, the E2E suite will pass against it, and the route will 404 in
        // production. /telemetry/stream existed in the mock and nowhere else until 2026-08-31.
        Assert.True(phantom.Count == 0,
            "The contract declares routes the daemon does not register. A mock built to satisfy " +
            "these would make the E2E suite pass against endpoints that 404 in production:\n" +
            string.Join("\n", phantom.Select(r => "  - " + r)));
    }

    /// <summary>Route parameters are named differently in ASP.NET and in the contract
    /// (<c>{id:guid}</c> vs <c>{id}</c>); compare structure, not the constraint syntax.</summary>
    private static string Normalise(string path)
        => System.Text.RegularExpressions.Regex.Replace(path, @"\{[^}]*\}", "{}");

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
