// GPD Forge — the fixed GPU clock. GPL-3.0-or-later.
//
// Measured on this device 2026-10-13: the Radeon 890M offers no clock tuning through ADLX (every
// IsSupported*Tuning answered false), but PSMU 0x89 — RyzenAdj's --gfx-clk, what Motion Assistant
// uses — held it at exactly 1200 MHz. It is a FIXED clock, not a ceiling, and no message is known that
// hands it back to the driver: 0 pinned the GPU at 0 MHz, and only a restart undid it. So the clock
// is a state that lasts until the handheld restarts, and these tests pin that it is reported as such.
using GpdForge.Gpu;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class GpuClockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpdforge-gpuclock-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Boot = new(2026, 10, 13, 13, 39, 11, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Boot.AddMinutes(10);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class FakeControl : IGpuClockControl
    {
        public bool Supported { get; init; } = true;
        public Func<int, int>? Echo { get; init; }
        public bool Throws { get; init; }
        public List<int> Sent { get; } = [];

        public Task<int> ForceAsync(int mhz, CancellationToken ct)
        {
            Sent.Add(mhz);
            if (Throws) throw new TimeoutException("simulated SMU timeout");
            return Task.FromResult(Echo?.Invoke(mhz) ?? mhz);
        }
    }

    private GpuClockService Service(IGpuClockControl control, DateTimeOffset? boot = null) =>
        new(control, new GpuClockStore(_dir), now: () => Now, bootUtc: () => boot ?? Boot);

    [Fact]
    public async Task Setting_a_clock_fixes_it_and_says_it_lasts_until_a_restart()
    {
        var control = new FakeControl();
        var service = Service(control);

        var outcome = await service.SetAsync(1200, CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Equal([1200], control.Sent);
        Assert.Equal(1200, outcome.View.ForcedMhz);
        Assert.Equal(Now, outcome.View.SinceUtc);
        Assert.True(outcome.View.ResetsOnRestart);
        Assert.Equal(1200, service.View().ForcedMhz);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(399)]
    [InlineData(2901)]
    public async Task A_clock_outside_the_band_is_refused_without_touching_the_hardware(int mhz)
    {
        var control = new FakeControl();

        var outcome = await Service(control).SetAsync(mhz, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal("bad_mhz", outcome.Code);
        Assert.Empty(control.Sent);
        Assert.Null(outcome.View.ForcedMhz);
    }

    [Fact]
    public async Task A_clock_the_SMU_did_not_hand_back_is_not_claimed()
    {
        var service = Service(new FakeControl { Echo = _ => 0 });

        var outcome = await service.SetAsync(1200, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal("not_confirmed", outcome.Code);
        Assert.Null(service.View().ForcedMhz);
    }

    [Fact]
    public async Task A_failed_write_is_reported_and_not_claimed()
    {
        var service = Service(new FakeControl { Throws = true });

        var outcome = await service.SetAsync(1200, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal("smu_failed", outcome.Code);
        Assert.Contains("simulated SMU timeout", outcome.Error);
        Assert.Null(service.View().ForcedMhz);
    }

    [Fact]
    public async Task Without_a_backend_that_can_do_it_nothing_is_sent()
    {
        var control = new FakeControl { Supported = false };

        var outcome = await Service(control).SetAsync(1200, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal("not_supported", outcome.Code);
        Assert.False(outcome.View.Supported);
        Assert.Empty(control.Sent);
    }

    [Fact]
    public async Task The_clock_is_still_reported_after_the_service_restarts_on_the_same_boot()
    {
        await Service(new FakeControl()).SetAsync(1500, CancellationToken.None);

        // A minute of drift in the computed boot time is still the same boot.
        var after = Service(new FakeControl(), boot: Boot.AddSeconds(40));

        Assert.Equal(1500, after.View().ForcedMhz);
    }

    [Fact]
    public async Task After_the_handheld_restarts_the_clock_reads_automatic_again()
    {
        await Service(new FakeControl()).SetAsync(1500, CancellationToken.None);

        var after = Service(new FakeControl(), boot: Boot.AddHours(3));

        Assert.Null(after.View().ForcedMhz);
        Assert.Null(new GpuClockStore(_dir).Read());
    }

    [Fact]
    public async Task Reapplying_resends_the_fixed_clock_and_nothing_without_one()
    {
        var control = new FakeControl();
        var service = Service(control);

        Assert.False(await service.ReapplyAsync(CancellationToken.None));
        Assert.Empty(control.Sent);

        await service.SetAsync(1300, CancellationToken.None);
        Assert.True(await service.ReapplyAsync(CancellationToken.None));
        Assert.Equal([1300, 1300], control.Sent);
    }

    [Fact]
    public async Task Waking_up_re_applies_a_fixed_GPU_clock()
    {
        // Whether a suspend drops the forced clock is not known; re-sending the same value is harmless,
        // and without it the screen could say "fixed at 1300 MHz" over a clock that is automatic again.
        var control = new FakeControl();
        var service = Service(control);
        await service.SetAsync(1300, CancellationToken.None);
        var standby = new NullStandby();
        var clock = new ScriptedClock(TimeSpan.FromHours(1), TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        var wall = new Queue<DateTimeOffset>([Now, Now.AddHours(8)]);
        var worker = new GpdForge.Standby.ResumeRestoreWorker(
            standby, new GpdForge.Api.ModeState(), clock, logger: null, interval: TimeSpan.FromMilliseconds(1),
            now: () => wall.Count > 0 ? wall.Dequeue() : Now.AddHours(8), gpuClock: service);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (control.Sent.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal([1300, 1300], control.Sent);
    }

    private sealed class NullStandby : GpdForge.Standby.IStandbyService
    {
        public Task<GpdForge.Standby.StandbyStatus> GetStatusAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SampleAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<GpdForge.Standby.StandbyRestoreOutcome> RestoreAsync(GpdForge.Tdp.TdpProfile? activeProfile, CancellationToken ct) =>
            Task.FromResult(new GpdForge.Standby.StandbyRestoreOutcome(DateTimeOffset.UtcNow, []));
    }

    private sealed class ScriptedClock(params TimeSpan?[] readings) : GpdForge.Standby.IUnbiasedClock
    {
        private int _i;
        public TimeSpan? Read() => _i < readings.Length ? readings[_i++] : readings[^1];
    }
}
