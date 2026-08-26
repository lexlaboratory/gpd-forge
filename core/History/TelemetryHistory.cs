// GPD Forge — telemetry history (fixed-capacity ring buffer). GPL-3.0-or-later.
using GpdForge.Telemetry;

namespace GpdForge.History;

/// <summary>One telemetry snapshot stamped with the wall-clock time it was read (Unix epoch
/// milliseconds, UTC). The ring buffer below never reads a clock itself — the caller stamps this
/// before calling <see cref="TelemetryHistory.Add"/>.</summary>
public readonly record struct HistorySample(long UnixMs, TelemetrySnapshot Snap);

/// <summary>
/// Thread-safe fixed-capacity ring buffer of timestamped telemetry samples. Pure logic: it never calls
/// DateTime/DateTimeOffset itself, so it is trivially deterministic to unit-test — construct samples
/// with whatever <c>UnixMs</c> the test wants. In production <c>ForgeWorker</c> is the sole writer and
/// stamps each sample with <c>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()</c> before calling
/// <see cref="Add"/>. Default capacity 3600 = 1 hour of history at the worker's 1 Hz tick.
/// </summary>
public sealed class TelemetryHistory
{
    private readonly HistorySample[] _buffer;
    private readonly object _lock = new();
    private int _count;
    private int _head; // index the NEXT Add() writes to

    public TelemetryHistory(int capacity = 3600)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be positive");
        _buffer = new HistorySample[capacity];
    }

    /// <summary>Fixed capacity this history was constructed with.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>How many samples are currently held (0..Capacity).</summary>
    public int Count { get { lock (_lock) return _count; } }

    /// <summary>Appends a sample, overwriting the oldest one once at capacity.</summary>
    public void Add(HistorySample sample)
    {
        lock (_lock)
        {
            _buffer[_head] = sample;
            _head = Mod(_head + 1, _buffer.Length);
            if (_count < _buffer.Length) _count++;
        }
    }

    /// <summary>The most recent <paramref name="max"/> samples, oldest first. Returns fewer than
    /// <paramref name="max"/> if the history doesn't hold that many yet. A non-positive <paramref
    /// name="max"/> returns empty — this never throws, mirroring LINQ's <c>Take</c>.</summary>
    public IReadOnlyList<HistorySample> Recent(int max)
    {
        lock (_lock)
        {
            int n = Math.Clamp(max, 0, _count);
            return Window(n);
        }
    }

    /// <summary>All held samples with <see cref="HistorySample.UnixMs"/> &gt;= <paramref
    /// name="unixMs"/>, oldest first.</summary>
    public IReadOnlyList<HistorySample> Since(long unixMs)
    {
        lock (_lock)
        {
            var window = Window(_count);
            var result = new List<HistorySample>(window.Length);
            foreach (var s in window)
                if (s.UnixMs >= unixMs) result.Add(s);
            return result;
        }
    }

    /// <summary>The newest <paramref name="n"/> entries (0 &lt;= n &lt;= the current count), oldest
    /// first. Caller must hold <see cref="_lock"/>.</summary>
    private HistorySample[] Window(int n)
    {
        var result = new HistorySample[n];
        int start = Mod(_head - n, _buffer.Length);
        for (int i = 0; i < n; i++)
            result[i] = _buffer[Mod(start + i, _buffer.Length)];
        return result;
    }

    /// <summary>True modulo (always non-negative), unlike C#'s <c>%</c> for negative operands.</summary>
    private static int Mod(int a, int m) => ((a % m) + m) % m;
}
