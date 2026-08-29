// GPD Forge - bringing the controller back after a resume. GPL-3.0-or-later.
//
// The last step of the resume restore that had no backend. What it must NOT do is restart the pad
// every time the machine wakes: a working controller yanked mid-game is a worse bug than the one
// this fixes. So it acts only on a node Windows itself reports as faulted, and when the controller
// came back on its own it says so and touches nothing.
//
// The restart goes through pnputil rather than SetupAPI/CfgMgr32 P/Invoke: it is a supported,
// in-box command, it keeps this layer unit-testable behind IProcessRunner, and a resume path is the
// last place to be hand-rolling native device calls. It needs elevation, which the daemon has.
using GpdForge.Tdp;
using Microsoft.Extensions.Logging;

namespace GpdForge.Hid;

/// <summary>
/// <paramref name="Acted"/> says whether anything was restarted; <paramref name="Healthy"/> whether
/// the controller is usable now. They are independent on purpose — "did nothing because nothing was
/// wrong" and "restarted it and it still will not start" are opposite outcomes.
/// </summary>
public sealed record HidRestoreResult(bool Acted, bool Healthy, string Detail);

public sealed class HidReenumerator(
    IHidDeviceEnumerator devices,
    IProcessRunner runner,
    ILogger<HidReenumerator>? logger = null)
{
    /// <summary>Confirmed against the reference Win 4 on 2026-08-29 (7 nodes carried this pair).</summary>
    public const string ControllerId = "VID_2F24&PID_0135";

    public async Task<HidRestoreResult> RestoreAsync(CancellationToken ct)
    {
        var nodes = devices.Find(ControllerId);

        if (nodes.Count == 0)
        {
            return new(false, false,
                $"No controller device node ({ControllerId}) is present. A pad Windows cannot see at " +
                "all does not come back from a restart — this needs a replug or a driver, not a resume fix.");
        }

        var faulted = nodes.Where(n => !n.Healthy).ToList();
        if (faulted.Count == 0)
        {
            return new(false, true,
                $"The controller came back on its own — {nodes.Count} device node(s), none reporting a " +
                "fault. Nothing was restarted.");
        }

        // One restart of the composite parent re-enumerates every interface beneath it. Only when the
        // parent itself is healthy (so the fault is confined to one interface) are the leaves touched.
        var parent = nodes.FirstOrDefault(n => n.IsCompositeParent && !n.Healthy);
        var targets = parent is not null ? [parent] : faulted;

        var restarted = new List<string>();
        foreach (var t in targets)
        {
            logger?.LogWarning(
                "Resume: controller node {Id} reports fault code {Code}; restarting it.",
                t.InstanceId, t.ConfigManagerErrorCode);
            try
            {
                await runner.RunAsync("pnputil", $"/restart-device \"{t.InstanceId}\"", ct);
                restarted.Add(t.InstanceId);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new(true, false, $"Restarting {t.InstanceId} failed: {ex.Message}");
            }
        }

        // Verified against the device, not assumed from a clean exit code: pnputil returns success
        // for a restart that leaves the node in exactly the same faulted state.
        var after = devices.Find(ControllerId);
        var stillFaulted = after.Where(n => !n.Healthy).ToList();

        if (stillFaulted.Count == 0)
        {
            return new(true, true,
                $"Restarted {restarted.Count} device node(s); the controller now reports no fault.");
        }

        var codes = string.Join(", ", stillFaulted.Select(n => n.ConfigManagerErrorCode).Distinct());
        return new(true, false,
            $"Restarted {restarted.Count} device node(s), but {stillFaulted.Count} still report a " +
            $"fault (code {codes}).");
    }
}
