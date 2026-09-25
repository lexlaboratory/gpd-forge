// GPD Forge — the fan's own loop: reads the sampler's cache, never waits on ForgeWorker. GPL-3.0-or-later.
//
// Until 2026-09-24 the fan was the last block of ForgeWorker's tick, after the guardian's and the
// charge guard's ryzenadj applies. Each apply runs ryzenadj twice, and under a sustained throttle
// that stretched the tick to ~1.7 s — the fan answered a heat spike late by exactly as long as a
// TDP write took. These tests pin the separation: a fixed 1 s timer, the latest cached sample, and
// the same safety rules as before.
using GpdForge.Api;
using GpdForge.Fan;
using GpdForge.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GpdForge.Core.Tests;

public class FanWorkerTests
{
    private static int Curve(string mode, double tempC) =>
        FanCurve.DutyForTemp(tempC, FanCurve.ForMode(mode)!, FanCurve.DefaultHysteresisC, 0);

    private static TelemetrySnapshot Temp(double? c) => TelemetrySnapshot.Unmeasured with { CpuTempC = c };

    private static (FanWorker worker, ScriptedTelemetrySource source, RecordingFanController fan, FanState state, ManualTimeProvider clock)
        Build(string mode = "Balanced", int manualDuty = 128)
    {
        var source = new ScriptedTelemetrySource();
        var fan = new RecordingFanController();
        var state = new FanState { Mode = mode, ManualDuty = manualDuty };
        var clock = new ManualTimeProvider();
        return (new FanWorker(source, state, fan, NullLogger<FanWorker>.Instance, clock), source, fan, state, clock);
    }

    // ---------------------------------------------------------------------------------------------
    // One tick
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_tick_drives_the_curve_from_the_latest_cached_sample()
    {
        var (worker, source, fan, _, _) = Build("Balanced");
        source.Publish(Temp(75));

        worker.Tick();

        Assert.Equal(new[] { $"duty:{Curve("Balanced", 75)}" }, fan.Calls);
    }

    [Fact]
    public void A_stalled_sampler_is_a_missing_reading_and_hands_back_to_firmware_after_the_grace()
    {
        // A sample that never advances is not a live temperature: the cache would hold the last one
        // forever, and a fan running a curve off it would be running blind. It gets the same grace as
        // a missed read — then firmware takes over, exactly as for a dead sensor.
        var (worker, source, fan, _, clock) = Build("Balanced");
        source.Publish(Temp(72));

        worker.Tick();
        for (int s = 1; s <= (int)FanTickPolicy.SensorGraceSeconds; s++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            worker.Tick();
        }
        Assert.All(fan.Calls, c => Assert.StartsWith("duty:", c));

        clock.Advance(TimeSpan.FromSeconds(1));
        worker.Tick();
        Assert.Equal("auto", fan.Calls[^1]);
    }

