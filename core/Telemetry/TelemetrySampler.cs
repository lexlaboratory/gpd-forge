// GPD Forge — the one loop that reads telemetry hardware. GPL-3.0-or-later.
//
// Why this exists (measured 2026-09-24): a full read — four WMI queries, an Update() of every LHM
// device, an EC read — costs ~100–140 ms, and six callers did it on their own timers: ForgeWorker,
// GET /telemetry twice a second (the UI and the overlay, 1 Hz each), FocusProfileWorker every 1.5 s
// for one boolean, POST /jobs, GET /health/check and StandbyService. That is ~4 reads a second, each
// of them waking the same drivers, on a handheld whose whole purpose is spending its watts on a game.
//
// Now there is one reader. It samples at 1 Hz and publishes an immutable reading; every consumer
// reads the last one for the cost of a field load.
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GpdForge.Telemetry;

public sealed class TelemetrySampler(
    ITelemetryService reader,
    ILogger<TelemetrySampler>? logger = null,
    TimeProvider? time = null) : BackgroundService, ITelemetrySource
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private TelemetryReading _latest = TelemetryReading.Unsampled;
    private bool _failing;   // touched only by SampleOnceAsync, which only the loop calls

    // Swapped on every publish. A waiter grabs the current one, re-checks _latest, then awaits it;
    // publish writes _latest BEFORE swapping, so a waiter either sees the new reading on its re-check
    // or holds the source that is about to be completed. No lost wake-ups, no lock.
    private TaskCompletionSource _published = NewSignal();

    public TelemetryReading Latest => Volatile.Read(ref _latest);

    /// <summary>
    /// One hardware read, published on success. A failed read keeps the previous reading — which then
    /// ages visibly through <see cref="TelemetryReading.SampledAt"/> — instead of replacing good data
    /// with nothing or taking the loop down. Cancellation is the one exception that propagates.
    /// </summary>
    public async Task SampleOnceAsync(CancellationToken ct)
    {
        TelemetrySnapshot snapshot;
        try
        {
            snapshot = await reader.ReadAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Warn once per outage, not once a second: a broken provider would otherwise write 3600
            // identical warnings an hour into the service log.
            if (!_failing) logger?.LogWarning(ex, "Telemetry read failed; keeping the last sample until reads recover.");
            else logger?.LogDebug(ex, "Telemetry read still failing.");
            _failing = true;
            return;
        }

        if (_failing) logger?.LogInformation("Telemetry reads recovered.");
        _failing = false;
        var previous = Latest;
        Volatile.Write(ref _latest, new TelemetryReading(snapshot, _time.GetUtcNow(), previous.Sequence + 1));
        Interlocked.Exchange(ref _published, NewSignal()).TrySetResult();
    }

    public async Task<TelemetryReading> WaitForNewerAsync(long afterSequence, TimeSpan timeout, CancellationToken ct)
    {
        // The timeout runs on the real clock, not _time: a test's manual clock never advances on its
        // own, and a wait that could only end when the test moved it would hang.
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            var signal = Volatile.Read(ref _published);
            var latest = Latest;
            if (latest.Sequence > afterSequence) return latest;

            var remaining = timeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return latest;
            try { await signal.Task.WaitAsync(remaining, ct); }
            catch (TimeoutException) { return Latest; }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The reads below are synchronous WMI/LHM calls under an async signature. Without this yield
        // the first one would run inside host startup, holding up every other service by ~140 ms.
        await Task.Yield();

        // PeriodicTimer, not Delay(1 s) after each read: a 140 ms read followed by a 1 s delay is a
        // 0.88 Hz sampler, which misses the ≥ 0.95 samples/s bar in the plan. A tick that overruns is
        // skipped rather than queued, so a slow read never causes a burst of catch-up reads.
        using var timer = new PeriodicTimer(Interval, _time);
        try
        {
            do { await SampleOnceAsync(stoppingToken); }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* shutting down */ }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
