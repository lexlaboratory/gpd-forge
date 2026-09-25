// GPD Forge — session recording glue. GPL-3.0-or-later.
//
// The one stateful piece the worker touches: it turns a telemetry tick into a SessionTick, runs it
// through the (pure) tracker, and files whatever session that closes. Everything decidable lives in
// SessionTracker/SessionMath; this only wires them to the probe, the store and the log.
//
// Reading the frame probe a second time in the same tick is deliberate and free: the probe
// aggregates a trailing window that is not consumed by reading, and the target (FrameTarget) is
// chosen the same way both times, so the recorder sees the sample the telemetry snapshot was built
// from — including the presenting process name, which the snapshot itself does not carry. Since
// audit round 3 (2026-09-24) both reads happen on the sampler's thread (core/History/SampleRecorder.cs).
using GpdForge.Telemetry;
using Microsoft.Extensions.Logging;

namespace GpdForge.Sessions;

public sealed class SessionRecorder(
    SessionStore store,
    IFrameRateProbe? probe = null,
    SessionPolicy? policy = null,
    ILogger<SessionRecorder>? logger = null)
{
    private readonly SessionTracker _tracker = new(policy);
    // Observe runs on the sampler's thread, Flush on ForgeWorker's at shutdown; the tracker is not
    // thread-safe, and the two can meet while the host stops.
    private readonly Lock _gate = new();

    /// <summary>
    /// False when no frame-rate probe is registered at all — the GPDFORGE_ENABLE_FPS gate is closed,
    /// PresentMon is not installed, or Smart App Control blocked it. Surfaced over the API so the UI
    /// can say why the history is empty instead of implying the user has not played anything.
    /// </summary>
    public bool FpsAvailable => probe is not null;

    public string? CurrentApp { get { lock (_gate) return _tracker.CurrentApp; } }

    /// <summary>Feeds one telemetry sample. Returns the session it closed, if any.</summary>
    public GameSession? Observe(in TelemetrySnapshot snapshot, DateTimeOffset now)
    {
        FpsSample? frames = null;
        if (probe is not null && probe.TryRead(out var sample)) frames = sample;
        GameSession? closed;
        lock (_gate) closed = _tracker.Observe(SessionTick.From(snapshot, frames, now));
        return Record(closed);
    }

    /// <summary>Files the in-flight session, if any — call on shutdown so quitting the service does
    /// not silently lose the evening.</summary>
    public GameSession? Flush(DateTimeOffset now)
    {
        GameSession? closed;
        lock (_gate) closed = _tracker.Flush(now);
        return Record(closed);
    }

    private GameSession? Record(GameSession? closed)
    {
        if (closed is null) return null;
        try
        {
            store.Add(closed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a history row must never take the daemon down: telemetry, TDP and the fan all
            // depend on this tick completing.
            logger?.LogWarning(ex, "Play session for {App} could not be saved", closed.App);
            return null;
        }
        logger?.LogInformation("Session recorded: {App}, {Minutes:F0} min", closed.App, closed.DurationSeconds / 60);
        return closed;
    }
}
