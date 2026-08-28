// GPD Forge — PresentMon CSV parsing + frame-window aggregation tests. GPL-3.0-or-later.
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class PresentMonCsvTests
{
    // PresentMon 1.x header. Both 1.x and 2.x name the frame interval "MsBetweenPresents" and the
    // process "Application" — what changes between them is the surrounding columns and their order,
    // which is exactly why the parser resolves by name instead of by index.
    private const string Header1x =
        "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped," +
        "TimeInSeconds,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsUntilRenderComplete,MsUntilDisplayed";

    // PresentMon 2.x default (v2) metrics: same two names, different neighbours and position.
    private const string Header2x =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags," +
        "AllowsTearing,PresentMode,CPUStartTime,MsBetweenPresents,MsCPUBusy,MsGPUTime,DisplayLatency";

    [Fact]
    public void Resolves_columns_from_a_1x_header()
    {
        Assert.True(PresentMonCsv.TryParseHeader(Header1x, out var cols));
        Assert.True(cols.IsValid);
        Assert.Equal(0, cols.Application);
        Assert.Equal(8, cols.FrameTimeMs);
    }

    [Fact]
    public void Resolves_columns_from_a_2x_header_by_name_not_position()
    {
        Assert.True(PresentMonCsv.TryParseHeader(Header2x, out var cols));
        Assert.Equal(0, cols.Application);
        Assert.Equal(9, cols.FrameTimeMs); // moved relative to 1.x; index would have been wrong
    }

    [Fact]
    public void Accepts_the_alternate_FrameTime_column_name()
    {
        // Some PresentMon builds label the interval "FrameTime". Tolerated so a version bump
        // degrades to "no FPS" only if the name is genuinely unknown, not merely different.
        const string header = "Application,ProcessID,PresentMode,FrameTime,CPUBusy";
        Assert.True(PresentMonCsv.TryParseHeader(header, out var cols));
        Assert.Equal(3, cols.FrameTimeMs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("some,unrelated,columns")]
    [InlineData("Application,ProcessID")] // no frame-time column at all
    public void Rejects_a_line_that_is_not_a_usable_header(string line)
    {
        Assert.False(PresentMonCsv.TryParseHeader(line, out _));
    }

    [Fact]
    public void Parses_a_valid_row()
    {
        PresentMonCsv.TryParseHeader(Header1x, out var cols);
        const string line =
            "game.exe,4242,0x1234,DXGI,1,0,0,12.5,16.67,16.70,0.42,8.1,17.0";

        Assert.True(PresentMonCsv.TryParseRow(line, cols, out var row));
        Assert.Equal("game.exe", row.Application);
        Assert.Equal(16.67, row.FrameTimeMs, 3);
    }

    [Fact]
    public void Drops_a_truncated_row_rather_than_guessing()
    {
        PresentMonCsv.TryParseHeader(Header1x, out var cols);
        // The process died mid-write; the frame-time column never made it out.
        Assert.False(PresentMonCsv.TryParseRow("game.exe,4242,0x1234,DXGI", cols, out _));
    }

    [Fact]
    public void Drops_a_repeated_header_row()
    {
        PresentMonCsv.TryParseHeader(Header1x, out var cols);
        // PresentMon re-emits the header when a new capture starts.
        Assert.False(PresentMonCsv.TryParseRow(Header1x, cols, out _));
    }

    [Theory]
    [InlineData("game.exe,4242,0x1234,DXGI,1,0,0,12.5,0,16.70,0.42,8.1,17.0")]      // zero interval
    [InlineData("game.exe,4242,0x1234,DXGI,1,0,0,12.5,-3.0,16.70,0.42,8.1,17.0")]   // negative
    [InlineData("game.exe,4242,0x1234,DXGI,1,0,0,12.5,NaN,16.70,0.42,8.1,17.0")]    // not a number
    [InlineData(",4242,0x1234,DXGI,1,0,0,12.5,16.67,16.70,0.42,8.1,17.0")]          // no app name
    public void Drops_rows_that_cannot_yield_an_honest_frame_time(string line)
    {
        PresentMonCsv.TryParseHeader(Header1x, out var cols);
        Assert.False(PresentMonCsv.TryParseRow(line, cols, out _));
    }

    [Fact]
    public void Parses_frame_time_with_invariant_culture()
    {
        // The daemon runs as LocalSystem, but a comma-decimal locale must never turn 16.67 into 1667.
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("es-MX");
            PresentMonCsv.TryParseHeader(Header1x, out var cols);
            Assert.True(PresentMonCsv.TryParseRow(
                "game.exe,4242,0x1234,DXGI,1,0,0,12.5,16.67,16.70,0.42,8.1,17.0", cols, out var row));
            Assert.Equal(16.67, row.FrameTimeMs, 3);
        }
        finally { Thread.CurrentThread.CurrentCulture = prev; }
    }
}

