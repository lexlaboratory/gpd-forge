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
    public void Parse_returns_zero_when_labels_absent()
    {
        var readout = RyzenAdjOutput.Parse("no table here");
        Assert.Equal(0, readout.StapmW);
        Assert.Equal(0, readout.PptW);
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
