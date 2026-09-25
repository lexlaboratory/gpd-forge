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
// F0 audit round 1 (2026-09-25): that stand-in held ANY unlisted app and replaced EVERY listed one, so
// notepad left open hid Steam / Big Picture in front of it and the shipped steam -> gaming rule never
// fired (nor a user's rule on a browser or Discord). Now only a ruled app is held, and only a listed
// window with no rule of its own stands in for it.
//
// The switch is automatic, so it goes through SwitchAutomatically: a restart does not restore it as the
// user's pick (see ModeState).
//
// F1 (2026-09-25): a rule can carry per-game overrides (RuleOverrides), layered by GameProfileApplier.
// The mode engine alone cannot drive them: Steam and Elden Ring are both `gaming`, so moving from one
// to the other switches no mode, yet it has to put Elden Ring's 22 W on. So the RULE settles too, with
// the same hysteresis — a two-second alt-tab must not restore the fan and rewrite TDP — and its profile
// is in force while that settled rule's app is in front AND the mode is the rule's. A mode the user
// picked by hand over the game (the engine never overrides one) therefore ends the profile, without a
// TDP write: the user's own apply already wrote their mode.
//
// Order within a tick is what keeps it to ONE write: the old profile comes off and the new one goes on
// (TdpIntent's game layer) before the mode is applied, so ProfileApplier writes the game's watts
// directly instead of the preset followed by the game a moment later.
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
    Func<string, bool>? isRunning = null,
    GameProfileApplier? games = null,
    TdpIntent? intent = null)
{
    private readonly Func<string, bool> _isRunning = isRunning ?? IsProcessRunning;
    private FocusProfileEngine? _engine;
    private string? _lastGameLike;

    // The rule's own hysteresis (see the header): a candidate rule id and how many ticks it has held.
    private Guid? _ruleCandidate;
    private int _ruleCandidateTicks;
    private Guid? _settledRule;
    private string? _settledGame;

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
        bool gameTdpChanged = games is not null && LayerGameProfile(games, proc, switched ?? mode.Active);

        if (switched is null)
        {
            // Same mode, different game (or none): only the game layer moved, and only it is written —
            // WITHOUT ending a manual override (F1 audit round 1): the mode did not change, and TdpIntent's
            // rule is that the override lives until it does. While one is set nothing is written at all:
            // Resolve would answer the manual value, which is already what the device holds.
            if (gameTdpChanged && intent?.Manual(mode.Active) is null)
                games!.RecordTdpOutcome(await applier.ApplyWithReportAsync(mode.Active, clearManual: false, ct));
            return null;
        }

        mode.SwitchAutomatically(switched);
        logger?.LogInformation("Auto-profile -> {Mode} (foreground={Proc})", switched, proc ?? "(none)");
        // The mode's TDP, or its game layer (yields if a rival is running). A mode change ends the override.
        var report = await applier.ApplyWithReportAsync(switched, clearManual: true, ct);
        games?.RecordTdpOutcome(report);
        return switched;
    }

    /// <summary>Settles the rule in front and swaps the game profile when the settled one changes.
    /// True when the swap moved TDP in <paramref name="modeAfter"/> and the caller must write it.</summary>
    private bool LayerGameProfile(GameProfileApplier games, string? proc, string modeAfter)
    {
        games.Observe();
        var inFront = rules.RuleFor(proc);
        if (inFront?.Id == _ruleCandidate) _ruleCandidateTicks++;
        else { _ruleCandidate = inFront?.Id; _ruleCandidateTicks = 1; }
        if (_ruleCandidateTicks >= FocusProfileEngine.DefaultStabilityTicks && _settledRule != _ruleCandidate)
        {
            _settledRule = _ruleCandidate;
            _settledGame = proc;
        }

        // Looked up by id every tick, not kept: an edit, a disable or a delete of the rule mid-game has
        // to reach the profile in force, and the store is the one place that knows.
        var want = _settledRule is Guid id ? rules.List().FirstOrDefault(r => r.Id == id && r.Enabled) : null;
        if (want is not null && !string.Equals(want.Mode, modeAfter, StringComparison.OrdinalIgnoreCase)) want = null;
        if (Equals(games.Rule, want)) return false;

        bool tdp = games.Rule is not null && games.End(modeAfter);
        if (want is not null) tdp |= games.Begin(want, _settledGame ?? want.Match, modeAfter);
        return tdp;
    }

    /// <summary>The foreground the engine should judge: <paramref name="current"/>, unless it is a known
    /// non-game window no rule names, over a ruled app that is still running — then that app. Null (the
    /// agent has not reported) passes through and does not forget the held app.</summary>
    private string? Effective(string? current)
    {
        if (current is null) return null;
        // A rule on the foreground decides, listed non-game or not: the shipped steam -> gaming rule
        // (Steam and Big Picture are on the list), or a user's rule on a browser or Discord.
        bool ruled = rules.ModeFor(current) is not null;
        if (!FrameTarget.IsNonGame(current))
        {
            // Only a ruled app is worth holding. An unruled one resolves to the power default exactly as
            // the non-game window over it would — and holding it (notepad, say) is what hid a ruled
            // launcher in front of it, and named the wrong app in the Profiles readout.
            _lastGameLike = ruled ? current : null;
            return current;
        }
        if (ruled) return current;
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
