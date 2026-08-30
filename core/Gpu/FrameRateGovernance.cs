// GPD Forge — the rule for when two things both govern frame rate. GPL-3.0-or-later.
//
// Two features here decide how many frames appear, and they work in opposite directions:
//
//   auto-FPS  steers TDP to REACH a target. Frames too low, it spends more watts.
//   FRTC      is the driver refusing to EXCEED a cap. Frames too high, it holds them back.
//
// Most combinations are fine and some are genuinely useful — a 60 FPS cap with a 45 FPS target means
// "aim for 45, never spike past 60", which is a sensible thing to want on a handheld.
//
// One combination is pathological, and it is the reason this file exists. When the cap sits BELOW the
// target, auto-FPS sees a frame rate it can never reach and responds the only way it knows: by
// raising power. It will climb to the ceiling and stay there, burning watts and heat to chase frames
// the driver is deliberately withholding. Nothing errors. The machine just runs hot and loud for no
// benefit, and the cause is two settings that each look reasonable alone.
//
// So that pairing is refused, with an explanation naming both numbers. Refused rather than silently
// adjusted: quietly moving someone's target or cap to make them agree applies a setting they did not
// choose, and they would have no way of knowing which of the two we changed.
namespace GpdForge.Gpu;

public static class FrameRateGovernance
{
    /// <summary>
    /// Null when the combination is allowed; otherwise why it is not.
    ///
    /// Only ACTIVE settings conflict. A disabled auto-FPS with a stale target of 120 must not block a
    /// 30 FPS cap — the target is not governing anything, and refusing on it would be enforcing a
    /// number that has no effect.
    /// </summary>
    /// <param name="autoFpsEnabled">Whether auto-TDP-to-FPS is currently steering power.</param>
    /// <param name="autoFpsTarget">Its target, in FPS. A double because that is what the controller
    /// carries; truncating to int here would move the threshold by up to a frame.</param>
    /// <param name="frameCap">The driver cap, or null when there is none.</param>
    public static string? Conflict(bool autoFpsEnabled, double autoFpsTarget, int? frameCap)
    {
        if (!autoFpsEnabled || frameCap is not int cap) return null;
        if (cap >= autoFpsTarget) return null;

        return $"A {cap} FPS cap sits below the {autoFpsTarget:0.#} FPS auto-FPS target. Auto-FPS would keep "
             + "raising power to reach a frame rate the driver is holding back, so the machine would run "
             + "hot for no extra frames. Raise the cap, lower the target, or turn one of them off.";
    }
}
