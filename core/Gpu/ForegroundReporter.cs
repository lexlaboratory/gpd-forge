// GPD Forge — tells the daemon which app is in front, from the user's session. GPL-3.0-or-later.
//
// The daemon runs in session 0, where GetForegroundWindow is always NULL (docs/adr/0002), so the FPS
// target and the per-app rules depend on this report (core/Profiles/SessionForegroundApp.cs). It was
// a line inside the GPU agent's loop until audit round 2 (2026-09-24), and that loop exited at once
// unless GPU profiles were enabled — off by default — so on a default install nothing reported and
// every foreground consumer was blind. It is its own piece now, run whatever that gate says.
//
// A refused report is a failure, not a success with an unread status: a 400 (a name the daemon will
// not take) or a 404 (a daemon older than the endpoint) used to vanish into a discarded response.
// Logged at Warning once per outage — every 3 s would bury the log — and once more when it recovers.
using System.Net.Http.Json;
using GpdForge.Profiles;
using Microsoft.Extensions.Logging;

namespace GpdForge.Gpu;

public sealed class ForegroundReporter(HttpClient http, IForegroundApp foreground, ILogger? logger = null)
{
    public const string Route = "/session/foreground";

    private bool _failing;

    /// <summary>One report. True when the daemon accepted it; never throws except on cancellation.</summary>
    public async Task<bool> ReportAsync(CancellationToken ct)
    {
        try
        {
            using var res = await http.PostAsJsonAsync(Route, new { process = foreground.Current() }, ct);
            res.EnsureSuccessStatusCode();
            if (_failing) logger?.LogInformation("Foreground report accepted again; the daemon can see the app in front.");
            _failing = false;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            if (!_failing)
                logger?.LogWarning(e,
                    "Foreground report to {Route} failed; until it succeeds the daemon cannot see the app in front (FPS target, per-app rules).",
                    Route);
            _failing = true;
            return false;
        }
    }
}
