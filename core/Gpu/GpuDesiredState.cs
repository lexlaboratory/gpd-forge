// GPD Forge — what the daemon WANTS the GPU to do, for the agent to carry out. GPL-3.0-or-later.
//
// The daemon cannot call ADLX (session 0 has no display driver stack), so it cannot apply anything
// itself. It records an intent here; the agent reads it each tick and reconciles. Same shape as the
// mode: desired state plus a reconciler, rather than a command queue.
//
// Desired state rather than commands is the important choice. A queue would need delivery guarantees,
// ordering and de-duplication, and an agent that restarted mid-queue would land somewhere nobody
// asked for. With desired state, an agent that misses ten ticks, crashes, or starts an hour late
// converges on exactly the same result: whatever is currently wanted.
//
// The cap is a REQUEST until the agent reports back. `POST /gpu/frame-cap` therefore does not claim
// success — this project has spent enough time removing endpoints that answered "applied" for work
// that had not happened yet.
namespace GpdForge.Gpu;

/// <summary>
/// The frame-rate cap the daemon wants applied. Null means "no cap" — an explicit intent, distinct
/// from "nobody has expressed one", which is why <see cref="Requested"/> exists separately.
/// </summary>
public sealed class GpuDesiredState
{
    private readonly object _gate = new();
    private int? _frameCapFps;
    private bool _requested;
    private DateTimeOffset? _requestedAtUtc;
    private long _capVersion;
    private bool? _antiLag;
    private bool? _chill;
    private GpuImageRequest? _image;
    private long _imageVersion;

    /// <summary>Rises on every cap request. A game profile restores its predecessor's cap only if
    /// this is still the value its own request left — otherwise someone asked for a cap since (the
    /// user, a mode), and that request is newer than the one being undone.</summary>
    public long CapVersion { get { lock (_gate) return _capVersion; } }

    /// <summary>A game profile's Anti-Lag / Chill, layered over the mode's Radeon profile by the agent
    /// (GpuFeatureOverride). Null = the mode decides. Independent of <see cref="Requested"/>, which is
    /// about the cap only.</summary>
    public bool? AntiLag { get { lock (_gate) return _antiLag; } }

    public bool? Chill { get { lock (_gate) return _chill; } }

    public void RequestFeatures(bool? antiLag, bool? chill)
    {
        lock (_gate) { _antiLag = antiLag; _chill = chill; }
    }

    /// <summary>RSR / RIS to set (F4), from a game profile or the Display page. Null = nothing asked.
    /// Carried out once per <see cref="ImageVersion"/> (GpuImageReconciler), like the cap.</summary>
    public GpuImageRequest? Image { get { lock (_gate) return _image; } }

    /// <summary>Rises on every image request. A game profile restores what it changed only while this
    /// is still the value its own request left — a Display-page change since is the user's.</summary>
    public long ImageVersion { get { lock (_gate) return _imageVersion; } }

    /// <summary>Record an RSR / RIS intent. An empty or null request asks for nothing (the agent
    /// writes nothing) but still moves the version, which retires whatever was asked before.</summary>
    public void RequestImage(GpuImageRequest? image)
    {
        lock (_gate) { _image = image is { IsEmpty: false } ? image : null; _imageVersion++; }
    }

    /// <summary>Whether anything has ever been asked for. Until then the agent must leave the GPU
    /// alone — starting the daemon is not a reason to change someone's Adrenalin settings.</summary>
    public bool Requested { get { lock (_gate) return _requested; } }

    public int? FrameCapFps { get { lock (_gate) return _frameCapFps; } }

    public DateTimeOffset? RequestedAtUtc { get { lock (_gate) return _requestedAtUtc; } }

    /// <summary>
    /// How long after a cap request the agent's reading can be relied on to include it: two agent
    /// ticks (3 s each). The agent posts what the driver holds BEFORE it reconciles in the same tick,
    /// so a report up to one tick after a request can still be the cap the request is replacing.
    /// </summary>
    public static readonly TimeSpan CapSettle = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Whether a driver reading taken at <paramref name="reportAtUtc"/> supersedes a request made at
    /// <paramref name="requestedAtUtc"/>. The agent applies each request once (GpuCapReconciler) and
    /// never re-asserts it — so a cap the user sets in Adrenalin afterwards is what holds, and the
    /// request is history. Measured on the device 2026-09-25: a 60 FPS request from the day before
    /// against a driver at 45 (F1 audit round 3). Within the settle window the request is the truth.
    /// </summary>
    public static bool SupersededBy(DateTimeOffset? requestedAtUtc, DateTimeOffset reportAtUtc) =>
        requestedAtUtc is DateTimeOffset at && reportAtUtc - at >= CapSettle;

    /// <summary>Record an intent. <paramref name="fps"/> null disables the cap.</summary>
    public void RequestFrameCap(int? fps, DateTimeOffset now)
    {
        lock (_gate)
        {
            _frameCapFps = fps;
            _requested = true;
            _requestedAtUtc = now;
            _capVersion++;
        }
    }

    /// <summary>Back to "nobody has expressed one": the agent leaves the driver's cap alone again. For a
    /// game profile whose cap was never applied and whose predecessor was never read (F1 audit round 1,
    /// 2026-09-25) — "off" would have erased the user's own Adrenalin cap.</summary>
    public void WithdrawFrameCap()
    {
        lock (_gate)
        {
            _frameCapFps = null;
            _requested = false;
            _requestedAtUtc = null;
            _capVersion++;
        }
    }

    /// <summary>
    /// Whether a requested cap is within the range the driver reported. Checked before the request is
    /// accepted rather than after it fails, so a user who types 500 is told why instead of watching
    /// nothing happen. Null range = the driver did not report one, and then only obvious nonsense is
    /// rejected — a limit we did not read is not a limit we can enforce.
    /// </summary>
    public static string? Reject(int? fps, int? minFps, int? maxFps)
    {
        if (fps is not int v) return null;   // disabling is always legal

        if (v <= 0) return "A frame cap must be a positive number of frames per second.";

        if (minFps is int lo && v < lo)
            return $"The driver's lowest supported frame cap is {lo} FPS.";
        if (maxFps is int hi && v > hi)
            return $"The driver's highest supported frame cap is {hi} FPS.";

        // Without a reported range, reject only what cannot be a frame rate on this class of hardware.
        if (minFps is null && maxFps is null && v > 1000)
            return "That is not a plausible frame cap, and the driver did not report its supported range.";

        return null;
    }
}
