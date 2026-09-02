// GPD Forge — RyzenAdj-backed TDP backend. GPL-3.0-or-later.
//
// Applies/reads TDP by driving ryzenadj (LGPL). This DOES write to the SMU when enabled, so it is
// OFF by default (see Program.cs: only wired when GPDFORGE_ENABLE_HARDWARE=1) and requires the
// service to run elevated/SYSTEM. Long term the SMU access moves behind the PawnIO broker instead
// of ryzenadj's own driver.
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace GpdForge.Tdp;

/// <summary>Runs an external process and returns its stdout. Injected so the backend is testable.</summary>
public interface IProcessRunner
{
    Task<string> RunAsync(string exePath, string arguments, CancellationToken ct);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<string> RunAsync(string exePath, string arguments, CancellationToken ct)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo(exePath, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        p.Start();
        string stdout = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return stdout;
    }
}

public sealed partial class RyzenAdjBackend(
    IProcessRunner runner, string exePath, ILogger<RyzenAdjBackend>? logger = null) : ITdpBackend
{
    public async Task ApplyAsync(TdpProfile profile, CancellationToken ct)
    {
        // ryzenadj takes milliwatts and °C.
        string args =
            $"--stapm-limit={profile.StapmW * 1000} " +
            $"--fast-limit={profile.FastW * 1000} " +
            $"--slow-limit={profile.SlowW * 1000} " +
            $"--tctl-temp={profile.TctlC}";
        logger?.LogInformation("ryzenadj apply: {Args}", args);
        await runner.RunAsync(exePath, args, ct);
    }

    public async Task<TdpReadout> ReadAsync(CancellationToken ct)
    {
        string info = await runner.RunAsync(exePath, "--info", ct);
        return RyzenAdjOutput.Parse(info);
    }
}

/// <summary>Pure parser for `ryzenadj --info` output (unit-tested, no process needed).</summary>
public static partial class RyzenAdjOutput
{
    /// <summary>
    /// Reads the two limits out of `ryzenadj --info`. A label that is not in the output yields
    /// <c>null</c>, never 0 — see <see cref="TdpReadout"/>. The `?? 0` that used to be here turned a
    /// missing line into a confident zero-watt reading.
    /// </summary>
    public static TdpReadout Parse(string info)
    {
        int? stapm = FindValue(info, "STAPM LIMIT") is double s ? (int)Math.Round(s) : null;
        int? ppt = FindValue(info, "PPT LIMIT FAST") is double p ? (int)Math.Round(p) : null;
        return new TdpReadout(stapm, ppt);
    }

    private static double? FindValue(string text, string label)
    {
        foreach (var raw in text.Split('\n'))
        {
            string line = Whitespace().Replace(raw, " ").Trim();
            if (line.Contains(label, StringComparison.OrdinalIgnoreCase))
            {
                var m = Number().Match(line);
                if (m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return v;
            }
        }
        return null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[-+]?\d+(\.\d+)?")]
    private static partial Regex Number();
}
