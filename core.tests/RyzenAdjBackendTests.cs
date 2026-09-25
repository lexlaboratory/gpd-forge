// GPD Forge — RyzenAdj backend/parser tests. GPL-3.0-or-later.
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

public class RyzenAdjBackendTests
{
    private const string SampleInfo = """
        CPU Family: Phoenix
        SMU BIOS Interface Version: 24
        Version: v0.16.0

        PM Table: 0x400005
        | Name            | Value  | Parameter   |
        | STAPM LIMIT     | 15.000 | stapm-limit |
        | STAPM VALUE     |  3.456 |             |
        | PPT LIMIT FAST  | 20.000 | fast-limit  |
        | PPT VALUE FAST  |  5.000 |             |
        | PPT LIMIT SLOW  | 17.000 | slow-limit  |
        """;

    private sealed class FakeRunner : IProcessRunner
    {
        public string? LastArgs { get; private set; }
        public string Output { get; init; } = "";
        public Task<string> RunAsync(string exePath, string arguments, CancellationToken ct)
        {
            LastArgs = arguments;
            return Task.FromResult(Output);
        }
    }

    [Fact]
    public void Parse_extracts_stapm_and_fast_limits()
    {
        var readout = RyzenAdjOutput.Parse(SampleInfo);
        Assert.Equal(15, readout.StapmW);
        Assert.Equal(20, readout.PptW);
    }

    [Fact]
    public void Parse_returns_null_when_labels_absent()
    {
        // Inverted on 2026-09-02, and the old assertion is the point: this test REQUIRED the zero, so
        // it pinned the defect. A `ryzenadj --info` run with no "STAPM LIMIT" line — the tool missing,
        // a failed call, a changed output format — produced a confident 0 W reading. Zero watts is not
        // something a CPU reports; it is the absence of a reading wearing a number.
        //
        // It mattered downstream: ClosedLoopTdpController.Holds compares the readback against the
        // request to decide `verified`, and "we could not read it" is not the same fact as "the
        // firmware refused the write".
        var readout = RyzenAdjOutput.Parse("no table here");
        Assert.Null(readout.StapmW);
        Assert.Null(readout.PptW);
    }

    [Fact]
    public void Parse_returns_the_value_it_finds_and_null_for_the_one_it_does_not()
    {
        // The half that keeps the change honest: a partial read must report the field it HAS.
        // Collapsing the whole readout to null on one missing label would lose real information.
        var readout = RyzenAdjOutput.Parse("STAPM LIMIT | 15.000 | stapm limit");
        Assert.Equal(15, readout.StapmW);
        Assert.Null(readout.PptW);
    }

    // ---------------------------------------------------------------------------------------------
    // Strix Point (HX 370). The fixture is rebuilt from ryzenadj's printf format, NOT captured: the
    // read-only run on the device needs elevation and was refused — see RyzenAdjFixtures.cs.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_reads_the_strix_point_table_with_crlf_line_endings()
    {
        var readout = RyzenAdjOutput.Parse(RyzenAdjFixtures.StrixPointInfo);

        Assert.Equal(15, readout.StapmW);
        Assert.Equal(20, readout.PptW);
    }

    [Fact]
    public void Parse_takes_the_limit_row_not_the_value_row_beneath_it()
    {
        // "STAPM VALUE 6.482" sits one line under "STAPM LIMIT 15.000". Reading the live value as the
        // limit would make every readback "moved", and the 30 s reassert would rewrite forever.
        var readout = RyzenAdjOutput.Parse(RyzenAdjFixtures.StrixPointInfo);
        Assert.NotEqual(6, readout.StapmW);
        Assert.NotEqual(9, readout.PptW);
    }

    [Fact]
    public void Parse_of_a_non_elevated_run_is_no_reading_not_zero()
    {
        var readout = RyzenAdjOutput.Parse(RyzenAdjFixtures.NotElevated);
        Assert.Null(readout.StapmW);
        Assert.Null(readout.PptW);
    }

    [Fact]
    public void Parse_of_a_table_ryzenadj_cannot_decode_is_no_reading()
    {
        // The error line carries a number (-1). It must not be mistaken for a limit.
        var readout = RyzenAdjOutput.Parse(RyzenAdjFixtures.StrixPointNoTable);
        Assert.Null(readout.StapmW);
        Assert.Null(readout.PptW);
    }

    [Fact]
    public void Parse_of_nan_limits_is_no_reading()
    {
        // `nan` is ryzenadj saying "not in this table". The parameter column ("stapm-limit") has no
        // digits, so nothing else on the row can be picked up in its place.
        var readout = RyzenAdjOutput.Parse(RyzenAdjFixtures.StrixPointNanLimits);
        Assert.Null(readout.StapmW);
        Assert.Null(readout.PptW);
    }

    [Fact]
    public void Parse_rounds_a_fractional_limit_to_whole_watts()
    {
        // The SMU reports what it stored, which is not always the round number that was sent.
        var info = RyzenAdjFixtures.StrixPointInfo
            .Replace("|    15.000 | stapm-limit", "|    14.998 | stapm-limit")
            .Replace("|    20.000 | fast-limit ", "|    20.4   | fast-limit ");
        var readout = RyzenAdjOutput.Parse(info);
        Assert.Equal(15, readout.StapmW);
        Assert.Equal(20, readout.PptW);
    }

    [Fact]
    public async Task A_strix_point_readback_verifies_the_profile_that_produced_it()
    {
        // End to end through the closed loop: `windows` (15/20/17, Tctl 92) applied, this table read
        // back, verified on the first attempt.
        var runner = new FakeRunner { Output = RyzenAdjFixtures.StrixPointInfo };
        var controller = new ClosedLoopTdpController(new RyzenAdjBackend(runner, "ryzenadj.exe"), new NoWait());

        var r = await controller.ApplyAsync(new TdpProfile(15, 20, 17, 92), TdpOwner.Mode, CancellationToken.None);

        Assert.True(r.Verified);
        Assert.Equal(1, r.Attempts);
        Assert.Equal(new TdpReadout(15, 20), r.Observed);
    }

    [Fact]
    public async Task ReadAsync_parses_runner_output()
    {
        var backend = new RyzenAdjBackend(new FakeRunner { Output = SampleInfo }, "ryzenadj.exe");
        var readout = await backend.ReadAsync(CancellationToken.None);
        Assert.Equal(15, readout.StapmW);
        Assert.Equal(20, readout.PptW);
    }

    [Fact]
    public async Task ApplyAsync_passes_milliwatts_to_ryzenadj()
    {
        var runner = new FakeRunner();
        var backend = new RyzenAdjBackend(runner, "ryzenadj.exe");
        await backend.ApplyAsync(new TdpProfile(25, 25, 25, 90), CancellationToken.None);

        Assert.Contains("--stapm-limit=25000", runner.LastArgs);
        Assert.Contains("--fast-limit=25000", runner.LastArgs);
        Assert.Contains("--tctl-temp=90", runner.LastArgs);
    }
}
