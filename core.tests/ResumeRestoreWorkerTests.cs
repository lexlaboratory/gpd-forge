// GPD Forge - automatic resume-restore tests. GPL-3.0-or-later.
//
// ResumeDetectorTests pins the arithmetic; this pins the behaviour that arithmetic exists for -
// that waking the machine actually re-applies fan and power limits, with nobody pressing anything.
// Both clocks are injected, so a suspend is simulated rather than performed.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GpdForge.Api;
using GpdForge.Standby;
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

public class ResumeRestoreWorkerTests
{
    /// <summary>Records what was restored and for which profile, and never touches hardware.</summary>
    private sealed class SpyStandbyService : IStandbyService
    {
        public List<TdpProfile?> Restores { get; } = new();
        public TaskCompletionSource Restored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<StandbyStatus> GetStatusAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SampleAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<StandbyRestoreOutcome> RestoreAsync(TdpProfile? activeProfile, CancellationToken ct)
        {
            Restores.Add(activeProfile);
            Restored.TrySetResult();
            return Task.FromResult(new StandbyRestoreOutcome(
                DateTimeOffset.UtcNow, new[] { new StandbyRestoreStep("fan", true, "re-initialised") }));
        }
    }

    /// <summary>Replays a scripted sequence of clock readings, then holds the last one.</summary>
    private sealed class ScriptedClock(params TimeSpan?[] readings) : IUnbiasedClock
    {
        private int _i;
        public TimeSpan? Read() => _i < readings.Length ? readings[_i++] : readings[^1];
    }

    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Waking_up_re_applies_the_active_modes_profile_without_anyone_asking()
    {
        var standby = new SpyStandbyService();
        var mode = new ModeState { Active = "gaming" };

        // The unbiased clock barely moves while the wall clock jumps eight hours: a suspend.
        var clock = new ScriptedClock(TimeSpan.FromHours(1), TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        var wall = new Queue<DateTimeOffset>(new[] { T0, T0 + TimeSpan.FromHours(8) });

        var worker = new ResumeRestoreWorker(
            standby, mode, clock, logger: null,
            interval: TimeSpan.FromMilliseconds(1),
            now: () => wall.Count > 0 ? wall.Dequeue() : T0 + TimeSpan.FromHours(8));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await standby.Restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.NotEmpty(standby.Restores);
        // The profile handed over is the one for the mode that was actually active.
        Assert.Equal(GpdForge.Profiles.ModeProfiles.For("gaming"), standby.Restores[0]);
    }

    [Fact]
    public async Task Ordinary_awake_polling_restores_nothing()
    {
        var standby = new SpyStandbyService();
        var clock = new ScriptedClock(
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1) + TimeSpan.FromSeconds(5),
            TimeSpan.FromHours(1) + TimeSpan.FromSeconds(10));

        int tick = 0;
        var worker = new ResumeRestoreWorker(
            standby, new ModeState(), clock, logger: null,
            interval: TimeSpan.FromMilliseconds(1),
            now: () => T0 + TimeSpan.FromSeconds(5 * tick++));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await Task.Delay(120);
        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.Empty(standby.Restores);
    }

    [Fact]
    public async Task An_unavailable_clock_stops_the_worker_instead_of_spinning_forever()
    {
        var standby = new SpyStandbyService();

        var worker = new ResumeRestoreWorker(
            standby, new ModeState(), new ScriptedClock(new TimeSpan?[] { null }), logger: null,
            interval: TimeSpan.FromMilliseconds(1), now: () => T0);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await Task.Delay(80);

        // ExecuteAsync returned on its own; nothing was restored and no exception escaped.
        Assert.Empty(standby.Restores);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
    }
}
