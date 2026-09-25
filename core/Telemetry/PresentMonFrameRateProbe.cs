// GPD Forge — frame-rate telemetry via Intel PresentMon. GPL-3.0-or-later.
//
// READ-ONLY. Hosts PresentMon as a child process and streams its CSV output into a PresentMonFeed,
// which keeps the last 10 s of every app's frames on the capture's own timeline. Reads answer for the
// TARGET process — the foreground app, else a rule-matched game (see FrameTarget). Nothing here
// writes to hardware.
//
// Why shell out to the signed PresentMon.exe instead of consuming ETW in-process: the in-process
// route (Microsoft.Diagnostics.Tracing.TraceEvent) drags native DLLs along, and this machine runs
// Smart App Control in Enforcement, which has already killed unsigned native binaries more than
// once. Intel signs its PresentMon releases, and SAC does run signed, reputable binaries.
//
// Every failure mode — PresentMon missing, blocked by SAC, exiting, emitting nothing — lands in the
// same place: TryRead returns false and telemetry reports fps 0, meaning "not available". We never
// invent a frame rate.
using System.Diagnostics;
using GpdForge.Profiles;
using Microsoft.Extensions.Logging;

namespace GpdForge.Telemetry;

public sealed class PresentMonFrameRateProbe : IFrameRateProbe, IFrameTimeSource
{
    private readonly PresentMonFeed _feed = new();
    private readonly IForegroundApp? _foreground;
    private readonly IModeResolver? _rules;
    private readonly TimeProvider _time;
    // Arrival times come from the monotonic counter, anchored once to the wall clock. FrameClock
    // takes a running minimum over them, and a wall-clock step backwards (NTP, a manual change, a
    // resume) would latch into that minimum and misplace every later frame.
    private readonly DateTimeOffset _epoch;
    private readonly long _epochTimestamp;
    private readonly ILogger? _logger;
    private readonly string _exePath;
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private bool _disposed;
    private bool _startFailureLogged;

    /// <param name="foreground">Names the foreground process. Null (or a null answer) leaves the
    /// choice to the rules and to the previous target.</param>
    /// <param name="rules">The user's app rules; an app one of them names is a game worth reading
    /// when the foreground app is not presenting.</param>
    public PresentMonFrameRateProbe(
        string exePath,
        ILogger<PresentMonFrameRateProbe>? logger = null,
        IForegroundApp? foreground = null,
        IModeResolver? rules = null,
        TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        _exePath = exePath;
        _logger = logger;
        _foreground = foreground;
        _rules = rules;
        _time = time ?? TimeProvider.System;
        _epoch = _time.GetUtcNow();
        _epochTimestamp = _time.GetTimestamp();
    }

    /// <summary>
    /// Locates the bundled PresentMon next to the service binary, or on PATH.
    /// Null when it is not installed — the caller then simply does not register the probe.
    /// </summary>
    public static string? Locate(string? baseDirectory = null)
    {
        var dir = baseDirectory ?? AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(dir, "presentmon", "PresentMon.exe"),
                     Path.Combine(dir, "PresentMon.exe"),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(segment.Trim(), "PresentMon.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* a malformed PATH segment is not our problem */ }
        }
        return null;
    }

    public bool TryRead(out FpsSample sample)
    {
        sample = default;
        if (_disposed) return false;

        EnsureRunning();
        return _feed.TryRead(Now(), Foreground(), IsRuleMatched, out sample);
    }

    public bool TryGetFrameTimes(out FrameTimeSeries series)
    {
        series = null!;
        if (_disposed) return false;

        EnsureRunning();
        return _feed.TryGetFrameTimes(Now(), Foreground(), IsRuleMatched, out series);
    }

    private DateTimeOffset Now() => _epoch + _time.GetElapsedTime(_epochTimestamp);

    private string? Foreground()
    {
        // Win32ForegroundApp already swallows its own failures; this guards any other implementation,
        // because a foreground lookup going wrong must cost the target choice, not the FPS reading.
        try { return _foreground?.Current(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            return null;
        }
    }

    private bool IsRuleMatched(string application) => _rules?.ModeFor(application) is not null;

    /// <summary>
    /// Starts PresentMon if it is not up. Also covers the restart case: PresentMon exits on its own
    /// when its ETW session is torn down (a driver reset, another consumer taking over), and a probe
    /// that never restarts would silently report "no FPS" forever.
    /// </summary>
    private void EnsureRunning()
    {
        if (_process is { HasExited: false }) return;

        try
        {
            _process?.Dispose();
            _process = null;

            var psi = new ProcessStartInfo(_exePath)
            {
                // Track every process and stream CSV to stdout; no file, no console window.
                // Deliberately NOT --terminate_on_proc_exit: with no --process_name target, "all
                // target processes have exited" is true from the start and PresentMon would quit
                // immediately. --no_console_stats keeps the live stats table out of our stdout.
                // No --v1_metrics/--v2_metrics either: the parser resolves columns by name, so it
                // reads whichever schema the installed version emits.
                Arguments = "--output_stdout --stop_existing_session --no_console_stats",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var proc = Process.Start(psi);
            if (proc is null)
            {
                LogStartFailureOnce(null);
                return;
            }

            _process = proc;
            _startFailureLogged = false;
            _ = Task.Run(() => PumpAsync(proc, _cts.Token), _cts.Token);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Most likely: SAC blocked the binary, or it is not where we thought it was.
            LogStartFailureOnce(ex);
        }
    }

    private void LogStartFailureOnce(Exception? ex)
    {
        if (_startFailureLogged) return;
        _startFailureLogged = true;
        _logger?.LogWarning(ex, "PresentMon could not be started ({Path}); FPS stays unavailable", _exePath);
    }

    private async Task PumpAsync(Process proc, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break; // stdout closed: PresentMon exited

                // Arrival time only bounds when a frame happened; the feed places it by the row's own
                // time column (see FrameClock), which is what survives stdout arriving in bursts.
                _feed.Accept(line, Now());
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "PresentMon output pump stopped; FPS will retry on the next read");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _logger?.LogDebug(ex, "PresentMon did not stop cleanly");
        }
        _process?.Dispose();
        _cts.Dispose();
    }
}
