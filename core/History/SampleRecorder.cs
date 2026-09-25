// GPD Forge — records every telemetry sample into /history and the play-session tracker. GPL-3.0-or-later.
using GpdForge.Sessions;
using GpdForge.Telemetry;

namespace GpdForge.History;

/// <summary>
/// The sampler's sink for the two consumers that need every sample (audit round 3, 2026-09-24).
/// Both used to be fed by ForgeWorker's tick, which takes only the newest sample and, in the same
/// iteration, awaits closed-loop ryzenadj writes (up to ~1.7 s a tick, measured): a sample published
/// and superseded while a write was in flight never reached /history, so the samples/s the plan
/// measures with get_history could drop under auto-FPS steering or a tuner sweep while the sampler
/// itself ran at 1 Hz. Fed from the sampler, the rate /history shows is the rate the hardware was read.
///
/// It also puts the session recorder's frame-probe read on the sampler's thread, the same one that
/// already reads the probe for the snapshot — a second thread reaching PresentMon's process
/// management is how two PresentMon instances could be started at once.
/// </summary>
public sealed class SampleRecorder(TelemetryHistory history, SessionRecorder sessions) : ITelemetrySink
{
    public void Accept(TelemetryReading reading)
    {
        // Unsampled readings are never published, but the guard costs nothing and keeps a fake
        // source's sequence-0 reading out of the history.
        if (reading.SampledAt is not DateTimeOffset at) return;
        // Stamped with when the hardware was READ.
        history.Add(new HistorySample(at.ToUnixTimeMilliseconds(), reading.Snapshot));
        sessions.Observe(reading.Snapshot, at);
    }
}
