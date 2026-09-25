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
    ILogger? logger = null)
{
    private FocusProfileEngine? _engine;

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
        var proc = foreground.Current();
        if (_engine is null)
        {
            _engine = new FocusProfileEngine(mode.Active, rules);
            if (mode.Restored) _engine.Adopt(_engine.Resolve(proc, ac));
        }

        // Recorded every tick, not only on a switch: the UI's "this rule is deciding right now" readout
        // must stay true while the mode is steady, which is most of the time.
        rules.RecordMatch(proc, _engine.Resolve(proc, ac), ac);
        var switched = _engine.Tick(proc, ac);
        if (switched is null) return null;

        mode.Active = switched;
        logger?.LogInformation("Auto-profile -> {Mode} (foreground={Proc})", switched, proc ?? "(none)");
        await applier.ApplyAsync(switched, ct);   // apply the mode's TDP (yields if a rival is running)
        return switched;
    }
}
