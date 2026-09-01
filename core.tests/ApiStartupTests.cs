// GPD Forge — the daemon actually starts, and every route it advertises answers. GPL-3.0-or-later.
//
// This closes a structural hole the rest of the suite cannot cover, and it was opened three separate
// times on 2026-08-30 alone:
//
//   1. Removing one DI block took the registrations below it with it. Build green, 828 unit tests
//      green, and EVERY endpoint returned 500 — including /health — because ASP.NET could not build
//      an endpoint whose services were gone, and endpoint routing throws for the WHOLE app, not for
//      one route.
//   2. IProcessRunner was never registered at all. Types took it as an optional constructor argument
//      and quietly received null; the one that genuinely needed it returned 500.
//   3. The same shape again with the GPU services.
//
// None are compile errors — DI resolves at runtime — and none are reachable by a test that news up a
// class directly. Only starting the host finds them.
//
// It launches the REAL daemon as a process rather than using WebApplicationFactory. That is a
// deliberate trade: the factory is faster but wraps the app in its own host, and what failed here was
// the actual startup path of the actual binary. Running the thing under test also means this exercises
// the same code path the installer verifies on the device, rather than a near-enough approximation.
//
// GPDFORGE_PORT keeps it off 8787, so it can never disturb an installed service. Hardware gates are
// explicitly cleared: this must not touch the EC, the GPU or power limits on whatever machine runs it.
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using GpdForge.Alerts;
using Xunit;

namespace GpdForge.Core.Tests;

/// <summary>Starts one daemon for the whole class and shuts it down afterwards. Starting a process
/// per test would multiply a 10-second cost by every route.</summary>
public sealed class DaemonUnderTest : IDisposable
{
    /// <summary>An unusual port on purpose: 8787 belongs to a real installed service, and 8790 was
    /// found occupied by something else entirely on the reference machine.</summary>
    public const int Port = 8846;

    private readonly Process? _process;
    private readonly string _dataDir;

    public string BaseUrl { get; } = $"http://127.0.0.1:{Port}";
    public HttpClient Client { get; }
    public string StartupLog => _log.ToString();

    private readonly System.Text.StringBuilder _log = new();

    public DaemonUnderTest()
    {
        Client = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(20) };

        // Isolated state, for two reasons that are easy to conflate.
        //
        // The first is damage: until 2026-08-31 this fixture ran against %ProgramData%\GPD Forge, so
        // every test run read and WROTE the installed service's alerts, sessions and app rules on
        // whatever machine ran the suite.
        //
        // The second matters more for what these tests prove. A daemon whose input is the machine's
        // history passes for reasons nobody chose: on the reference device /alerts held 13 real
        // entries and the contract's item checks ran; on a clean CI runner the array is empty, every
        // item check is skipped, and the guard reports success having verified nothing. Seeding it
        // makes the shape checks say the same thing everywhere.
        _dataDir = Path.Combine(Path.GetTempPath(), "gpdforge-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dataDir);
        SeedAlerts(_dataDir);

        var dll = Path.Combine(AppContext.BaseDirectory, "GpdForge.Service.dll");
        if (!File.Exists(dll)) return;   // Started == false; the fixture-level test reports it

        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.Environment["GPDFORGE_PORT"] = Port.ToString();
        // Cleared, not merely unset: an inherited gate would let a test suite write to an EC.
        psi.Environment["GPDFORGE_ENABLE_HARDWARE"] = "0";
        psi.Environment["GPDFORGE_ENABLE_FAN_CONTROL"] = "0";
        psi.Environment["GPDFORGE_ENABLE_GPU_PROFILES"] = "0";
        psi.Environment["GPDFORGE_AUTO_PROFILES"] = "0";
        psi.Environment[GpdForge.SystemControl.DataRoot.OverrideVariable] = _dataDir;

        _process = Process.Start(psi);
        if (_process is null) return;

        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) _log.AppendLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _log.AppendLine(e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        WaitForPort(TimeSpan.FromSeconds(45));
    }

    public bool Started { get; private set; }

