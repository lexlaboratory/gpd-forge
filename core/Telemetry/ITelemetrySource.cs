// GPD Forge — the cached telemetry every consumer reads. GPL-3.0-or-later.
namespace GpdForge.Telemetry;

/// <summary>
/// One published sample: the snapshot, when it was measured, and a sequence number that rises by one
/// per successful read. Sequence 0 is the "nothing sampled yet" reading — an all-null snapshot with
/// no timestamp, never a guess.
/// </summary>
public sealed record TelemetryReading(TelemetrySnapshot Snapshot, DateTimeOffset? SampledAt, long Sequence)
{
    public static readonly TelemetryReading Unsampled = new(TelemetrySnapshot.Unmeasured, null, 0);

    public bool IsSampled => Sequence > 0;

    /// <summary>How old the reading is at <paramref name="now"/>, in ms; null when never sampled.
    /// Clamped at zero so a wall-clock step backwards cannot produce a negative age.</summary>
    public long? AgeMs(DateTimeOffset now) =>
        SampledAt is { } at ? Math.Max(0, (long)(now - at).TotalMilliseconds) : null;
}

/// <summary>
/// Read side of <see cref="TelemetrySampler"/>. Everything that wants telemetry takes THIS, not
/// <see cref="ITelemetryService"/>: reading it costs a field load, where a hardware read costs
/// ~100–140 ms of WMI + LibreHardwareMonitor + EC (measured 2026-09-24).
/// </summary>
public interface ITelemetrySource
{
    /// <summary>The newest published reading, or <see cref="TelemetryReading.Unsampled"/>. Never blocks.</summary>
    TelemetryReading Latest { get; }

    /// <summary>
    /// Completes with the first reading whose sequence is greater than <paramref name="afterSequence"/>.
    /// On <paramref name="timeout"/> it returns <see cref="Latest"/> instead of throwing — a stalled
    /// sampler shows up to the caller as a reading that did not advance, which it can check.
    /// </summary>
    Task<TelemetryReading> WaitForNewerAsync(long afterSequence, TimeSpan timeout, CancellationToken ct);
}

public static class TelemetrySourceExtensions
{
    /// <summary>
    /// How long a caller may wait for the very first sample. The loop publishes one immediately at
    /// startup, so this only bites when the first hardware read itself hangs; then the caller gets
    /// the unmeasured reading rather than a request that never answers.
    /// </summary>
    public static readonly TimeSpan FirstSampleTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The latest reading, waiting (bounded) only if nothing has been sampled yet.</summary>
    public static Task<TelemetryReading> ReadAsync(this ITelemetrySource source, CancellationToken ct)
    {
        var latest = source.Latest;
        return latest.IsSampled
            ? Task.FromResult(latest)
            : source.WaitForNewerAsync(0, FirstSampleTimeout, ct);
    }
}

/// <summary>
/// A consumer that must see EVERY published reading, not merely the newest: handed each one by
/// <see cref="TelemetrySampler"/> on its own thread, right after publishing. Must be quick and must
/// not block — it runs inside the 1 Hz sampling loop. Audit round 3 (2026-09-24): /history was fed
/// from ForgeWorker's tick, which skips any sample superseded while a ryzenadj write is in flight.
/// </summary>
public interface ITelemetrySink
{
    void Accept(TelemetryReading reading);
}
