// GPD Forge - auto-profile background worker. GPL-3.0-or-later.
// On unless GPDFORGE_AUTO_PROFILES=0. Switches the ACTIVE MODE based on the foreground app, applying the
// mode's TDP through ProfileApplier. The per-tick decision lives in FocusProfileLoop (testable without
// the 1.5 s timer); this class is only the timer and the error net. Since F1 it also hands the loop the
// GameProfileApplier, which layers a rule's per-game overrides while its game is in front.
using GpdForge.Api;
using GpdForge.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GpdForge.Profiles;

public sealed class FocusProfileWorker(
    IForegroundApp foreground,
    ITelemetrySource telemetry,
    ModeState mode,
    ProfileApplier applier,
    IAppRuleStore rules,
    ILogger<FocusProfileWorker> logger,
    GameProfileApplier? games = null,
    TdpIntent? intent = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var loop = new FocusProfileLoop(foreground, telemetry, mode, applier, rules, logger, games: games, intent: intent);
        logger.LogInformation("Auto-profiles ON (foreground-driven mode switching).");

        while (!ct.IsCancellationRequested)
        {
            try { await loop.TickAsync(ct); }
            catch (Exception ex) { logger.LogDebug(ex, "focus tick"); }

            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
        }
    }

    /// <summary>
    /// A clean stop ends the game profile (F1 audit round 4): nothing did, so the game's cap, fan and
    /// watts stayed layered until the process died. After the loop has stopped — the applier is only
    /// ever touched from the loop's thread — and keeping the cap record, since the agent may not read
    /// the restore before the daemon is gone (GameProfileApplier.Shutdown).
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { games?.Shutdown(mode.Active); }
        catch (Exception ex) { logger.LogDebug(ex, "game profile shutdown"); }
    }
}
