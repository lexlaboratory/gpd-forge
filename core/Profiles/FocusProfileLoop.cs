// GPD Forge - one auto-profile tick, separated from the worker's timer so it can be tested. GPL-3.0-or-later.
//
// Audit round 2 (2026-09-25): since ModeStore, the daemon starts in the mode the user left it in. The
// engine used to be built up front with that mode as its "active" one, so a foreground no rule names
// (or no foreground at all, until the session agent reports) resolved to windows on AC, differed from
// the restored `gaming`, and three ticks later — ~4.5 s after every restart — the worker switched to
// windows, wrote 15/20/17 W and saved `windows` over the user's pick. Nothing had changed; the engine
// was comparing its first target with a mode it never chose.
//
// So when the mode was restored, the engine is built on the FIRST sampled tick, seeded with what that
// tick resolves to. The restored mode holds until the resolution actually changes — a ruled app comes
// to the front, the AC state flips — which is the only thing auto-profiles are meant to react to.
// Without a restored mode the engine starts at the active mode, exactly as before.
//
// Audit round 3 (2026-09-25): since F0 the session agent reports the real foreground on every install,
// and a window that sits ON TOP of the game became the foreground the engine judged. The shipped
// overlay is an Edge --app window: opening it over a ruled game resolved `msedge` to windows, and three
// ticks later the loop switched, wrote the windows preset and ended the manual override the user had
// just set with the overlay's own stepper; closing it switched back. So a known non-game window (the
// same list FrameTarget keeps out of the FPS reading) stands in for the last game-like foreground while
// that app is still running. Once it has exited, the shell in front is just the desktop, and counts.
//
// The switch is automatic, so it goes through SwitchAutomatically: a restart does not restore it as the
// user's pick (see ModeState).
using System.Diagnostics;
using GpdForge.Api;
using GpdForge.Telemetry;
using Microsoft.Extensions.Logging;

namespace GpdForge.Profiles;

public sealed class FocusProfileLoop(
    IForegroundApp foreground,
    ITelemetrySource telemetry,
    ModeState mode,
    ProfileApplier applier,
    IAppRuleStore rules,
    ILogger? logger = null,
    Func<string, bool>? isRunning = null)
{
    private readonly Func<string, bool> _isRunning = isRunning ?? IsProcessRunning;
    private FocusProfileEngine? _engine;
    private string? _lastGameLike;

    /// <summary>Samples once and switches the mode if the engine says so. Returns the new mode, or null
    /// when nothing switched (including a tick skipped for lack of a sampled AC state).</summary>
    public async Task<string?> TickAsync(CancellationToken ct)
    {
        // Only AcConnected is needed here, and it used to cost a full hardware read every 1.5 s. The
        // sampler's last reading answers it for free. Before the first sample the AC state is unknown,
        // and acting on the unmeasured default ("on battery") could switch to a battery rule on a
        // plugged-in machine — so the tick is skipped instead. The same holds for a sample whose battery
        // query failed (AcUnknown, 2026-09-24).
        var reading = telemetry.Latest;
        if (!reading.IsSampled || reading.Snapshot.AcUnknown) return null;

        bool ac = reading.Snapshot.AcConnected;
        var proc = Effective(foreground.Current());
        if (_engine is null)
        {
            _engine = new FocusProfileEngine(mode.Active, rules);
            if (mode.Restored) _engine.Adopt(_engine.Resolve(proc, ac));
        }

        // Recorded every tick, not only on a switch: the UI's "this rule is deciding right now" readout
        // must stay true while the mode is steady, which is most of the time. It records the app that
        // decided — the game under the overlay, not the overlay.
        rules.RecordMatch(proc, _engine.Resolve(proc, ac), ac);
        var switched = _engine.Tick(proc, ac);
        if (switched is null) return null;

        mode.SwitchAutomatically(switched);
        logger?.LogInformation("Auto-profile -> {Mode} (foreground={Proc})", switched, proc ?? "(none)");
        await applier.ApplyAsync(switched, ct);   // apply the mode's TDP (yields if a rival is running)
        return switched;
    }

    /// <summary>The foreground the engine should judge: <paramref name="current"/>, unless it is a known
    /// non-game window over a game-like app that is still running, in which case that app. Null (the
    /// agent has not reported) passes through and does not forget the last game-like app.</summary>
    private string? Effective(string? current)
    {
        if (current is null) return null;
        if (!FrameTarget.IsNonGame(current))
        {
            _lastGameLike = current;
            return current;
        }
        if (_lastGameLike is not null && _isRunning(_lastGameLike)) return _lastGameLike;
        _lastGameLike = null;   // gone: the non-game window is simply what the user is using now
        return current;
    }

    // Only reached while a non-game window is in front, so the process enumeration is not paid on
    // ordinary ticks. The daemon runs as LocalSystem and sees the user session's processes.
    private static bool IsProcessRunning(string name)
    {
        try
        {
            // The agent's report is a process name, but nothing stops a client posting "game.exe".
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            var procs = Process.GetProcessesByName(name);
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        }
        catch (InvalidOperationException) { return false; }
    }
}
