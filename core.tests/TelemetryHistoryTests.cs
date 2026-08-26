// GPD Forge — telemetry history ring buffer tests. GPL-3.0-or-later.
using GpdForge.History;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class TelemetryHistoryTests
{
    private static TelemetrySnapshot Snap(double cpuTempC = 50) =>
        new(cpuTempC, 0, 0, 0, 0, 0, 0, 0, 80, 0, true, true);

    private static HistorySample Sample(long unixMs) => new(unixMs, Snap(unixMs));

    [Fact]
    public void Constructor_rejects_non_positive_capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelemetryHistory(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelemetryHistory(-1));
    }

    [Fact]
    public void Starts_empty()
    {
        var h = new TelemetryHistory(10);
        Assert.Equal(0, h.Count);
        Assert.Equal(10, h.Capacity);
        Assert.Empty(h.Recent(5));
        Assert.Empty(h.Since(0));
    }

    [Fact]
    public void Recent_returns_samples_oldest_first()
    {
        var h = new TelemetryHistory(10);
        h.Add(Sample(100));
        h.Add(Sample(200));
        h.Add(Sample(300));

        var recent = h.Recent(10);
        Assert.Equal(new long[] { 100, 200, 300 }, recent.Select(s => s.UnixMs));
    }

    [Fact]
    public void Recent_clamps_to_available_count_and_to_requested_max()
    {
        var h = new TelemetryHistory(10);
        h.Add(Sample(1));
        h.Add(Sample(2));

        Assert.Equal(2, h.Recent(100).Count);      // fewer held than asked
        Assert.Equal(2L, Assert.Single(h.Recent(1)).UnixMs); // asked for fewer than held — the single most-recent one
        Assert.Empty(h.Recent(0));
        Assert.Empty(h.Recent(-5));
    }

    [Fact]
    public void Wraparound_keeps_only_the_newest_capacity_samples_in_order()
    {
        var h = new TelemetryHistory(3);
        for (long i = 1; i <= 5; i++) h.Add(Sample(i));

        Assert.Equal(3, h.Count);
        Assert.Equal(new long[] { 3, 4, 5 }, h.Recent(10).Select(s => s.UnixMs));
    }

    [Fact]
    public void Wraparound_survives_multiple_full_laps()
    {
        var h = new TelemetryHistory(4);
        for (long i = 1; i <= 15; i++) h.Add(Sample(i)); // several full wraps of a 4-slot buffer

        Assert.Equal(4, h.Count);
        Assert.Equal(new long[] { 12, 13, 14, 15 }, h.Recent(10).Select(s => s.UnixMs));
    }

    [Fact]
    public void Since_filters_inclusively_and_preserves_order()
    {
        var h = new TelemetryHistory(10);
        foreach (var t in new long[] { 100, 200, 300, 400 }) h.Add(Sample(t));

        Assert.Equal(new long[] { 300, 400 }, h.Since(250).Select(s => s.UnixMs));
        Assert.Equal(new long[] { 100, 200, 300, 400 }, h.Since(0).Select(s => s.UnixMs));
        Assert.Equal(new long[] { 400 }, h.Since(400).Select(s => s.UnixMs)); // boundary is inclusive
        Assert.Empty(h.Since(401));
        Assert.Empty(h.Since(1_000));
    }

    [Fact]
    public void Since_respects_capacity_eviction()
    {
        var h = new TelemetryHistory(2);
        h.Add(Sample(1));
        h.Add(Sample(2));
        h.Add(Sample(3)); // evicts UnixMs=1

        Assert.Equal(new long[] { 2, 3 }, h.Since(0).Select(s => s.UnixMs));
    }

    [Fact]
    public void Snapshot_payload_round_trips_through_add_and_recent()
    {
        var h = new TelemetryHistory(5);
        h.Add(new HistorySample(42, Snap(cpuTempC: 73.5)));

        var only = Assert.Single(h.Recent(5));
        Assert.Equal(42, only.UnixMs);
        Assert.Equal(73.5, only.Snap.CpuTempC);
    }
}
