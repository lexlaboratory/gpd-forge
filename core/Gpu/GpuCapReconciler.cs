// GPD Forge — what the agent reads from GET /gpu/desired, and when it carries a cap out. GPL-3.0-or-later.
//
// The agent used to write FRTC only when the requested VALUE changed from the last one it wrote. That
// was right while one request meant one user intent, but F1 re-requests caps as a matter of course (a
// game's Begin and End, End+Begin on a rule edit), and the dedupe swallowed real applies. Measured on
// the device 2026-09-25: GET /gpu/desired {requested:true, frameCapFps:60, requestedAtUtc:
// 2026-09-24T23:21Z} against GET /gpu FRTC enabled at 45 — the agent had applied 60 once and the user
// then set 45 in Adrenalin. A game whose profile says 60 asked for 60 again and nothing was written, so
// the game ran at 45 and the notice blamed the driver (F1 audit round 4).
//
// So the dedupe is keyed on the request's IDENTITY — its value, its CapVersion and its timestamp. A
// new request is carried out whatever it asks for; an old one is never re-asserted, so a cap the user
// sets in Adrenalin under it stays theirs (the reason the agent does not reconcile every tick).
using System.Text.Json;

namespace GpdForge.Gpu;

/// <summary>GET /gpu/desired as the agent reads it. <see cref="CapVersion"/> and
/// <see cref="RequestedAtUtc"/> are null from a daemon older than F1 audit round 4.</summary>
public sealed record DesiredGpuState(
    bool Requested, int? FrameCapFps, bool? AntiLag, bool? Chill,
    long? CapVersion = null, DateTimeOffset? RequestedAtUtc = null,
    GpuImageRequest? Image = null, long? ImageVersion = null)
{
    /// <summary>Null when the body is not the daemon's answer — which must NOT be read as "no cap
    /// wanted", or a momentary hiccup would undo the user's setting. The route also serves the SPA
    /// fallback, which answers 200 with HTML.</summary>
    public static DesiredGpuState? Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("requested", out var requested)
                || requested.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;

            int? cap = root.TryGetProperty("frameCapFps", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : null;
            long? version = root.TryGetProperty("capVersion", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
            DateTimeOffset? at = root.TryGetProperty("requestedAtUtc", out var a) && a.ValueKind == JsonValueKind.String
                && a.TryGetDateTimeOffset(out var parsed) ? parsed : null;

            // antiLag / chill are absent on a daemon older than F1, which reads as "no game opinion".
            // image / imageVersion are absent on a daemon older than F4: no RSR / RIS request.
            var image = root.TryGetProperty("image", out var img) ? GpuImageRequest.Parse(img) : null;
            long? imageVersion = root.TryGetProperty("imageVersion", out var iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetInt64() : null;
            return new DesiredGpuState(requested.GetBoolean(), cap, Bool(root, "antiLag"), Bool(root, "chill"), version, at, image, imageVersion);
        }
        catch (JsonException) { return null; }
    }

    private static bool? Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}

/// <summary>Which cap requests the agent has already carried out. One per agent process: a restarted
/// agent applies the standing request once, as it always has.</summary>
public sealed class GpuCapReconciler
{
    private (int? Fps, long? Version, DateTimeOffset? At)? _handled;

    /// <summary>Whether <paramref name="desired"/> is a request the agent has not carried out yet.</summary>
    public bool ShouldApply(DesiredGpuState desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        return desired.Requested && _handled != Identity(desired);
    }

    /// <summary>Recorded whether or not the driver took it, so a cap it refuses is not re-sent every
    /// three seconds forever. GET /gpu still shows the truth, read from the driver.</summary>
    public void Handled(DesiredGpuState desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        _handled = Identity(desired);
    }

    // The value is part of the identity too: a daemon older than the version field sends neither
    // version nor timestamp, and then a changed value is the only sign of a new request.
    private static (int?, long?, DateTimeOffset?) Identity(DesiredGpuState d) => (d.FrameCapFps, d.CapVersion, d.RequestedAtUtc);
}
