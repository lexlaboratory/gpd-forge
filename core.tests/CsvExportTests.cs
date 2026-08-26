// GPD Forge — CSV export tests. GPL-3.0-or-later.
using GpdForge.History;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class CsvExportTests
{
    private static readonly TelemetrySnapshot Snap = new(
        CpuTempC: 61.5, GpuTempC: 58.2, PackageW: 20, CpuClockMhz: 3300, FanRpm: 3600, FanDutyPct: 45,
        Fps: 60, Fps1PctLow: 55, BatteryPct: 78, DischargeW: 18.4, AcConnected: false, TdpVerified: true);

    [Fact]
    public void Header_names_every_column_in_order()
    {
        Assert.Equal(
            "unixMs,isoTime,cpuTempC,gpuTempC,packageW,cpuClockMhz,fanRpm,fps,batteryPct,dischargeW,acConnected,tdpVerified",
            CsvExport.Header);
    }

    [Fact]
    public void Empty_input_is_just_the_header()
    {
        string csv = CsvExport.ToCsv(Array.Empty<HistorySample>());
        Assert.Equal(CsvExport.Header + "\n", csv);
    }

    [Fact]
    public void Formats_one_row_with_iso_time_and_lowercase_booleans()
    {
        var stamp = new DateTimeOffset(2026, 8, 25, 13, 45, 30, 250, TimeSpan.Zero);
        var sample = new HistorySample(stamp.ToUnixTimeMilliseconds(), Snap);

        string csv = CsvExport.ToCsv(new[] { sample });

        string expected = CsvExport.Header + "\n" +
            $"{stamp.ToUnixTimeMilliseconds()},2026-08-25T13:45:30.250Z,61.5,58.2,20,3300,3600,60,78,18.4,false,true\n";
        Assert.Equal(expected, csv);
    }

    [Fact]
    public void Renders_one_row_per_sample_in_the_given_order()
    {
        var a = new HistorySample(1, Snap);
        var b = new HistorySample(2, Snap with { CpuTempC = 70 });

        string csv = CsvExport.ToCsv(new[] { a, b });

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // header + 2 rows
        Assert.StartsWith("1,", lines[1]);
        Assert.StartsWith("2,", lines[2]);
        Assert.Contains("70", lines[2]);
    }
}