    [Fact]
    public void A_fresh_sample_each_tick_keeps_the_curve_running_indefinitely()
    {
        var (worker, source, fan, _, clock) = Build("Balanced");
        for (int s = 0; s < 20; s++)
        {
            source.Publish(Temp(70));
            worker.Tick();
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(20, fan.Calls.Count);
        Assert.DoesNotContain("auto", fan.Calls);
    }

    [Fact]
    public void A_panic_mode_switch_lands_on_the_next_tick()
    {
        // POST /panic sets FanState.Mode = Aggressive. The worker reads the mode fresh every tick, so
        // the switch is at most one 1 s tick away whatever ForgeWorker is doing.
        var (worker, source, fan, state, clock) = Build("Auto");
        source.Publish(Temp(80));
        worker.Tick();
        Assert.Equal(new[] { "auto" }, fan.Calls);

        state.Mode = "Aggressive";
        clock.Advance(TimeSpan.FromSeconds(1));
        source.Publish(Temp(80));
        worker.Tick();

        Assert.Equal($"duty:{Curve("Aggressive", 80)}", fan.Calls[^1]);
    }

    [Fact]
    public void Auto_is_written_once_not_every_tick()
    {
        var (worker, source, fan, _, clock) = Build("Auto");
        for (int s = 0; s < 5; s++) { source.Publish(Temp(60)); worker.Tick(); clock.Advance(TimeSpan.FromSeconds(1)); }
        Assert.Equal(new[] { "auto" }, fan.Calls);
    }

    [Fact]
    public void A_controller_that_throws_leaves_the_fan_on_firmware_and_the_loop_alive()
    {
        var (worker, source, fan, _, clock) = Build("Manual", manualDuty: 200);
        fan.ThrowOnDuty = true;
        source.Publish(Temp(70));

        worker.Tick();   // must not throw
        Assert.Equal("auto", fan.Calls[^1]);

        fan.ThrowOnDuty = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        worker.Tick();
        Assert.Equal("duty:200", fan.Calls[^1]);
    }

    // ---------------------------------------------------------------------------------------------
    // The loop
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_loop_ticks_on_its_own_timer_and_restores_auto_on_shutdown()
    {
        // The source publishes once and then never again — a sampler (or a ForgeWorker) stuck in a
        // slow call. The fan keeps its 1 Hz cadence anyway, because nothing it waits on is theirs.
        var source = new ScriptedTelemetrySource();
        source.Publish(Temp(70));
        var fan = new RecordingFanController();
        var worker = new FanWorker(source, new FanState { Mode = "Manual", ManualDuty = 150 }, fan,
            NullLogger<FanWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => fan.Calls.Count(c => c == "duty:150") >= 3, TimeSpan.FromSeconds(6));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal("auto", fan.Calls[^1]);
    }

    [Fact]
    public async Task The_loop_waits_for_the_first_sample_before_touching_the_fan()
    {
        // Without this the first tick would see the unsampled placeholder, hand the fan to firmware,
        // and then pay a MAX safety write a second later when the first real reading arrived.
        var source = new ScriptedTelemetrySource();
        var fan = new RecordingFanController();
        var worker = new FanWorker(source, new FanState { Mode = "Balanced" }, fan, NullLogger<FanWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(400);
            Assert.Empty(fan.Calls);

            source.Publish(Temp(75));
            await WaitUntil(() => fan.Calls.Count > 0, TimeSpan.FromSeconds(3));
            Assert.Equal($"duty:{Curve("Balanced", 75)}", fan.Calls[0]);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Wiring
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Only_the_fan_worker_drives_the_fan_and_it_reads_the_cache_not_the_hardware()
    {
        static IEnumerable<Type> Params(Type t) => t.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType);

        Assert.DoesNotContain(typeof(IGpdFanController), Params(typeof(ForgeWorker)));
        Assert.Contains(typeof(IGpdFanController), Params(typeof(FanWorker)));
        Assert.Contains(typeof(ITelemetrySource), Params(typeof(FanWorker)));
        Assert.DoesNotContain(typeof(ITelemetryService), Params(typeof(FanWorker)));
    }

    [Fact]
    public void Program_registers_the_fan_worker_as_a_hosted_service()
    {
        var source = File.ReadAllText(ProgramRoutes.FindProgramCs());
        // A factory since the sustained fan: it hands the worker the active power mode.
        Assert.Matches(@"AddHostedService\(sp => ActivatorUtilities\.CreateInstance<FanWorker>\(sp,", source);
        Assert.Contains("FanWorker.IsSustainedMode(sp.GetRequiredService<ModeState>().Active)", source);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(50);
        }
    }

    [Fact]
    public void The_ai_mode_runs_the_curve_sustained()
    {
        // ROADMAP "sustained fan curve for AI mode": the worker asks the active power mode, so an
        // `ai` session holds its duty through a dip that `windows` follows down.
        int DutyAfterDip(string powerMode)
        {
            var source = new ScriptedTelemetrySource();
            var fan = new RecordingFanController();
            var clock = new ManualTimeProvider();
            var worker = new FanWorker(source, new FanState { Mode = "Balanced" }, fan,
                NullLogger<FanWorker>.Instance, clock, sustained: () => FanWorker.IsSustainedMode(powerMode));
            for (int s = 0; s < 120; s++) { source.Publish(Temp(75)); worker.Tick(); clock.Advance(TimeSpan.FromSeconds(1)); }
            for (int s = 0; s < 280; s++) { source.Publish(Temp(68)); worker.Tick(); clock.Advance(TimeSpan.FromSeconds(1)); }
            return int.Parse(fan.Calls[^1].Split(':')[1]);
        }

        Assert.Equal(Curve("Balanced", 75), DutyAfterDip("ai"));
        Assert.True(DutyAfterDip("windows") < Curve("Balanced", 75));
    }

    [Theory]
    [InlineData("ai", true)]
    [InlineData("AI", true)]
    [InlineData("gaming", false)]
    [InlineData("windows", false)]
    [InlineData(null, false)]
    [InlineData("nonsense", false)]
    public void Only_a_sustained_catalogue_mode_runs_the_curve_sustained(string? mode, bool expected) =>
        Assert.Equal(expected, FanWorker.IsSustainedMode(mode));
}

/// <summary>A source a test publishes into by hand. Waiters wake on publish, or time out to Latest.</summary>
public sealed class ScriptedTelemetrySource : ITelemetrySource
{
    private TelemetryReading _latest = TelemetryReading.Unsampled;
    private TaskCompletionSource _published = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TelemetryReading Latest => Volatile.Read(ref _latest);

    public void Publish(TelemetrySnapshot snapshot)
    {
        var previous = Latest;
        Volatile.Write(ref _latest, new TelemetryReading(snapshot, DateTimeOffset.UtcNow, previous.Sequence + 1));
        Interlocked.Exchange(ref _published, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private long _lastWaitAfter = -1;

    /// <summary>The sequence the consumer last asked to wait past. A loop that paces itself on new
    /// samples (ForgeWorker) has finished the tick for sample N once it asks to wait past N.</summary>
    public long LastWaitAfter => Interlocked.Read(ref _lastWaitAfter);

    public async Task<TelemetryReading> WaitForNewerAsync(long afterSequence, TimeSpan timeout, CancellationToken ct)
    {
        Interlocked.Exchange(ref _lastWaitAfter, afterSequence);
        var signal = Volatile.Read(ref _published);
        if (Latest.Sequence > afterSequence) return Latest;
        try { await signal.Task.WaitAsync(timeout, ct); }
        catch (TimeoutException) { }
        return Latest;
    }
}

/// <summary>Records every command the worker sends, in order. Thread-safe: the loop test writes from
/// the worker's thread while the test reads.</summary>
public sealed class RecordingFanController : IGpdFanController
{
    private readonly List<string> _calls = [];
    public bool ThrowOnDuty { get; set; }

    public IReadOnlyList<string> Calls { get { lock (_calls) return _calls.ToArray(); } }

    public bool Available => true;
    public bool IsManual { get; private set; }

    public bool SetManualDuty(int duty0to255)
    {
        if (ThrowOnDuty) throw new InvalidOperationException("EC port gone");
        lock (_calls) _calls.Add($"duty:{duty0to255}");
        IsManual = true;
        return true;
    }

    public void SetAuto()
    {
        lock (_calls) _calls.Add("auto");
        IsManual = false;
    }

    public int? ReadDuty() => null;
    public void Dispose() { }
}
