// GPD Forge - per-mode TDP presets. GPL-3.0-or-later.
// Starting points for the Ryzen AI 9 HX 370; tune per device. Watts / °C.
using GpdForge.Ai;
using GpdForge.Tdp;

namespace GpdForge.Profiles;

public static class ModeProfiles
{
    /// <summary>
    /// The mode whose profile is a <b>sustained ceiling</b>, not a burst budget: boost above the
    /// sustained STAPM buys no throughput once a workload is continuously CPU-bound, it only adds
    /// heat, fan noise and thermal cycling. <see cref="ProfileShaper"/> exists to collapse that
    /// headroom, and this is where it enters the path — every caller that resolves a profile
    /// (ProfileApplier, ForgeWorker, the standby restore, the resume worker, GET /ai) goes through
    /// <see cref="For"/>, so shaping here covers all of them instead of one call site.
    ///
    /// The default preset below is already flat, but nothing was <i>keeping</i> it flat: a single
    /// POST to /profiles/ai reintroduced boost, because Set clamps ranges without flattening.
    /// </summary>
    public const string SustainedMode = "ai";

    // Mutable so the UI can tune presets live (like MotionAssistant's per-profile TDP).
    public static readonly Dictionary<string, TdpProfile> Map = new()
    {
        ["battery"] = new(StapmW: 8,  FastW: 12, SlowW: 10, TctlC: 90),
        ["windows"] = new(StapmW: 15, FastW: 20, SlowW: 17, TctlC: 92),
        ["gaming"]  = new(StapmW: 25, FastW: 33, SlowW: 28, TctlC: 95),
        ["ai"]      = new(StapmW: 25, FastW: 25, SlowW: 25, TctlC: 90),  // sustained: fast ≈ slow ≈ stapm
        ["standby"] = new(StapmW: 15, FastW: 20, SlowW: 17, TctlC: 92),
    };

    public static TdpProfile? For(string mode) =>
        Map.TryGetValue(mode, out var p) ? (mode == SustainedMode ? Shape(p) : p) : null;

    /// <summary>
    /// Update a mode's TDP preset (clamped to sane bounds). Returns the stored value.
    ///
    /// The sustained mode is flattened on the way IN as well as on the way out, so what
    /// <c>GET /profiles</c> reports is what will actually be applied. Storing a boost that
    /// <see cref="For"/> would quietly discard would put a number on screen that never reaches the
    /// silicon — the failure this codebase keeps removing.
    /// </summary>
    public static TdpProfile Set(string mode, TdpProfile p)
    {
        int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
        var safe = new TdpProfile(Clamp(p.StapmW, 5, 40), Clamp(p.FastW, 5, 45), Clamp(p.SlowW, 5, 45), Clamp(p.TctlC, 60, 95));
        if (mode == SustainedMode) safe = Shape(safe);
        Map[mode] = safe;
        return safe;
    }

    private static TdpProfile Shape(TdpProfile p) => ProfileShaper.Shape(p.StapmW, p.TctlC);
}
