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

    /// <summary>Whether anything has ever been asked for. Until then the agent must leave the GPU
    /// alone — starting the daemon is not a reason to change someone's Adrenalin settings.</summary>
    public bool Requested { get { lock (_gate) return _requested; } }

    public int? FrameCapFps { get { lock (_gate) return _frameCapFps; } }

    public DateTimeOffset? RequestedAtUtc { get { lock (_gate) return _requestedAtUtc; } }

    /// <summary>Record an intent. <paramref name="fps"/> null disables the cap.</summary>
    public void RequestFrameCap(int? fps, DateTimeOffset now)
    {
        lock (_gate)
        {
            _frameCapFps = fps;
            _requested = true;
            _requestedAtUtc = now;
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
