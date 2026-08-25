// GPD Forge - Standby Doctor. GPL-3.0-or-later.
//
// Diagnoses Modern Standby drain/wake issues (parsing powercfg output) and restores device state
// on resume - the thing MotionAssistant / GPD Tool do not do. Parsing is pure + unit-tested; the
// restore orchestration re-runs the (gated) hardware backends in the right order.
using GpdForge.Fan;
using GpdForge.Tdp;

namespace GpdForge.Standby;

public sealed record StandbyReport(string? LastWakeReason, IReadOnlyList<string> SleepBlockers);

public sealed record RestoreResult(IReadOnlyList<string> Steps);

public static class PowerCfgParser
{
    /// <summary>Extract sleep blockers from `powercfg /requests` (non-"None." entries under each category).</summary>
    public static IReadOnlyList<string> ParseRequests(string requestsOutput)
    {
        var blockers = new List<string>();
        if (string.IsNullOrWhiteSpace(requestsOutput)) return blockers;

        foreach (var raw in requestsOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            // Category headers end with ':' (DISPLAY:, SYSTEM:, ...). Skip them and the literal "None."
            if (line.EndsWith(':')) continue;
            if (line.Equals("None.", StringComparison.OrdinalIgnoreCase)) continue;
            blockers.Add(line);
        }
        return blockers;
    }

    /// <summary>Extract the last wake reason (Friendly Name) from `powercfg /lastwake`.</summary>
    public static string? ParseLastWake(string lastWakeOutput)
    {
        if (string.IsNullOrWhiteSpace(lastWakeOutput)) return null;
        foreach (var raw in lastWakeOutput.Split('\n'))
        {
            var line = raw.Trim();
            const string key = "Friendly Name:";
            var idx = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var val = line[(idx + key.Length)..].Trim();
                if (val.Length > 0) return val;
            }
        }
        return null;
    }
}

public sealed class StandbyDoctor(IProcessRunner runner, ITdpController tdp, IFanController fan)
{
    public async Task<StandbyReport> DiagnoseAsync(CancellationToken ct)
    {
        var requests = await runner.RunAsync("powercfg", "/requests", ct);
        var lastWake = await runner.RunAsync("powercfg", "/lastwake", ct);
        return new StandbyReport(PowerCfgParser.ParseLastWake(lastWake), PowerCfgParser.ParseRequests(requests));
    }

    /// <summary>On resume the EC and SMU forget state; re-init the fan, then re-verify TDP - in that order.</summary>
    public async Task<RestoreResult> RestoreOnResumeAsync(TdpProfile activeProfile, CancellationToken ct)
    {
        var steps = new List<string>();
        await fan.InitializeAsync(ct);
        steps.Add("fan-reinit");
        var tdpResult = await tdp.ApplyAsync(activeProfile, ct);
        steps.Add(tdpResult.Verified ? "tdp-reapplied-verified" : "tdp-reapplied-unverified");
        // HID restore hooks in here once the virtual-pad/device-hiding layer lands.
        return new RestoreResult(steps);
    }
}
