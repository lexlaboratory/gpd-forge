// GPD Forge — one auto-FPS tick as a decision: the next STAPM, and whether it needs a write.
// GPL-3.0-or-later.
//
// Until 2026-09-24 ForgeWorker applied the controller's answer every tick whether it had changed or
// not. Inside the deadband NextStapm returns its input unchanged, so a game sitting on its target
// re-wrote the same limit once a second — one ryzenadj apply and one --info readback per tick, for
// nothing. Firmware reverts, the reason given for re-issuing, are now caught by the 30 s readback in
// TdpReasserter instead.
//
// The integrator had the mirror-image problem. AutoFpsState.CurrentStapm started at 25 W whatever the
// mode and nothing ever synced it, so the first governor tick after a mode switch or a manual write
// steered from a number that was not in force. Here it starts from what IS in force (the last TDP
// write, whoever made it) and falls back to the mode's preset only when nothing has been written.
namespace GpdForge.Tdp;

/// <param name="NextStapm">The controller's STAPM for this tick (the new integrator value).</param>
/// <param name="Write">The profile to apply, or null when it is already the one in force.</param>
public readonly record struct AutoFpsDecision(int NextStapm, TdpProfile? Write);

public static class AutoFpsStep
{
    /// <param name="basis">The active mode's profile: auto-FPS steers STAPM only and keeps the mode's
    /// fast/slow/Tctl.</param>
    /// <param name="inForce">The last profile written to the hardware (TdpState.Last), or null when
    /// nothing has been written since the daemon started.</param>
    public static AutoFpsDecision Decide(
        FpsTdpController controller, double targetFps, double measuredFps,
        TdpProfile basis, TdpProfile? inForce, int minW, int maxW)
    {
        ArgumentNullException.ThrowIfNull(controller);

        int integrator = inForce?.StapmW ?? basis.StapmW;
        int next = controller.NextStapm(targetFps, measuredFps, integrator, minW, maxW);
        TdpProfile desired = basis with { StapmW = next };

        return new AutoFpsDecision(next, desired == inForce ? null : desired);
    }
}
