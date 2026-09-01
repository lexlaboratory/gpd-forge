// GPD Forge — CSV export for telemetry history (pure formatting, no I/O). GPL-3.0-or-later.
using System.Globalization;
using System.Text;
using GpdForge.Telemetry;

namespace GpdForge.History;

/// <summary>
/// Pure CSV formatting for exported telemetry history — no file or HTTP I/O; callers (the
/// <c>/history/export.csv</c> endpoint, tests, ...) decide where the resulting string goes. Every
/// field is numeric, boolean or an ISO-8601 timestamp, so none can ever contain a comma or quote —
/// no quoting/escaping logic is needed. Uses invariant culture throughout so the file is well-formed
/// regardless of the host machine's regional settings (a comma decimal separator would otherwise
/// corrupt the columns).
/// </summary>
public static class CsvExport
{
    public const string Header =
        "unixMs,isoTime,cpuTempC,gpuTempC,packageW,cpuClockMhz,fanRpm,fps,fps1PctLow,batteryPct,dischargeW,acConnected,tdpVerified";

    /// <summary>Header row + one row per sample, in the order given (this does not sort). Always ends
    /// with the header — even for an empty sequence — so downstream tools see valid, column-typed
    /// CSV rather than an empty file.</summary>
    public static string ToCsv(IEnumerable<HistorySample> samples)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        foreach (var sample in samples)
            AppendRow(sb, sample);
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, HistorySample sample)
    {
        var t = sample.Snap;
        string iso = DateTimeOffset.FromUnixTimeMilliseconds(sample.UnixMs).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        sb.Append(sample.UnixMs).Append(',')
          .Append(iso).Append(',')
          .Append(Cell(t.CpuTempC)).Append(',')
          .Append(Cell(t.GpuTempC)).Append(',')
          .Append(Cell(t.PackageW)).Append(',')
          .Append(Cell(t.CpuClockMhz)).Append(',')
          .Append(Cell(t.FanRpm)).Append(',')
          .Append(Cell(t.Fps)).Append(',')
          .Append(Cell(t.Fps1PctLow)).Append(',')
          .Append(Cell(t.BatteryPct)).Append(',')
          .Append(Cell(t.DischargeW)).Append(',')
          .Append(t.AcConnected ? "true" : "false").Append(',')
          .Append(t.TdpVerified ? "true" : "false")
          .Append('\n');
    }

    /// <summary>
    /// An unmeasured value becomes an EMPTY cell, not a zero.
    ///
    /// This is the export people load into a spreadsheet to plot temperature against power, and a
    /// column of zeros for a sensor the machine could not read produces a chart that says the CPU
    /// was at 0 °C — the same lie the API used to tell, preserved in a file that outlives the
    /// session that produced it. Every CSV convention reads an empty field as "no data", and both
    /// Excel and pandas skip it rather than plotting it.
    /// </summary>
    private static string Cell<T>(T? value) where T : struct, IFormattable
        => value?.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;
}
