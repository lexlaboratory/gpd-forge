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
    ILogger<FocusProfileWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var engine = new FocusProfileEngine(mode.Active);
        logger.LogInformation("Auto-profiles ON (foreground-driven mode switching).");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var t = await telemetry.ReadAsync(ct);
                var proc = foreground.Current();
                var switched = engine.Tick(proc, t.AcConnected);
                if (switched is not null)
                {
                    mode.Active = switched;
                    logger.LogInformation("Auto-profile -> {Mode} (foreground={Proc})", switched, proc ?? "(none)");
                }
            }
            catch (Exception ex) { logger.LogDebug(ex, "focus tick"); }

            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
        }
    }
}
