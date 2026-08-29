// GPD Forge - Standby Doctor tests. GPL-3.0-or-later.
using GpdForge.Fan;
using GpdForge.Standby;
using GpdForge.Tdp;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class PowerCfgParserTests
{
    private const string Requests = """
        DISPLAY:
        None.

        SYSTEM:
        [DRIVER] Realtek(R) Audio
        [PROCESS] \Device\HarddiskVolume4\game.exe

        AWAYMODE:
        None.
        """;

    private const string LastWake = """
        Wake History Count - 1
        Wake History [0]
          Wake Source Count - 1
          Wake Source [0]
            Type: Device
            Instance Path: USB\VID_27C6&PID_...
            Friendly Name: Goodix fingerprint reader
        """;

    [Fact]
    public void ParseRequests_lists_blockers_and_skips_none_and_headers()
    {
        var blockers = PowerCfgParser.ParseRequests(Requests);
        Assert.Equal(2, blockers.Count);
        Assert.Contains("[DRIVER] Realtek(R) Audio", blockers);
        Assert.DoesNotContain(blockers, b => b.Equals("None.", System.StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(blockers, b => b.EndsWith(':'));
    }

    [Fact]
    public void ParseLastWake_extracts_friendly_name()
    {
        Assert.Equal("Goodix fingerprint reader", PowerCfgParser.ParseLastWake(LastWake));
    }

    [Fact]
    public void ParseLastWake_returns_null_when_absent()
    {
        Assert.Null(PowerCfgParser.ParseLastWake("Wake History Count - 0"));
    }
}

public class StandbyRestoreOrderTests
{
    private sealed class RecordingFan(List<string> order) : IFanController
    {
        public Task InitializeAsync(CancellationToken ct) { order.Add("fan"); return Task.CompletedTask; }
        public Task SetDutyAsync(int percent, CancellationToken ct) => Task.CompletedTask;
        public int ReadRpm() => 0;
    }

    private sealed class RecordingTdp(List<string> order) : ITdpController
    {
        public Task<TdpApplyResult> ApplyAsync(TdpProfile profile, CancellationToken ct)
        {
            order.Add("tdp");
            return Task.FromResult(new TdpApplyResult(profile, new TdpReadout(profile.StapmW, profile.FastW), true, 1));
        }
    }

    private sealed class RecordingBackend : ITdpBackend
    {
        public Task ApplyAsync(TdpProfile profile, CancellationToken ct) => Task.CompletedTask;
        public Task<TdpReadout> ReadAsync(CancellationToken ct) => Task.FromResult(new TdpReadout(0, 0));
    }

    private sealed class NoRunner : IProcessRunner
    {
        public Task<string> RunAsync(string exePath, string arguments, CancellationToken ct) => Task.FromResult("");
    }

    [Fact]
    public async Task Restore_reinits_the_fan_before_reapplying_tdp()
    {
        var order = new List<string>();
        var svc = new StandbyService(
            new RecordingTdp(order), new RecordingBackend(), new RecordingFan(order),
            new StubTelemetryService(), logger: null, runner: new NoRunner());

        var outcome = await svc.RestoreAsync(new TdpProfile(20, 20, 20, 90), CancellationToken.None);

        Assert.Equal(new[] { "fan", "tdp" }, order);   // the EC comes back uninitialized; fan first
        Assert.True(outcome.AnyRestored);
    }
}

/// <summary>
/// The overnight-drain measurement. Every case here is about refusing to produce a number: the
/// tracker only ever reports a battery delta it actually observed across a real suspend.
/// </summary>
public class StandbyDrainTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 23, 0, 0, TimeSpan.Zero);

    private static StandbyDrainTracker Tracker() => new();

    [Fact]
    public void No_measurement_from_a_single_sample()
    {
        var t = Tracker();
        Assert.Null(t.Observe(T0, TimeSpan.Zero, 90, acConnected: false));
        Assert.Null(t.Last);
    }

    [Fact]
    public void Measures_drain_across_an_observed_suspend()
    {
        var t = Tracker();
        t.Observe(T0, TimeSpan.Zero, 90, acConnected: false);

        // 8 h of wall clock passed while unbiased (sleep-excluding) time did not advance: the whole
        // gap was spent suspended.
        var m = t.Observe(T0.AddHours(8), TimeSpan.Zero, 82, acConnected: false);

        Assert.NotNull(m);
        Assert.Equal(1.0, m!.PctPerHour);
        Assert.Equal(8, m.SleptHours);
        Assert.Equal(90, m.FromPct);
        Assert.Equal(82, m.ToPct);
        Assert.Same(m, t.Last);
    }

    [Fact]
    public void A_gap_spent_awake_is_not_a_standby_measurement()
    {
        var t = Tracker();
        t.Observe(T0, TimeSpan.Zero, 90, acConnected: false);
        Assert.Null(t.Observe(T0.AddHours(8), TimeSpan.FromHours(8), 60, acConnected: false));
        Assert.Null(t.Last);
    }

    [Fact]
    public void A_suspend_shorter_than_the_floor_is_ignored()
    {
        var t = Tracker();
        t.Observe(T0, TimeSpan.Zero, 90, acConnected: false);
        Assert.Null(t.Observe(T0.AddMinutes(5), TimeSpan.Zero, 89, acConnected: false));
    }

    [Fact]
    public void Charger_at_either_end_makes_the_delta_unattributable()
    {
        var onAcAfter = Tracker();
        onAcAfter.Observe(T0, TimeSpan.Zero, 90, acConnected: false);
        Assert.Null(onAcAfter.Observe(T0.AddHours(8), TimeSpan.Zero, 82, acConnected: true));

        var onAcBefore = Tracker();
        onAcBefore.Observe(T0, TimeSpan.Zero, 90, acConnected: true);
        Assert.Null(onAcBefore.Observe(T0.AddHours(8), TimeSpan.Zero, 82, acConnected: false));
    }

    [Fact]
    public void A_charge_gain_is_not_reported_as_negative_drain()
    {
        var t = Tracker();
        t.Observe(T0, TimeSpan.Zero, 80, acConnected: false);
        Assert.Null(t.Observe(T0.AddHours(8), TimeSpan.Zero, 95, acConnected: false));
    }

    [Fact]
    public void Zero_drop_is_a_real_zero_and_not_a_missing_value()
    {
        var t = Tracker();
        t.Observe(T0, TimeSpan.Zero, 90, acConnected: false);
        var m = t.Observe(T0.AddHours(8), TimeSpan.Zero, 90, acConnected: false);
        Assert.NotNull(m);
        Assert.Equal(0.0, m!.PctPerHour);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void An_unreadable_battery_is_dropped_without_disturbing_the_previous_sample(int bogus)
    {
        var t = Tracker();
        t.Observe(T0, TimeSpan.Zero, 90, acConnected: false);
        Assert.Null(t.Observe(T0.AddHours(4), TimeSpan.Zero, bogus, acConnected: false));

        // The bogus reading must not have become the new baseline: the real one still measures
        // against T0.
        var m = t.Observe(T0.AddHours(8), TimeSpan.Zero, 82, acConnected: false);
        Assert.NotNull(m);
        Assert.Equal(90, m!.FromPct);
    }

    [Fact]
    public void A_backwards_clock_produces_nothing()
    {
        var t = Tracker();
        t.Observe(T0, TimeSpan.FromHours(1), 90, acConnected: false);
        Assert.Null(t.Observe(T0.AddHours(-1), TimeSpan.FromHours(1), 82, acConnected: false));
    }
}

public class StandbyServiceTests
{
    private const string Requests = """
        DISPLAY:
        None.

        SYSTEM:
        [DRIVER] Realtek(R) Audio

        AWAYMODE:
        None.
        """;

    private const string LastWake = """
        Wake History Count - 1
        Wake History [0]
            Friendly Name: Goodix fingerprint reader
        """;

    private sealed class ScriptedRunner(string requests, string lastWake) : IProcessRunner
    {
        public Task<string> RunAsync(string exePath, string arguments, CancellationToken ct) =>
            Task.FromResult(arguments.Contains("lastwake", StringComparison.OrdinalIgnoreCase) ? lastWake : requests);
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<string> RunAsync(string exePath, string arguments, CancellationToken ct) =>
            throw new InvalidOperationException("powercfg not found");
    }

    private sealed class FakeTelemetry(int batteryPct, bool ac) : ITelemetryService
    {
        public int BatteryPct { get; set; } = batteryPct;
        public bool Ac { get; set; } = ac;
        public Task<TelemetrySnapshot> ReadAsync(CancellationToken ct) =>
            Task.FromResult(new TelemetrySnapshot(0, 0, 0, 0, 0, 0, 0, 0, BatteryPct, 0, Ac, false));
    }

    private sealed class RealFan : IFanController
    {
        public bool Initialized { get; private set; }
        public Task InitializeAsync(CancellationToken ct) { Initialized = true; return Task.CompletedTask; }
        public Task SetDutyAsync(int percent, CancellationToken ct) => Task.CompletedTask;
        public int ReadRpm() => 0;
    }

    private sealed class ThrowingFan : IFanController
    {
        public Task InitializeAsync(CancellationToken ct) => throw new InvalidOperationException("EC locked");
        public Task SetDutyAsync(int percent, CancellationToken ct) => Task.CompletedTask;
        public int ReadRpm() => 0;
    }

    private sealed class FakeTdpController(bool verified) : ITdpController
    {
        public Task<TdpApplyResult> ApplyAsync(TdpProfile profile, CancellationToken ct) =>
            Task.FromResult(new TdpApplyResult(profile, new TdpReadout(profile.StapmW, profile.FastW), verified, 1));
    }

    private sealed class FakeTdpBackend : ITdpBackend
    {
        public Task ApplyAsync(TdpProfile profile, CancellationToken ct) => Task.CompletedTask;
        public Task<TdpReadout> ReadAsync(CancellationToken ct) => Task.FromResult(new TdpReadout(0, 0));
    }

    private static StandbyService Service(
        IProcessRunner runner,
        IFanController? fan = null,
        ITdpController? tdp = null,
        ITdpBackend? backend = null,
        ITelemetryService? telemetry = null) =>
        new(tdp ?? new FakeTdpController(true),
            backend ?? new StubTdpBackend(),
            fan ?? new StubFanController(),
            telemetry ?? new FakeTelemetry(0, false),
            logger: null,
            runner: runner);

    private static StandbyRestoreStep Step(StandbyRestoreOutcome o, string name) =>
        o.Steps.Single(s => s.Name == name);

    [Fact]
    public async Task Status_reports_parsed_powercfg_diagnostics()
    {
        var s = await Service(new ScriptedRunner(Requests, LastWake)).GetStatusAsync(CancellationToken.None);

        Assert.True(s.DiagnosticsAvailable);
        Assert.Null(s.DiagnosticsError);
        Assert.Equal("Goodix fingerprint reader", s.TopWakeReason);
        Assert.Equal(new[] { "[DRIVER] Realtek(R) Audio" }, s.Blockers);
    }

    [Fact]
    public async Task Silent_powercfg_is_reported_as_unavailable_not_as_a_clean_bill_of_health()
    {
        var s = await Service(new ScriptedRunner("", "")).GetStatusAsync(CancellationToken.None);

        Assert.False(s.DiagnosticsAvailable);
        Assert.NotNull(s.DiagnosticsError);
        Assert.Null(s.TopWakeReason);
        Assert.Empty(s.Blockers);
    }

    [Fact]
    public async Task An_empty_request_list_is_available_and_genuinely_empty()
    {
        var s = await Service(new ScriptedRunner("DISPLAY:\nNone.\n", "Wake History Count - 0"))
            .GetStatusAsync(CancellationToken.None);

        Assert.True(s.DiagnosticsAvailable);
        Assert.Empty(s.Blockers);
        Assert.Null(s.TopWakeReason);   // no wake recorded is not the same as "we could not look"
    }

    [Fact]
    public async Task A_failing_powercfg_degrades_instead_of_throwing()
    {
        var s = await Service(new ThrowingRunner()).GetStatusAsync(CancellationToken.None);

        Assert.False(s.DiagnosticsAvailable);
        Assert.Contains("powercfg", s.DiagnosticsError);
        Assert.Null(s.LastDrainPctPerHour);
    }

    [Fact]
    public async Task Drain_is_null_until_a_suspend_has_actually_been_measured()
    {
        var s = await Service(new ScriptedRunner(Requests, LastWake)).GetStatusAsync(CancellationToken.None);

        Assert.Null(s.LastDrainPctPerHour);
        Assert.Null(s.LastDrainSleptHours);
        Assert.Null(s.LastDrainAt);
    }

    [Fact]
    public async Task Restore_with_stub_backends_claims_nothing()
    {
        var svc = Service(new ScriptedRunner(Requests, LastWake));

        var outcome = await svc.RestoreAsync(new TdpProfile(25, 25, 25, 90), CancellationToken.None);

        Assert.False(outcome.AnyRestored);
        Assert.False(Step(outcome, "fan").Restored);
        Assert.False(Step(outcome, "tdp").Restored);
        Assert.False(Step(outcome, "hid").Restored);
        Assert.All(outcome.Steps, s => Assert.NotEmpty(s.Detail));
    }

    [Fact]
    public async Task Restore_reports_the_steps_it_really_performed()
    {
        var fan = new RealFan();
        var svc = Service(new ScriptedRunner(Requests, LastWake),
            fan: fan, tdp: new FakeTdpController(verified: true), backend: new FakeTdpBackend());

        var outcome = await svc.RestoreAsync(new TdpProfile(25, 25, 25, 90), CancellationToken.None);

        Assert.True(fan.Initialized);
        Assert.True(Step(outcome, "fan").Restored);
        Assert.True(Step(outcome, "tdp").Restored);
        Assert.False(Step(outcome, "hid").Restored);   // no HID backend exists yet
        Assert.True(outcome.AnyRestored);
    }

    [Fact]
    public async Task An_unverified_tdp_reapply_does_not_count_as_restored()
    {
        var svc = Service(new ScriptedRunner(Requests, LastWake),
            fan: new RealFan(), tdp: new FakeTdpController(verified: false), backend: new FakeTdpBackend());

        var outcome = await svc.RestoreAsync(new TdpProfile(25, 25, 25, 90), CancellationToken.None);

        Assert.False(Step(outcome, "tdp").Restored);
    }

    [Fact]
    public async Task Restore_without_a_profile_says_so()
    {
        var svc = Service(new ScriptedRunner(Requests, LastWake), backend: new FakeTdpBackend());

        var outcome = await svc.RestoreAsync(null, CancellationToken.None);

        Assert.False(Step(outcome, "tdp").Restored);
        Assert.Contains("profile", Step(outcome, "tdp").Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_throwing_backend_becomes_a_failed_step_not_an_exception()
    {
        var svc = Service(new ScriptedRunner(Requests, LastWake), fan: new ThrowingFan());

        var outcome = await svc.RestoreAsync(new TdpProfile(25, 25, 25, 90), CancellationToken.None);

        Assert.False(Step(outcome, "fan").Restored);
        Assert.Contains("EC locked", Step(outcome, "fan").Detail);
    }

    [Fact]
    public async Task The_last_restore_outcome_shows_up_in_the_status()
    {
        var svc = Service(new ScriptedRunner(Requests, LastWake));
        Assert.Null((await svc.GetStatusAsync(CancellationToken.None)).LastRestore);

        await svc.RestoreAsync(new TdpProfile(25, 25, 25, 90), CancellationToken.None);

        var s = await svc.GetStatusAsync(CancellationToken.None);
        Assert.NotNull(s.LastRestore);
        Assert.Equal(3, s.LastRestore!.Steps.Count);
    }

    [Fact]
    public async Task A_sampled_suspend_surfaces_as_the_measured_drain()
    {
        var at = new DateTimeOffset(2026, 8, 28, 23, 0, 0, TimeSpan.Zero);
        var telemetry = new FakeTelemetry(90, ac: false);

        // Unbiased (sleep-excluding) time frozen while wall time jumps 8 h == the box was suspended.
        var svc = new StandbyService(
            new FakeTdpController(true), new StubTdpBackend(), new StubFanController(),
            telemetry, logger: null, runner: new ScriptedRunner(Requests, LastWake),
            clock: new FixedUnbiasedClock(() => TimeSpan.Zero), now: () => at);

        await svc.SampleAsync(CancellationToken.None);
        Assert.Null((await svc.GetStatusAsync(CancellationToken.None)).LastDrainPctPerHour);

        at = at.AddHours(8);
        telemetry.BatteryPct = 82;
        await svc.SampleAsync(CancellationToken.None);

        var s = await svc.GetStatusAsync(CancellationToken.None);
        Assert.Equal(1.0, s.LastDrainPctPerHour);
        Assert.Equal(8, s.LastDrainSleptHours);
        Assert.Equal(at, s.LastDrainAt);
    }

    [Fact]
    public async Task Sampling_without_a_usable_unbiased_clock_never_invents_a_drain()
    {
        var svc = new StandbyService(
            new FakeTdpController(true), new StubTdpBackend(), new StubFanController(),
            new FakeTelemetry(90, ac: false), logger: null, runner: new ScriptedRunner(Requests, LastWake),
            clock: new FixedUnbiasedClock(() => null));

        await svc.SampleAsync(CancellationToken.None);
        await svc.SampleAsync(CancellationToken.None);

        Assert.Null((await svc.GetStatusAsync(CancellationToken.None)).LastDrainPctPerHour);
    }

    private sealed class FixedUnbiasedClock(Func<TimeSpan?> read) : IUnbiasedClock
    {
        public TimeSpan? Read() => read();
    }
}
