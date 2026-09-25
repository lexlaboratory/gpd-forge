// GPD Forge — a sampler that stops publishing is visible, not silent. GPL-3.0-or-later.
//
// Audit 2026-09-24. WmiTelemetryService swallows every WMI/LHM error, so the realistic sampler failure
// is a read that HANGS (an LHM Update() or a driver stuck under the read lock), not one that throws —
// and TelemetrySampler only logs throws. ForgeWorker then spun on "no newer sample" every 2 s with
// nothing in the log, while the thermal guardian, the charge guard, the AC switch, the TDP reassert,
// history and sessions all stood still.
using GpdForge.Alerts;
using GpdForge.Api;
using GpdForge.Battery;
using GpdForge.Broker;
using GpdForge.Fan;
using GpdForge.Guardian;
using GpdForge.History;
using GpdForge.Profiles;
using GpdForge.Sessions;
using GpdForge.SystemControl;
using GpdForge.Tdp;
using GpdForge.Telemetry;
using GpdForge.Tuner;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GpdForge.Core.Tests;

public class SampleStallMonitorTests
{
    [Fact]
    public void A_few_missed_waits_are_one_warning_not_one_per_miss()
    {
        var m = new SampleStallMonitor(warnAfterMisses: 3);

        Assert.Equal(StallTransition.None, m.Missed());
        Assert.Equal(StallTransition.None, m.Missed());
        Assert.Equal(StallTransition.Stalled, m.Missed());
        for (int i = 0; i < 100; i++) Assert.Equal(StallTransition.None, m.Missed());
        Assert.Equal(103, m.Misses);
    }

    [Fact]
    public void Samples_resuming_after_a_warning_is_reported_once_and_rearms()
    {
        var m = new SampleStallMonitor(warnAfterMisses: 2);
        m.Missed(); m.Missed();

        Assert.Equal(StallTransition.Recovered, m.Advanced());
        Assert.Equal(StallTransition.None, m.Advanced());
        Assert.Equal(0, m.Misses);

        m.Missed();
        Assert.Equal(StallTransition.Stalled, m.Missed());   // a second outage warns again
    }

    [Fact]
    public void A_single_slow_sample_that_never_reached_the_warning_is_not_a_recovery()
    {
        var m = new SampleStallMonitor(warnAfterMisses: 3);
        m.Missed();
        Assert.Equal(StallTransition.None, m.Advanced());
    }
}

public sealed class ForgeWorkerStallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpdforge-tests", Guid.NewGuid().ToString("n"));
    private readonly StallingSource _source = new();
    private readonly CapturingLogger<ForgeWorker> _log = new();
    private readonly ForgeWorker _worker;

    public ForgeWorkerStallTests()
    {
        Directory.CreateDirectory(_dir);
        var state = new TdpState();
        var detector = new SwitchableDetector();
        var silicon = new FakeSilicon();
        var intent = new TdpIntent();
        var mode = new ModeState();
        var tdp = new SerializedTdpController(
            new AuditingTdpController(new ClosedLoopTdpController(silicon, new NoWait()), new HardwareAuditLog(), state, "test"), state);
        _worker = new ForgeWorker(
            _log, tdp, new StubFanController(), _source, mode, new AutoFpsState(),
            new FpsTdpController(), new FreezerService(new NullSuspender()), new GuardianService(), new TelemetryHistory(),
            new ProfileApplier(tdp, detector, intent: intent, state: state), new PowerSourceState(), new TunerState(),
            new AlertService(new AlertStore(_dir)), new ChargeGuardService(new MemoryChargeGuardStore()),
            new SessionRecorder(new SessionStore(_dir)),
            intent, state, new TdpReasserter(silicon, tdp, state, detector, intent, mode, new ManualTimeProvider()));
    }

    public void Dispose()
    {
        try { _worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        _worker.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task A_sampler_that_stops_publishing_is_a_warning_and_its_return_an_info_line()
    {
        await _worker.StartAsync(CancellationToken.None);

        await Until(() => _log.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("telemetry", StringComparison.OrdinalIgnoreCase)));
        await Task.Delay(100);   // many more misses: still ONE warning for the outage
        Assert.Equal(1, _log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("telemetry", StringComparison.OrdinalIgnoreCase)));

        _source.Resume(TelemetrySnapshot.Unmeasured with { CpuTempC = 55, BatteryPct = 60 });

        await Until(() => _log.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("resumed", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached in 5 s");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// A sampler whose read hangs: every wait "times out" at once (a real one takes 2 s), until
    /// <see cref="Resume"/> publishes a sample; after that, waits block as a healthy idle source would.
    /// </summary>
    private sealed class StallingSource : ITelemetrySource
    {
        private TelemetryReading _latest = TelemetryReading.Unsampled;
        private volatile bool _stalled = true;

        public TelemetryReading Latest => Volatile.Read(ref _latest);

        public void Resume(TelemetrySnapshot snapshot)
        {
            Volatile.Write(ref _latest, new TelemetryReading(snapshot, DateTimeOffset.UtcNow, Latest.Sequence + 1));
            _stalled = false;
        }

        public async Task<TelemetryReading> WaitForNewerAsync(long afterSequence, TimeSpan timeout, CancellationToken ct)
        {
            if (Latest.Sequence > afterSequence) return Latest;
            // 20 ms, not the real 2 s: fast enough to reach three misses quickly, slow enough not to
            // spin a core beside the other tests running in parallel.
            if (_stalled) { await Task.Delay(20, ct); return Latest; }
            await Task.Delay(Timeout.Infinite, ct);
            return Latest;
        }
    }

    private sealed class NullSuspender : IProcessSuspender
    {
        public void Suspend(int pid) { }
        public void Resume(int pid) { }
    }

    private sealed class MemoryChargeGuardStore : IChargeGuardStore
    {
        private ChargeGuardState _state = new();
        public ChargeGuardState Read() => _state;
        public void Write(ChargeGuardState state) => _state = state;
    }
}
