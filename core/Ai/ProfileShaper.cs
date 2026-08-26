// GPD Forge — sustained power shaping for AI/inference workloads. GPL-3.0-or-later.
//
// A gaming-style profile lets short "fast"/"slow" boost windows run above the sustained STAPM
// ceiling (great for a bursty frame, bad for a steady multi-minute inference job: the boost above
// STAPM buys no extra throughput once the workload is continuously CPU-bound, it only adds heat,
// fan noise and thermal cycling). ProfileShaper collapses fast == slow == stapm into one flat
// ceiling, so a long AI job runs at a constant, predictable, quiet power draw instead of
// ratcheting up and down. Pure function, no I/O — reuses GpdForge.Tdp.TdpProfile so its output
// composes directly with ITdpController / ClosedLoopTdpController.
using GpdForge.Tdp;

namespace GpdForge.Ai;

public static class ProfileShaper
{
    /// <summary>Safe TDP band for this device (mirrors ModeProfiles.Set's clamp for STAPM/Fast/Slow).</summary>
    public const int MinW = 5;
    public const int MaxW = 40;

    /// <summary>Safe Tctl band (mirrors ModeProfiles.Set's clamp for TctlC).</summary>
    public const int MinTctlC = 60;
    public const int MaxTctlC = 95;

    /// <summary>
    /// Build a flat sustained profile at <paramref name="targetW"/>: STAPM = FastW = SlowW, with no
    /// boost headroom above the sustained target. Both inputs are clamped to the device's safe band
    /// rather than throwing, so a caller-supplied value (e.g. from a saved preset) can never produce
    /// an out-of-range request.
    /// </summary>
    public static TdpProfile Shape(int targetW, int tctlC)
    {
        int w = Math.Clamp(targetW, MinW, MaxW);
        int t = Math.Clamp(tctlC, MinTctlC, MaxTctlC);
        return new TdpProfile(w, w, w, t);
    }
}
