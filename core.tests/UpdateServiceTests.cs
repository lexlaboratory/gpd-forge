// GPD Forge — update-check service tests (fake release source, zero network). GPL-3.0-or-later.
using GpdForge.Update;
using Xunit;

namespace GpdForge.Core.Tests;

public class UpdateServiceTests
{
    private sealed class FakeReleaseSource(string? tag, string? url) : ILatestReleaseSource
    {
        public int Calls { get; private set; }
        public Task<(string? Tag, string? Url)> FetchLatestAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult((tag, url));
        }
    }

    [Fact]
    public async Task Reports_no_update_when_the_latest_release_matches_current()
    {
        var svc = new UpdateService(new FakeReleaseSource("v0.1.0", "https://example.test/r/v0.1.0"), currentVersion: "0.1.0");

        var r = await svc.CheckAsync(CancellationToken.None);

        Assert.Equal("0.1.0", r.Current);
        Assert.Equal("v0.1.0", r.Latest);
        Assert.False(r.UpdateAvailable);
    }

    [Fact]
    public async Task Reports_an_update_when_the_latest_release_is_newer()
    {
        var svc = new UpdateService(new FakeReleaseSource("v0.2.0", "https://example.test/r/v0.2.0"), currentVersion: "0.1.0");

        var r = await svc.CheckAsync(CancellationToken.None);

        Assert.True(r.UpdateAvailable);
        Assert.Equal("v0.2.0", r.Latest);
        Assert.Equal("https://example.test/r/v0.2.0", r.Url);
    }

    [Fact]
    public async Task Does_not_flag_an_update_when_the_latest_release_is_older()
    {
        // e.g. a pre-release channel or a fetch that raced a rollback — never claim "available" for
        // something older than what's already running.
        var svc = new UpdateService(new FakeReleaseSource("v0.0.9", "https://example.test/r/v0.0.9"), currentVersion: "0.1.0");

        var r = await svc.CheckAsync(CancellationToken.None);

        Assert.False(r.UpdateAvailable);
    }

    [Fact]
    public async Task Degrades_to_no_update_when_the_source_has_nothing_never_throws()
    {
        // Mirrors what GitHubReleaseSource returns on any failure (offline/timeout/rate-limited).
        var svc = new UpdateService(new FakeReleaseSource(null, null), currentVersion: "0.1.0");

        var r = await svc.CheckAsync(CancellationToken.None);

        Assert.Equal("0.1.0", r.Current);
        Assert.Null(r.Latest);
        Assert.False(r.UpdateAvailable);
        Assert.Null(r.Url);
    }

    [Fact]
    public async Task Always_reports_the_current_version_it_was_constructed_with()
    {
        var svc = new UpdateService(new FakeReleaseSource("v9.9.9", "https://example.test/r/v9.9.9"), currentVersion: "3.4.5");
        var r = await svc.CheckAsync(CancellationToken.None);
        Assert.Equal("3.4.5", r.Current);
    }

    [Fact]
    public async Task Calls_the_source_exactly_once_per_check()
    {
        var source = new FakeReleaseSource("v0.1.0", null);
        var svc = new UpdateService(source, currentVersion: "0.1.0");

        await svc.CheckAsync(CancellationToken.None);

        Assert.Equal(1, source.Calls);
    }
}
