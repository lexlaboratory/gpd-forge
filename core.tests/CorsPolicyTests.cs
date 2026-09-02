// GPD Forge — who is allowed to talk to the daemon from a browser. GPL-3.0-or-later.
//
// Until 2026-09-02 the policy was AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod(), and the hole
// was demonstrated against the RUNNING service before it was closed: a preflight carrying
// `Origin: https://evil.example.com` for `POST /tdp` came back 204 with
// `Access-Control-Allow-Origin: *`, and `GET /audit` answered 200 with the same header. Any page the
// user was visiting could set power limits, fire a panic cool, and read this machine's hardware
// audit log — against a daemon running as SYSTEM.
//
// These tests run against the real daemon process (DaemonUnderTest), not a WebApplicationFactory,
// because CORS is middleware and the thing worth pinning is what the actual binary answers.
using System.Net.Http;
using Xunit;

namespace GpdForge.Core.Tests;

[Collection(DaemonCollection.Name)]
public class CorsPolicyTests(DaemonUnderTest daemon)
{
    private async Task<HttpResponseMessage> PreflightAsync(string origin, string route, string method)
    {
        var req = new HttpRequestMessage(HttpMethod.Options, route);
        req.Headers.Add("Origin", origin);
        req.Headers.Add("Access-Control-Request-Method", method);
        return await daemon.Client.SendAsync(req);
    }

    private static string? AllowOrigin(HttpResponseMessage res) =>
        res.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? string.Join(",", v) : null;

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://evil.example.com")]
    [InlineData("https://www.google.com")]
    [InlineData("null")]                      // sandboxed iframe / file:// document
    public async Task A_page_from_anywhere_else_is_not_allowed_to_write_power_limits(string origin)
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        var res = await PreflightAsync(origin, "/tdp", "POST");

        // The assertion is on the HEADER, not the status. A preflight may legitimately answer 204
        // while omitting the header — the browser then refuses. What must never appear is this
        // origin, or `*`, in Access-Control-Allow-Origin.
        var allowed = AllowOrigin(res);
        Assert.True(allowed is null,
            $"Origin {origin} was granted access to POST /tdp (Access-Control-Allow-Origin: {allowed}). " +
            "Any page the user visits could then set power limits on a SYSTEM daemon.");
    }

    [Fact]
    public async Task A_page_from_anywhere_else_cannot_read_the_hardware_audit_log()
    {
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        // /audit is the read that matters most: it is a log of every hardware write this machine has
        // performed. Reading is as much a leak as writing is a hazard.
        var req = new HttpRequestMessage(HttpMethod.Get, "/audit");
        req.Headers.Add("Origin", "https://evil.example.com");
        var res = await daemon.Client.SendAsync(req);

        var allowed = AllowOrigin(res);
        Assert.True(allowed is null,
            $"A foreign origin was handed Access-Control-Allow-Origin: {allowed} on /audit, so a page " +
            "could read this machine's hardware audit log.");
    }

    [Fact]
    public async Task Nothing_is_ever_answered_with_a_wildcard()
    {
        // The specific regression to prevent: someone "fixing a CORS problem" by reaching for
        // AllowAnyOrigin again. A wildcard is what was there, and it is never the right answer for a
        // daemon that controls hardware.
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        foreach (var origin in new[] { "https://evil.example.com", "http://tauri.localhost" })
        {
            var res = await PreflightAsync(origin, "/mode", "POST");
            Assert.NotEqual("*", AllowOrigin(res));
        }
    }

    [Theory]
    [InlineData("http://tauri.localhost")]
    [InlineData("http://localhost:5188")]
    [InlineData("http://127.0.0.1:4173")]
    public async Task The_first_party_shells_are_still_allowed(string origin)
    {
        // The other half, and the reason this is a test rather than a one-line change: an allowlist
        // that locks out the app itself is a worse bug than the one being fixed, and it would show up
        // as "the desktop shell stopped working" with no obvious cause.
        Assert.True(daemon.Started, "The daemon did not start; see The_daemon_starts_at_all.");

        var res = await PreflightAsync(origin, "/mode", "POST");

        Assert.Equal(origin, AllowOrigin(res));
    }
}
