// GPD Forge — the foreground app as the USER'S session sees it. GPL-3.0-or-later.
//
// The daemon runs as LocalSystem in session 0 (docs/adr/0002). Session 0 has no interactive desktop,
// so GetForegroundWindow always returns NULL there — the audit of 2026-09-24 read GET /app-rules three
// times while the user had windows open, and every `lastMatch.process` was null. Everything that asked
// "what is in front?" was silently blind on the installed service: the FPS target (FrameTarget) and
// the auto-profile worker alike.
//
// The answer has to come from the user's session. The GPU agent already runs there (`--gpu-agent`,
// started at logon) and already checks in with the daemon every 3 s, so it reports the foreground
// process too (POST /session/foreground). This class prefers that report while it is fresh and falls
// back to the local Win32 query otherwise — which is the right answer when the daemon itself runs in
// a user session (a dev run, `--probe-focus`) and a harmless null in session 0.
namespace GpdForge.Profiles;

/// <summary>Where <see cref="SessionForegroundApp.Current"/> got its answer.</summary>
public readonly record struct ForegroundReport(string? Process, string Source, long? AgeMs);

public sealed class SessionForegroundApp(IForegroundApp local, TimeProvider? time = null) : IForegroundApp
{
    /// <summary>
    /// How long an agent report is trusted: three missed 3 s check-ins and a margin. Past that the agent
    /// has gone (logged off, killed, crashed) and a frozen name would steer the FPS reading and the mode
    /// by an app that may have closed long ago.
    /// </summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(10);

    /// <summary>MAX_PATH: a process name is a file name, and anything longer is not one.</summary>
    public const int MaxProcessNameLength = 260;

    public const string SourceAgent = "agent";
    public const string SourceLocal = "local";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private string? _reported;
    private long? _reportedAt;

    /// <summary>
    /// A process name as the agent may send it: null (nothing in front — the lock screen, the desktop
    /// with no window) or a bare file name. Not a path, no control characters. The endpoint is
    /// loopback-only like the rest of the API, but a value that ends up in logs and in the FPS target
    /// is still validated rather than trusted.
    /// </summary>
    public static bool IsValidProcessName(string? name) =>
        name is null
        || (name.Trim().Length is > 0 and <= MaxProcessNameLength
            && name.IndexOfAny(['\\', '/', ':']) < 0
            && !name.Any(char.IsControl));

    /// <summary>Record what the user-session agent sees in front. Throws on an invalid name.</summary>
    public void Report(string? process)
    {
        if (!IsValidProcessName(process)) throw new ArgumentException("Not a process name.", nameof(process));
        lock (_gate)
        {
            _reported = process?.Trim();
            _reportedAt = _time.GetTimestamp();
        }
    }

    public string? Current() => Describe().Process;

    /// <summary>The answer and its provenance — what GET /session/foreground shows.</summary>
    public ForegroundReport Describe()
    {
        lock (_gate)
        {
            if (_reportedAt is long at)
            {
                var age = _time.GetElapsedTime(at);
                if (age <= Freshness) return new ForegroundReport(_reported, SourceAgent, (long)age.TotalMilliseconds);
            }
        }
        return new ForegroundReport(local.Current(), SourceLocal, null);
    }
}
