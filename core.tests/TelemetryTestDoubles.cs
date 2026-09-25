// GPD Forge — shared fakes for the telemetry sampler and its readers. GPL-3.0-or-later.
using GpdForge.Telemetry;

namespace GpdForge.Core.Tests;

/// <summary>
/// A clock that only moves when a test says so. Both halves are overridden — the wall clock stamps
/// samples, the monotonic timestamp drives the 5 s WMI cadence — so a test can age a reading without
/// sleeping and without the two disagreeing.
/// </summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    private long _ticks;

    public ManualTimeProvider() : this(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero)) { }

    public override DateTimeOffset GetUtcNow() => _now;
    public override long GetTimestamp() => _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        _now += by;
        _ticks += by.Ticks;
    }

    /// <summary>A suspend as a counter that stops in sleep would see it: wall time moves, ticks do not.</summary>
    public void AdvanceWallOnly(TimeSpan by) => _now += by;
}

/// <summary>The hardware reader, counted. Every call here stands for ~100–140 ms of WMI + LHM + EC.</summary>
public sealed class CountingTelemetryReader : ITelemetryService
{
    private int _reads;
    public int Reads => Volatile.Read(ref _reads);
    public TelemetrySnapshot Next { get; set; } = TelemetrySnapshot.Unmeasured with { CpuTempC = 55, AcConnected = true };
    public Exception? Throw { get; set; }

    public Task<TelemetrySnapshot> ReadAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _reads);
        if (Throw is not null) throw Throw;
        return Task.FromResult(Next);
    }
}

/// <summary>A source that always holds one already-sampled reading — for consumers under test.</summary>
public sealed class FixedTelemetrySource(TelemetrySnapshot snapshot) : ITelemetrySource
{
    public TelemetrySnapshot Snapshot { get; set; } = snapshot;
    private long _sequence = 1;

    public TelemetryReading Latest => new(Snapshot, DateTimeOffset.UtcNow, _sequence);

    public Task<TelemetryReading> WaitForNewerAsync(long afterSequence, TimeSpan timeout, CancellationToken ct)
    {
        // Every wait yields a "new" sample, so a consumer loop under test advances one step per call.
        if (afterSequence >= _sequence) _sequence = afterSequence + 1;
        return Task.FromResult(Latest);
    }
}

/// <summary>Scripted WMI answers, each query counted separately so the cadence can be asserted per class.</summary>
public sealed class FakeWmiQueries : IWmiTelemetryQueries
{
    public int ThermalReads, ClockReads, BatteryReads, DischargeReads;

    public double? ThermalTenthsKelvin { get; set; } = 3231.5;   // 50 °C
    public int? ClockMhz { get; set; } = 2000;
    public WmiBatteryReading? Battery { get; set; } = new(80, Ac: false);
    public double? DischargeMw { get; set; } = 12_300;

    public double? ReadThermalZoneTenthsKelvin() { ThermalReads++; return ThermalTenthsKelvin; }
    public int? ReadProcessorClockMhz() { ClockReads++; return ClockMhz; }
    public WmiBatteryReading? ReadBattery() { BatteryReads++; return Battery; }
    public double? ReadDischargeRateMw() { DischargeReads++; return DischargeMw; }
}

public sealed class FakeHardwareSensors(HwSample? sample) : IHardwareSensors
{
    public HwSample? Sample { get; set; } = sample;
    public bool TryRead(out HwSample s) { s = Sample ?? default; return Sample.HasValue; }
    public void Dispose() { }
}
