// GPD Forge — follows the active mode with the processor power policy. GPL-3.0-or-later.
//
// A poll of ModeState rather than a hook in every place that switches modes (POST /mode, app rules,
// the power-source flip, the advisor): each of those would have to remember to call it, and the one
// that forgot would leave Windows on the previous mode's policy with nothing saying so. Polling the
// single source of truth cannot miss a switch; a few seconds of lag on a boost policy costs nothing.
//
// Writes only on a CHANGE of mode (and once at start). A readback that disagrees is logged and audited
// by the service, not retried every tick: something else owns the scheme then, and fighting it on a
// timer is the two-controllers clash this project already refuses to have over TDP.
using GpdForge.Api;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GpdForge.Power;

public sealed class PowerPolicyWorker(
    PowerPolicyService service,
    ModeState mode,
    ILogger<PowerPolicyWorker>? logger = null) : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);
    private string? _applied;

    /// <summary>One poll: apply when the mode moved since the last apply. Public for the tests.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var active = mode.Active;
        if (string.Equals(active, _applied, StringComparison.Ordinal)) return;
        _applied = active;
        await service.ApplyAsync(active, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await TickAsync(stoppingToken); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger?.LogWarning(e, "Processor power policy tick failed.");
                }
                await Task.Delay(Poll, stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
