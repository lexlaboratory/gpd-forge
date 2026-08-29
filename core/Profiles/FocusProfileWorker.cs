// GPD Forge - auto-profile background worker. GPL-3.0-or-later.
// OPT-IN (GPDFORGE_AUTO_PROFILES=1). Switches the ACTIVE MODE based on the foreground app.
// It only updates the mode label/state; applying a mode's TDP stays behind the hardware gate.
using GpdForge.Api;
using GpdForge.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GpdForge.Profiles;

public sealed class FocusProfileWorker(
    IForegroundApp foreground,
    ITelemetryService telemetry,
    ModeState mode,
    ProfileApplier applier,
    IAppRuleStore rules,
    ILogger<FocusProfileWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var engine = new FocusProfileEngine(mode.Active, rules);
        logger.LogInformation("Auto-profiles ON (foreground-driven mode switching).");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var t = await telemetry.ReadAsync(ct);
                var proc = foreground.Current();
                // Recorded every tick, not only on a switch: the UI's "this rule is deciding right
                // now" readout must stay true while the mode is steady, which is most of the time.
                rules.RecordMatch(proc, engine.Resolve(proc, t.AcConnected), t.AcConnected);
                var switched = engine.Tick(proc, t.AcConnected);
                if (switched is not null)
                {
                    mode.Active = switched;
                    logger.LogInformation("Auto-profile -> {Mode} (foreground={Proc})", switched, proc ?? "(none)");
                    await applier.ApplyAsync(switched, ct);   // apply the mode's TDP (yields if a rival is running)
                }
            }
            catch (Exception ex) { logger.LogDebug(ex, "focus tick"); }

            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
        }
    }
}
