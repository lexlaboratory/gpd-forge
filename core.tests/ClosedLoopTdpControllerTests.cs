// GPD Forge — closed-loop TDP controller tests. GPL-3.0-or-later.
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

public class ClosedLoopTdpControllerTests
{
    private sealed class NoDelay : IDelay
    {
        public Task WaitAsync(TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Fake backend whose readback is a function of how many applies have happened.</summary>
    private sealed class FakeBackend(Func<int, TdpReadout> read) : ITdpBackend
    {
        public int Applies { get; private set; }
        public Task ApplyAsync(TdpProfile profile, CancellationToken ct) { Applies++; return Task.CompletedTask; }
        public Task<TdpReadout> ReadAsync(CancellationToken ct) => Task.FromResult(read(Applies));
    }

    private static readonly TdpProfile Want = new(StapmW: 25, FastW: 25, SlowW: 25, TctlC: 90);

    private static ClosedLoopTdpController Controller(ITdpBackend backend, ClosedLoopTdpController.Options? opt = null)
        => new(backend, new NoDelay(), logger: null, options: opt);

    [Fact]
    public async Task Verifies_when_limit_holds_immediately()
    {
        var backend = new FakeBackend(_ => new TdpReadout(25, 25));
        var result = await Controller(backend).ApplyAsync(Want, TdpOwner.Manual, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, backend.Applies);
    }

    [Fact]
    public async Task Reports_unverified_when_firmware_reverts()
    {
        // Firmware caps at 30W and never honors the 25W request → never matches.
        var backend = new FakeBackend(_ => new TdpReadout(30, 30));
        var result = await Controller(backend, new ClosedLoopTdpController.Options(MaxAttempts: 4)).ApplyAsync(Want, TdpOwner.Manual, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(4, result.Attempts);
        Assert.Equal(30, result.Observed.StapmW);
    }

    [Fact]
    public async Task Retries_until_the_limit_holds()
    {
        // Reverts on the first read, holds from the second.
        var backend = new FakeBackend(applies => applies >= 2 ? new TdpReadout(25, 25) : new TdpReadout(18, 18));
        var result = await Controller(backend).ApplyAsync(Want, TdpOwner.Manual, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task Accepts_readings_within_tolerance()
    {
        var backend = new FakeBackend(_ => new TdpReadout(24, 26)); // ±1 of 25
        var result = await Controller(backend, new ClosedLoopTdpController.Options(ToleranceW: 1)).ApplyAsync(Want, TdpOwner.Manual, CancellationToken.None);

        Assert.True(result.Verified);
    }
}
