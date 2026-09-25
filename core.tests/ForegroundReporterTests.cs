// GPD Forge — the session agent reports the foreground app whatever the GPU-profiles gate says.
// GPL-3.0-or-later.
//
// Audit round 2 (2026-09-24): the POST /session/foreground call lived inside the GPU agent's loop,
// after an early `return` when GPDFORGE_ENABLE_GPU_PROFILES was not "1" — and the installer only
// created the agent's autostart with -EnableGpuProfiles, which is off by default. So on a default
// install nothing ever reported the foreground, the installed service (session 0) saw null forever,
// and the FPS target and the per-app rules were blind with nothing saying so. The POST's own status
// was also discarded, so a 400 or a 404 from an older daemon was invisible.
using System.Net;
using GpdForge.Gpu;
using GpdForge.Profiles;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GpdForge.Core.Tests;

public class ForegroundReporterTests
{
    private sealed class FixedForeground(string? name) : IForegroundApp
    {
        public string? Current() => name;
    }

    /// <summary>Records every request and answers with a status the test chooses.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, string Path, string Body)> _seen = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool Throw { get; set; }
        public Action? OnRequest { get; set; }

        public IReadOnlyList<(HttpMethod Method, string Path, string Body)> Seen
        {
            get { lock (_seen) return _seen.ToArray(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (_seen) _seen.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            OnRequest?.Invoke();
            if (Throw) throw new HttpRequestException("connection refused");
            return new HttpResponseMessage(Status) { Content = new StringContent("{}") };
        }
    }

    private static HttpClient Client(RecordingHandler h) => new(h) { BaseAddress = new Uri("http://127.0.0.1:8787") };

    [Fact]
    public async Task A_report_posts_the_foreground_process()
    {
        var handler = new RecordingHandler();
        var reporter = new ForegroundReporter(Client(handler), new FixedForeground("eldenring"));

        Assert.True(await reporter.ReportAsync(CancellationToken.None));

        var (method, path, body) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/session/foreground", path);
        Assert.Contains("\"eldenring\"", body);
    }

    [Fact]
    public async Task A_refused_report_is_a_failure_and_warns_once_per_outage()
    {
        // A 400 (a name the daemon rejects) or a 404 (a daemon older than the endpoint) used to be
        // swallowed; the agent looked healthy while the daemon heard nothing.
        var handler = new RecordingHandler { Status = HttpStatusCode.NotFound };
        var log = new CapturingLogger<ForegroundReporter>();
        var reporter = new ForegroundReporter(Client(handler), new FixedForeground("game"), log);

        Assert.False(await reporter.ReportAsync(CancellationToken.None));
        Assert.False(await reporter.ReportAsync(CancellationToken.None));
        Assert.False(await reporter.ReportAsync(CancellationToken.None));
        Assert.Equal(1, log.Count(LogLevel.Warning));

        handler.Status = HttpStatusCode.OK;
        Assert.True(await reporter.ReportAsync(CancellationToken.None));
        Assert.Equal(1, log.Count(LogLevel.Information));   // "reporting again"

        handler.Throw = true;   // a second outage warns again
        Assert.False(await reporter.ReportAsync(CancellationToken.None));
        Assert.Equal(2, log.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task With_GPU_profiles_off_the_agent_still_reports_the_foreground_and_touches_no_GPU_route()
    {
        var handler = new RecordingHandler();
        using var cts = new CancellationTokenSource();
        handler.OnRequest = () => { if (handler.Seen.Count >= 1) cts.Cancel(); };

        int code = await GpuAgentLoop.RunAsync(Client(handler), new FixedForeground("game"), gpuProfiles: false, logger: null, cts.Token);

        Assert.Equal(0, code);
        Assert.Contains(handler.Seen, r => r.Path == "/session/foreground");
        Assert.DoesNotContain(handler.Seen, r => r.Path.StartsWith("/gpu") || r.Path == "/mode");
    }
}