    private void WaitForPort(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process?.HasExited == true) return;   // died on startup; the log explains it
            try
            {
                using var probe = new TcpClient();
                probe.Connect("127.0.0.1", Port);
                Started = true;
                return;
            }
            catch (SocketException) { Thread.Sleep(250); }
        }
    }

    /// <summary>
    /// Writes one alert of every severity through the real <see cref="AlertStore"/> rather than by
    /// hand-authoring alerts.json. Hand-authoring would mean encoding the store's naming policy and
    /// enum handling in a second place, and a seed that drifts out of that format loads as zero
    /// alerts — which looks exactly like a clean machine and re-creates the blind spot this seeding
    /// exists to remove.
    /// </summary>
    private static void SeedAlerts(string dataDir)
    {
        var store = new AlertStore(dataDir);
        store.Publish(AlertCategory.Thermal, AlertSeverity.Info, "Seeded info",
            "Contract fixture — deterministic input for the shape checks.");
        store.Publish(AlertCategory.Hardware, AlertSeverity.Aviso, "Seeded warning",
            "Contract fixture — deterministic input for the shape checks.");
        store.Publish(AlertCategory.Service, AlertSeverity.Critica, "Seeded critical",
            "Contract fixture — deterministic input for the shape checks.");
    }

    public void Dispose()
    {
        Client.Dispose();
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            _process?.Dispose();
        }
        catch { /* the daemon is a child of this test run; nothing survives it that matters */ }

        // Best-effort: a leftover temp directory is noise, not a failure, and throwing here would
        // turn a cleanup problem into a red suite.
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch { /* the daemon may still hold a handle; %TEMP% is cleaned by the OS anyway */ }
    }
}

/// <summary>
/// Shares ONE daemon across every class that needs it. It has to be a collection fixture rather than
/// a class fixture: xUnit constructs a class fixture per class, and the daemon binds a fixed port, so
/// the second class to run would get a daemon that could not listen — reported as twenty unrelated
/// assertion failures rather than as "the port was taken".
/// </summary>
[CollectionDefinition(Name)]
public sealed class DaemonCollection : ICollectionFixture<DaemonUnderTest>
{
    public const string Name = "the daemon under test";
}

[Collection(DaemonCollection.Name)]
public class ApiStartupTests(DaemonUnderTest daemon)
{
    /// <summary>
    /// Every route a client depends on. Adding an endpoint means adding it here — the list is the
    /// point: it is what turns a silently-unbuildable endpoint into a failing test rather than a 500
    /// the user finds first.
    /// </summary>
    public static TheoryData<string> Routes =>
    [
        "/health",
        "/version",
        "/telemetry",
        "/mode",
        "/fan",
        "/profiles",
        "/app-rules",
        "/ai",
        "/ai/inference-hold",
        "/gpu",
        "/gpu/desired",
        "/audit",
        "/standby",
        "/standby/hibernate",
        "/firmware",
        "/guardian",
        "/battery/budget",
        "/history",
        "/alerts",
        "/settings/export",
    ];

    [Fact]
    public void The_daemon_starts_at_all()
    {
        // Reported as its own test so a startup crash reads as "the daemon did not start" rather than
        // twenty identical connection failures that bury the reason.
        Assert.True(daemon.Started,
            "The daemon did not start. Its output was:\n" + daemon.StartupLog);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Every_route_answers_with_json(string route)
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        var res = await daemon.Client.GetAsync(route);
        var body = await res.Content.ReadAsStringAsync();

        Assert.True(res.IsSuccessStatusCode,
            $"GET {route} returned {(int)res.StatusCode}. Body: {body[..Math.Min(400, body.Length)]}");

        // A 2xx alone proves nothing here: this app serves an SPA fallback that answers 200 with
        // index.html for unknown paths, so a deleted route would still pass a status check. Requiring
        // parseable JSON is what makes this assert mean "the endpoint exists and ran".
        var ex = Record.Exception(() => JsonDocument.Parse(body));
        Assert.True(ex is null,
            $"GET {route} did not return JSON — likely the SPA fallback. Body starts: {body[..Math.Min(120, body.Length)]}");
    }

    [Fact]
    public async Task An_unknown_route_is_refused_rather_than_answered()
    {
        // The guard that keeps the theory above meaningful: if a nonexistent path returned success,
        // then "every route answers" would be true of routes that do not exist.
        //
        // Note what this does NOT assert. The installed daemon serves an SPA fallback from wwwroot,
        // which answers 200 with index.html for unknown paths — that is why the theory demands
        // parseable JSON rather than just a 2xx. Here there is no wwwroot, so the daemon answers a
        // JSON 404 instead. Both are correct; the invariant common to both is that an unknown route
        // is not reported as success.
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        var res = await daemon.Client.GetAsync("/definitely-not-an-endpoint");

        Assert.False(res.IsSuccessStatusCode,
            $"An unknown route answered {(int)res.StatusCode}, so 'every route answers' would prove nothing.");
    }
}
