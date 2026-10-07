// GPD Forge — RyzenAdj-backed TDP backend. GPL-3.0-or-later.
//
// Applies/reads TDP by driving ryzenadj (LGPL). This DOES write to the SMU when enabled, so it is
// OFF by default (see Program.cs: only wired when GPDFORGE_ENABLE_HARDWARE=1) and requires the
// service to run elevated/SYSTEM. Long term the SMU access moves behind the PawnIO broker instead
// of ryzenadj's own driver.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace GpdForge.Tdp;

/// <summary>Runs an external process and returns its stdout. Injected so the backend is testable.</summary>
public interface IProcessRunner
{
    Task<string> RunAsync(string exePath, string arguments, CancellationToken ct);
}

public sealed class SystemProcessRunner(TimeSpan? timeout = null) : IProcessRunner
{
    private const int MaxCapturedStandardErrorChars = 4096;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<string> RunAsync(string exePath, string arguments, CancellationToken ct)
    {
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "Process timeout must be positive.");

        using var p = new Process
        {
            StartInfo = new ProcessStartInfo(exePath, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath))!,
            },
        };
        p.Start();

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync(linkedCts.Token);
        Task<(string Text, bool Truncated)> stderrTask = ReadBoundedAsync(
            p.StandardError, MaxCapturedStandardErrorChars, linkedCts.Token);

        try
        {
            await p.WaitForExitAsync(linkedCts.Token);
            string stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (p.ExitCode != 0)
            {
                string detail = stderr.Text.Length == 0 ? "" : $" stderr: {stderr.Text}{(stderr.Truncated ? "… [truncated]" : "")}";
                throw new InvalidOperationException($"Process '{Path.GetFileName(exePath)}' exited with code {p.ExitCode}.{detail}");
            }

            return stdout;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || timeoutCts.IsCancellationRequested)
        {
            KillTree(p);
            try { await p.WaitForExitAsync(CancellationToken.None); } catch { /* best effort */ }
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { /* canceled streams are expected */ }

            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw new TimeoutException($"Process '{Path.GetFileName(exePath)}' exceeded the {_timeout.TotalSeconds:0.###} second timeout.");
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(
        StreamReader reader, int maxChars, CancellationToken ct)
    {
        var captured = new StringBuilder(Math.Min(maxChars, 512));
        var buffer = new char[1024];
        bool truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            int remaining = maxChars - captured.Length;
            if (remaining > 0) captured.Append(buffer, 0, Math.Min(read, remaining));
            if (read > remaining) truncated = true;
        }
        return (captured.ToString(), truncated);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* process already exited */ }
        catch (System.ComponentModel.Win32Exception) { /* best effort if the child has already gone */ }
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
    /// Reads the four limits GPD Forge writes out of `ryzenadj --info`. A label that is not in the
    /// output yields <c>null</c>, never 0 — see <see cref="TdpReadout"/>. The `?? 0` that used to be
    /// here turned a missing line into a confident zero-watt reading. Slow and Tctl since audit round
    /// 2 (2026-09-24): ryzenadj always printed them, and a reassert that did not read them could not
    /// see the firmware put its own back.
    /// </summary>
    public static TdpReadout Parse(string info) => new(
        Whole(FindValue(info, "STAPM LIMIT")),
        Whole(FindValue(info, "PPT LIMIT FAST")),
        Whole(FindValue(info, "PPT LIMIT SLOW")),
        Whole(FindValue(info, "THM LIMIT CORE")));

    private static int? Whole(double? v) => v is double d ? (int)Math.Round(d) : null;

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
