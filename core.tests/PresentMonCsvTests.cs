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


/// <summary>
/// The fields the target-aware feed needs on top of the frame interval: the row's own time, its
/// process id, and a fallback interval for rows whose preferred one is "NA".
/// </summary>
public class PresentMonCsvRowTimeTests
{
    [Fact]
    public void Resolves_the_real_251_header_the_installer_ships()
    {
        Assert.True(PresentMonCsv.TryParseHeader(PresentMonFixtures.Header251, out var cols));
        Assert.True(cols.IsValid);
        Assert.Equal(10, cols.FrameTimeMs);   // MsBetweenPresents: 2.5.1 has no "FrameTime" column
        Assert.Equal(8, cols.Time);           // TimeInMs — the present's own time, preferred over CPU start
        Assert.Equal(1.0, cols.TimeUnitMs);
        Assert.Equal(1, cols.ProcessId);
    }

    [Fact]
    public void Reads_TimeInSeconds_as_seconds()
    {
        PresentMonCsv.TryParseHeader(PresentMonFixtures.Header1x, out var cols);
        Assert.Equal(7, cols.Time);
        Assert.Equal(1000.0, cols.TimeUnitMs);

        Assert.True(PresentMonCsv.TryParseRow(
            "game.exe,4242,0x1234,DXGI,1,0,0,12.5,16.67,16.70,0.42,8.1,17.0", cols, out var row));
        Assert.Equal(12_500.0, row.TimeMs!.Value, 3);
        Assert.Equal(4242, row.ProcessId);
    }

    [Fact]
    public void Reads_CPUStartTime_as_milliseconds_and_prefers_FrameTime()
    {
        // PresentMon 2.x prints every default time in ms; only the "...InSeconds" names are seconds.
        Assert.True(PresentMonCsv.TryParseHeader(PresentMonFixtures.HeaderV2FrameTime, out var cols));
        Assert.Equal(8, cols.Time);
        Assert.Equal(1.0, cols.TimeUnitMs);
        Assert.Equal(9, cols.FrameTimeMs);            // FrameTime wins when both exist...
        Assert.Equal(10, cols.FallbackFrameTimeMs);   // ...MsBetweenPresents is kept for its NA rows
    }

    [Fact]
    public void Resolves_the_lower_case_v1_column_names_of_PresentMon_2()
    {
        // 2.5.1's --v1_metrics header spells it "msBetweenPresents"; names are case-insensitive.
        const string header = "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents";
        Assert.True(PresentMonCsv.TryParseHeader(header, out var cols));
        Assert.Equal(9, cols.FrameTimeMs);
    }

    [Fact]
    public void A_NA_FrameTime_falls_back_to_MsBetweenPresents_instead_of_dropping_the_frame()
    {
        PresentMonCsv.TryParseHeader(PresentMonFixtures.HeaderV2FrameTime, out var cols);
        const string line = "game.exe,4242,0x1,DXGI,0,0,0,Hardware: Independent Flip,1000.5,NA,7.14,6.0,1.1";

        Assert.True(PresentMonCsv.TryParseRow(line, cols, out var row));
        Assert.Equal(7.14, row.FrameTimeMs, 3);
        Assert.Equal(1000.5, row.TimeMs!.Value, 3);
    }

    [Fact]
    public void A_NA_time_keeps_the_frame_with_no_row_time()
    {
        // The frame interval is still honest; only its position in time is unknown, and the feed
        // stamps such a row with its arrival time instead.
        PresentMonCsv.TryParseHeader(PresentMonFixtures.Header251, out var cols);
        var line = PresentMonFixtures.Row251("game.exe", 100, 7.14).Replace("100.0000", "NA");

        Assert.True(PresentMonCsv.TryParseRow(line, cols, out var row));
        Assert.Null(row.TimeMs);
        Assert.Equal(7.14, row.FrameTimeMs, 3);
    }

    [Fact]
    public void NA_in_columns_the_feed_does_not_read_is_ignored()
    {
        PresentMonCsv.TryParseHeader(PresentMonFixtures.Header251, out var cols);
        // Real row: MsBetweenSimulationStart, MsAnimationError and three latency columns are "NA".
        Assert.True(PresentMonCsv.TryParseRow(PresentMonFixtures.Rows251[0], cols, out var row));
        Assert.Equal("Orca.exe", row.Application);
        Assert.Equal(82.7006, row.FrameTimeMs, 3);
        Assert.Equal(165.1775, row.TimeMs!.Value, 3);
        Assert.Equal(7644, row.ProcessId);
    }

    [Fact]
    public void A_zero_interval_is_still_not_a_frame()
    {
        // The real capture's webview rows: MsBetweenPresents 0 is a repeated present. With no usable
        // fallback the row is dropped, as before.
        PresentMonCsv.TryParseHeader(PresentMonFixtures.Header251, out var cols);
        Assert.False(PresentMonCsv.TryParseRow(PresentMonFixtures.Rows251[5], cols, out _));
    }

    [Fact]
    public void A_row_with_both_intervals_NA_is_dropped()
    {
        PresentMonCsv.TryParseHeader(PresentMonFixtures.HeaderV2FrameTime, out var cols);
        Assert.False(PresentMonCsv.TryParseRow(
            "game.exe,4242,0x1,DXGI,0,0,0,Hardware: Independent Flip,1000.5,NA,NA,6.0,1.1", cols, out _));
    }

    [Theory]
    [InlineData("TimeInQPC")]
    [InlineData("CPUStartQPC")]
    [InlineData("CPUStartDateTime")]
    public void Time_columns_in_units_the_feed_cannot_convert_are_not_used(string timeColumn)
    {
        // Only reachable with --qpc_time/--date_time, which the probe never passes; if one ever
        // appears the feed falls back to arrival time rather than misreading ticks as milliseconds.
        var header = $"Application,ProcessID,{timeColumn},MsBetweenPresents";
        Assert.True(PresentMonCsv.TryParseHeader(header, out var cols));
        Assert.Equal(-1, cols.Time);
    }
}
