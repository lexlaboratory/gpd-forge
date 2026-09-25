// GPD Forge — ForgeWorker's TDP duties, end to end over a fake SMU. GPL-3.0-or-later.
//
// The pieces (TdpIntent, TdpReasserter, AutoFpsStep) are tested on their own; these tests pin that
// the worker actually USES them, with the production controller stack underneath (audit decorator →
// closed loop → backend). Each test drives the real loop one published sample at a time.
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class ForgeWorkerTdpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpdforge-tests", Guid.NewGuid().ToString("n"));

    private readonly FakeSilicon _silicon = new();
    private readonly HardwareAuditLog _audit = new();
    private readonly TdpState _state = new();
    private readonly SwitchableDetector _detector = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly ScriptedTelemetrySource _source = new();
    private readonly TelemetryHistory _history = new();
    private readonly ModeState _mode = new();
    private readonly AutoFpsState _autoFps = new();
    private readonly GuardianService _guardian = new();
    private readonly TdpIntent _intent = new();
    private readonly PowerSourceState _powerSource = new();
    private readonly ForgeWorker _worker;

    public ForgeWorkerTdpTests()
    {
        Directory.CreateDirectory(_dir);
        var tdp = new SerializedTdpController(
            new AuditingTdpController(new ClosedLoopTdpController(_silicon, new NoWait()), _audit, _state, "test"), _state);
        _worker = new ForgeWorker(
            NullLogger<ForgeWorker>.Instance, tdp, new StubFanController(), _source, _mode, _autoFps,
            new FpsTdpController(), new FreezerService(new NullSuspender()), _guardian, _history,
            new ProfileApplier(tdp, _detector, intent: _intent, state: _state), _powerSource, new TunerState(),
            new AlertService(new AlertStore(_dir)), new ChargeGuardService(new MemoryChargeGuardStore()),
            new SessionRecorder(new SessionStore(_dir)),
            _intent, _state, new TdpReasserter(_silicon, tdp, _state, _detector, _intent, _mode, _clock));
    }

    public void Dispose()
    {
        try { _worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        _worker.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static TelemetrySnapshot Cool(double? fps = null) =>
        TelemetrySnapshot.Unmeasured with { CpuTempC = 55, BatteryPct = 60, Fps = fps };

    private IEnumerable<string> Writes(string owner) =>
        _audit.Recent(500).Where(w => w.Subsystem == "tdp" && w.Detail.StartsWith($"[{owner}]")).Select(w => w.Detail);

    /// <summary>Publishes one sample and waits until the worker has finished the tick it causes.</summary>
    private async Task TickAsync(TelemetrySnapshot snapshot)
    {
        _source.Publish(snapshot);
        long published = _source.Latest.Sequence;
        // The worker asks to wait past sample N only once it has finished everything tick N does,
        // TDP writes included — an exact completion signal, where a history count would fire at the
        // START of the tick.
        await Until(() => _source.LastWaitAfter >= published);
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

    // ---------------------------------------------------------------------------------------------
    // (a) The active mode is applied when the daemon starts
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task At_start_the_active_modes_profile_is_applied_once()
    {
        // Before 2026-09-24 a restarted daemon applied nothing until the mode CHANGED, so the
        // machine ran on whatever the last writer — or the firmware default — had left.
        _mode.Active = "windows";
        await _worker.StartAsync(CancellationToken.None);

        await Until(() => _state.Last is not null);

        var last = _state.Last!.Value;
        Assert.Equal(TdpOwner.Mode, last.Owner);
        Assert.Equal(ModeProfiles.For("windows"), last.Requested);
        Assert.Single(Writes(TdpOwner.Mode));
    }

    [Fact]
    public async Task At_start_it_yields_to_a_rival_controller()
    {
        _detector.Rival = true;
        await _worker.StartAsync(CancellationToken.None);

        await TickAsync(Cool());

        Assert.Null(_state.Last);
        Assert.Equal(0, _silicon.Writes);
    }

    // ---------------------------------------------------------------------------------------------
    // (b) The 30 s readback reassert runs from the worker's tick
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_limit_the_firmware_moved_is_put_back_on_the_next_due_tick_and_not_before()
    {
        await _worker.StartAsync(CancellationToken.None);
        await Until(() => _state.Last is not null);
        _silicon.Limits = new TdpReadout(25, 30);

        _clock.Advance(TimeSpan.FromSeconds(29));
        await TickAsync(Cool());
        Assert.Empty(Writes(TdpOwner.Reassert));

        _clock.Advance(TimeSpan.FromSeconds(1));
        await TickAsync(Cool());
        await Until(() => Writes(TdpOwner.Reassert).Any());
        Assert.Equal(ModeProfiles.For("windows"), _state.Last!.Value.Requested);
    }

    [Fact]
    public async Task A_limit_that_holds_is_only_read_on_the_due_tick()
    {
        await _worker.StartAsync(CancellationToken.None);
        await Until(() => _state.Last is not null);
        int writes = _silicon.Writes, reads = _silicon.Reads;

        _clock.Advance(TimeSpan.FromSeconds(30));
        await TickAsync(Cool());
        await Until(() => _silicon.Reads > reads);

        Assert.Equal(writes, _silicon.Writes);
    }

    // ---------------------------------------------------------------------------------------------
    // (c) The manual override survives the guardian
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Clearing_a_throttle_restores_the_manual_override_not_the_preset()
    {
        // The complaint this fixes: set 20 W by hand, get throttled by a hot spell, come out of it at
        // the `windows` preset (15/20/17) with nothing saying why.
        await _worker.StartAsync(CancellationToken.None);
        await Until(() => _state.Last is not null);
        var manual = TdpIntent.ManualProfile(20, "windows");
        _intent.SetManual("windows", manual);

        await TickAsync(Cool() with { CpuTempC = 97 });   // at/above critical: the raw reading acts at once
        Assert.Equal(TdpOwner.ThermalGuardian, _state.Last!.Value.Owner);

        _guardian.Configure(_guardian.Config with { Enabled = false });   // clears the throttle next tick
        await TickAsync(Cool());

        var last = _state.Last!.Value;
        Assert.Equal(TdpOwner.Restore, last.Owner);
        Assert.Equal(manual, last.Requested);
    }

    [Fact]
    public async Task A_throttle_never_raises_a_manual_override_that_is_already_lower()
    {
        // The throttle is a ceiling under what is IN FORCE. Built on the preset, a 10 W override met
        // a 12 W throttle floor and went UP to 12 W while the machine was at its critical limit.
        await _worker.StartAsync(CancellationToken.None);
        await Until(() => _state.Last is not null);
        _intent.SetManual("windows", TdpIntent.ManualProfile(10, "windows"));

        await TickAsync(Cool() with { CpuTempC = 97 });

        var last = _state.Last!.Value;
        Assert.Equal(TdpOwner.ThermalGuardian, last.Owner);
        Assert.True(last.Requested.StapmW <= 10, $"throttle raised STAPM to {last.Requested.StapmW} W");
        Assert.True(last.Requested.FastW <= 10);
    }

    // ---------------------------------------------------------------------------------------------
    // (d) Auto-FPS does not write inside its deadband
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task On_target_the_governor_writes_nothing_tick_after_tick()
    {
        _mode.Active = "gaming";
        _autoFps.Enabled = true;
        _autoFps.TargetFps = 60;
        await _worker.StartAsync(CancellationToken.None);
        await Until(() => _state.Last is not null);

        for (int i = 0; i < 5; i++) await TickAsync(Cool(fps: 60));

        Assert.Empty(Writes(TdpOwner.AutoFps));
        Assert.Equal(ModeProfiles.For("gaming")!.Value.StapmW, _autoFps.CurrentStapm);
    }

    [Fact]
    public async Task Off_target_it_writes_each_step_and_holds_once_on_target()
    {
        _mode.Active = "gaming";
        _autoFps.Enabled = true;
        _autoFps.TargetFps = 60;
        await _worker.StartAsync(CancellationToken.None);
        await Until(() => _state.Last is not null);

        await TickAsync(Cool(fps: 40));
        await Until(() => Writes(TdpOwner.AutoFps).Count() == 1);
        int raised = _state.Last!.Value.Requested.StapmW;
        Assert.True(raised > 25);
        Assert.Equal(raised, _autoFps.CurrentStapm);

        for (int i = 0; i < 3; i++) await TickAsync(Cool(fps: 60));

        Assert.Single(Writes(TdpOwner.AutoFps));
        Assert.Equal(raised, _state.Last!.Value.Requested.StapmW);
    }

    // ---------------------------------------------------------------------------------------------
    // (e) A failed battery query is not an unplug (audit, 2026-09-24)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_battery_query_does_not_switch_the_mode_but_a_real_unplug_still_does()
    {
        // The query's "on battery" fallback is cached for 5 s, and this loop read it as an AC edge:
        // mode to battery, then back to windows, on one WMI glitch — ending any manual override too.
        _powerSource.Config = new PowerSourceConfig(Enabled: true);
        await _worker.StartAsync(CancellationToken.None);
        await Until(() => _state.Last is not null);

        await TickAsync(Cool() with { AcConnected = true });
        await TickAsync(Cool() with { AcConnected = false, AcUnknown = true });
        Assert.Equal("windows", _mode.Active);
        await TickAsync(Cool() with { AcConnected = true });
        Assert.Equal("windows", _mode.Active);
        Assert.Single(Writes(TdpOwner.Mode));   // the startup apply, and nothing since

        // The control: the same config does act on an observed unplug.
        await TickAsync(Cool() with { AcConnected = false });
        Assert.Equal("battery", _mode.Active);
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
