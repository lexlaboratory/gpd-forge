// GPD Forge - Standby Doctor tests. GPL-3.0-or-later.
using GpdForge.Fan;
using GpdForge.Standby;
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

public class PowerCfgParserTests
{
    private const string Requests = """
        DISPLAY:
        None.

        SYSTEM:
        [DRIVER] Realtek(R) Audio
        [PROCESS] \Device\HarddiskVolume4\game.exe

        AWAYMODE:
        None.
        """;

    private const string LastWake = """
        Wake History Count - 1
        Wake History [0]
          Wake Source Count - 1
          Wake Source [0]
            Type: Device
            Instance Path: USB\VID_27C6&PID_...
            Friendly Name: Goodix fingerprint reader
        """;

    [Fact]
    public void ParseRequests_lists_blockers_and_skips_none_and_headers()
    {
        var blockers = PowerCfgParser.ParseRequests(Requests);
        Assert.Equal(2, blockers.Count);
        Assert.Contains("[DRIVER] Realtek(R) Audio", blockers);
        Assert.DoesNotContain(blockers, b => b.Equals("None.", System.StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(blockers, b => b.EndsWith(':'));
    }

    [Fact]
    public void ParseLastWake_extracts_friendly_name()
    {
        Assert.Equal("Goodix fingerprint reader", PowerCfgParser.ParseLastWake(LastWake));
    }

    [Fact]
    public void ParseLastWake_returns_null_when_absent()
    {
        Assert.Null(PowerCfgParser.ParseLastWake("Wake History Count - 0"));
    }
}

public class StandbyRestoreTests
{
    private sealed class RecordingFan(List<string> order) : IFanController
    {
        public Task InitializeAsync(CancellationToken ct) { order.Add("fan"); return Task.CompletedTask; }
        public Task SetDutyAsync(int percent, CancellationToken ct) => Task.CompletedTask;
        public int ReadRpm() => 0;
    }

    private sealed class RecordingTdp(List<string> order, bool verified) : ITdpController
    {
        public Task<TdpApplyResult> ApplyAsync(TdpProfile profile, CancellationToken ct)
        {
            order.Add("tdp");
            return Task.FromResult(new TdpApplyResult(profile, new TdpReadout(profile.StapmW, profile.FastW), verified, 1));
        }
    }

    private sealed class NoRunner : IProcessRunner
    {
        public Task<string> RunAsync(string exePath, string arguments, CancellationToken ct) => Task.FromResult("");
    }

    [Fact]
    public async Task RestoreOnResume_reinits_fan_before_reapplying_tdp()
    {
        var order = new List<string>();
        var doctor = new StandbyDoctor(new NoRunner(), new RecordingTdp(order, verified: true), new RecordingFan(order));

        var result = await doctor.RestoreOnResumeAsync(new TdpProfile(20, 20, 20, 90), CancellationToken.None);

        Assert.Equal(new[] { "fan", "tdp" }, order);          // fan re-init first
        Assert.Equal(new[] { "fan-reinit", "tdp-reapplied-verified" }, result.Steps);
    }

    [Fact]
    public async Task RestoreOnResume_reports_unverified_tdp()
    {
        var order = new List<string>();
        var doctor = new StandbyDoctor(new NoRunner(), new RecordingTdp(order, verified: false), new RecordingFan(order));

        var result = await doctor.RestoreOnResumeAsync(new TdpProfile(30, 30, 30, 90), CancellationToken.None);

        Assert.Contains("tdp-reapplied-unverified", result.Steps);
    }
}