public class FrameWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reports_nothing_when_empty()
    {
        var w = new FrameWindow(TimeSpan.FromSeconds(2));
        Assert.False(w.TryAggregate(T0, out _));
    }

    [Fact]
    public void Reports_nothing_on_a_single_frame()
    {
        var w = new FrameWindow(TimeSpan.FromSeconds(2));
        w.Add("game.exe", 16.67, T0);
        Assert.False(w.TryAggregate(T0, out _));
    }

    [Fact]
    public void Averages_a_steady_60fps_stream()
    {
        var w = new FrameWindow(TimeSpan.FromSeconds(2));
        for (int i = 0; i < 100; i++) w.Add("game.exe", 16.67, T0.AddMilliseconds(i * 16.67));

        Assert.True(w.TryAggregate(T0.AddSeconds(1), out var s));
        Assert.Equal(60.0, s.Fps, 1);
        Assert.Equal("game.exe", s.Process);
    }

    [Fact]
    public void One_percent_low_tracks_the_worst_frames_not_the_mean()
    {
        // 99 good frames at 60fps + one 100ms stall: the mean barely moves, the 1% low collapses.
        var w = new FrameWindow(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 99; i++) w.Add("game.exe", 16.67, T0.AddMilliseconds(i * 16.67));
        w.Add("game.exe", 100.0, T0.AddMilliseconds(99 * 16.67));

        Assert.True(w.TryAggregate(T0.AddSeconds(2), out var s));
        Assert.True(s.Fps > 50, $"mean should stay high, was {s.Fps}");
        Assert.Equal(10.0, s.Fps1PctLow, 1); // 1000 / 100ms
        Assert.True(s.Fps1PctLow < s.Fps);
    }

    [Fact]
    public void One_percent_low_falls_back_to_the_worst_frame_on_small_samples()
    {
        Assert.Equal(20.0, FrameWindow.OnePercentLowMs([10.0, 20.0, 15.0]), 3);
    }

    [Fact]
    public void One_percent_low_of_nothing_is_zero()
    {
        Assert.Equal(0.0, FrameWindow.OnePercentLowMs([]));
    }

    [Fact]
    public void Evicts_frames_older_than_the_window()
    {
        var w = new FrameWindow(TimeSpan.FromSeconds(2));
        for (int i = 0; i < 10; i++) w.Add("game.exe", 16.67, T0.AddMilliseconds(i * 10));

        // ...and nothing since. Ten seconds later the window is empty: the game stopped rendering,
        // which must read as "no FPS", not as a stale 60.
        Assert.False(w.TryAggregate(T0.AddSeconds(10), out _));
    }

    [Fact]
    public void Attributes_the_reading_to_the_busiest_process()
    {
        var w = new FrameWindow(TimeSpan.FromSeconds(2));
        for (int i = 0; i < 50; i++) w.Add("game.exe", 16.67, T0.AddMilliseconds(i * 16.67));
        for (int i = 0; i < 5; i++) w.Add("dwm.exe", 8.0, T0.AddMilliseconds(i * 16.67));

        Assert.True(w.TryAggregate(T0.AddSeconds(1), out var s));
        Assert.Equal("game.exe", s.Process);
        Assert.Equal(60.0, s.Fps, 1); // dwm's faster frames must not inflate the game's reading
    }
}
