// GPD Forge - Standby Doctor. GPL-3.0-or-later.
//
// Diagnoses Modern Standby drain/wake issues by parsing powercfg output. Parsing is pure +
// unit-tested. The resume restore that used to live here now sits in StandbyService, which is the
// only place that knows whether the fan/TDP backends are real or stubs and can therefore report
// honestly what a restore actually did.
using GpdForge.Fan;
using GpdForge.Tdp;

namespace GpdForge.Standby;

public sealed record StandbyReport(string? LastWakeReason, IReadOnlyList<string> SleepBlockers);

/// <summary>
/// A diagnosis plus whether it could be made at all. "powercfg said nothing" and "powercfg said
/// there are no blockers" are opposite answers and must never collapse into the same empty list.
/// </summary>
public sealed record StandbyDiagnosis(
    bool Available, string? Error, string? LastWakeReason, IReadOnlyList<string> SleepBlockers);

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
    // The restore backends moved to StandbyService; they stay on this constructor (and are exposed
    // here) so the existing --probe-standby call shape in Program.cs keeps compiling unchanged.
    public ITdpController Tdp { get; } = tdp;
    public IFanController Fan { get; } = fan;

    public async Task<StandbyReport> DiagnoseAsync(CancellationToken ct)
    {
        var d = await DiagnoseDetailedAsync(ct);
        return new StandbyReport(d.LastWakeReason, d.SleepBlockers);
    }

    /// <summary>
    /// Runs both powercfg queries and reports whether they answered. Never throws: a missing or
    /// refusing powercfg degrades to Available=false with the reason attached.
    /// </summary>
    public async Task<StandbyDiagnosis> DiagnoseDetailedAsync(CancellationToken ct)
    {
        string requests, lastWake;
        try
        {
            requests = await runner.RunAsync("powercfg", "/requests", ct);
            lastWake = await runner.RunAsync("powercfg", "/lastwake", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new StandbyDiagnosis(false, $"powercfg could not be run: {ex.Message}", null, []);
        }

        // Both queries print to stderr and leave stdout empty when they are refused (they need an
        // elevated session). Silence is "we could not look", not "there is nothing to report".
        if (string.IsNullOrWhiteSpace(requests) && string.IsNullOrWhiteSpace(lastWake))
        {
            return new StandbyDiagnosis(
                false, "powercfg returned no output — /requests and /lastwake need an elevated session.", null, []);
        }

        return new StandbyDiagnosis(
            true, null, PowerCfgParser.ParseLastWake(lastWake), PowerCfgParser.ParseRequests(requests));
    }
}
