// GPD Forge — update checker (GitHub releases). GPL-3.0-or-later.
//
// The daemon has internet access, so it can check GitHub for a newer release — but that request must
// never be allowed to affect anything else it does. ILatestReleaseSource is real, unprivileged,
// read-only HTTP with a short timeout and an explicit User-Agent (GitHub rejects requests without
// one); it degrades to (null, null) on ANY failure — offline, timeout, rate limit, malformed JSON —
// and never throws, matching this repo's honest-by-construction convention for anything that reaches
// outside the box (see VramAdvisor, MotionAssistantImporter).
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GpdForge.Update;

/// <summary>Result of GET /update/check.</summary>
public readonly record struct UpdateCheckResult(string Current, string? Latest, bool UpdateAvailable, string? Url);

/// <summary>Abstraction over "fetch the latest release's tag + web URL", so <see cref="UpdateService"/>
/// is unit-testable with a fake — zero network in tests.</summary>
public interface ILatestReleaseSource
{
    Task<(string? Tag, string? Url)> FetchLatestAsync(CancellationToken ct);
}

/// <summary>Real source: GitHub's REST API, read-only. Never throws.</summary>
public sealed class GitHubReleaseSource : ILatestReleaseSource, IDisposable
{
    public const string ReleaseApiUrl = "https://api.github.com/repos/lexlaboratory/gpd-forge/releases/latest";

    private readonly HttpClient _http;
    private readonly ILogger<GitHubReleaseSource>? _logger;

    public GitHubReleaseSource(ILogger<GitHubReleaseSource>? logger = null)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("gpd-forge-daemon");
        _logger = logger;
    }

    public async Task<(string? Tag, string? Url)> FetchLatestAsync(CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage res = await _http.GetAsync(ReleaseApiUrl, ct);
            if (!res.IsSuccessStatusCode) return (null, null);

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            string? tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            string? url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            return (tag, url);
        }
        catch (Exception ex)
        {
            // Offline / timeout / rate-limited / malformed response — degrade honestly, never throw.
            _logger?.LogDebug(ex, "update check failed (degrading to no update available)");
            return (null, null);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Combines a release source with <see cref="VersionCompare"/> to answer GET /update/check.
/// Reports whatever the source found even when it isn't newer (so the UI can still show "latest:
/// vX.Y.Z"); <see cref="UpdateCheckResult.UpdateAvailable"/> is the only field a caller needs to gate
/// on. Never throws — a failed lookup surfaces as latest:null / updateAvailable:false.</summary>
public sealed class UpdateService(ILatestReleaseSource source, string currentVersion = "0.1.0")
{
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        (string? tag, string? url) = await source.FetchLatestAsync(ct);
        bool isNewer = VersionCompare.IsNewer(tag, currentVersion);
        return new UpdateCheckResult(currentVersion, tag, isNewer, url);
    }
}
