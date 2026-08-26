// GPD Forge - per-mode TDP presets. GPL-3.0-or-later.
// Starting points for the Ryzen AI 9 HX 370; tune per device. Watts / °C.
using GpdForge.Tdp;

namespace GpdForge.Profiles;

public static class ModeProfiles
{
    // Mutable so the UI can tune presets live (like MotionAssistant's per-profile TDP).
    public static readonly Dictionary<string, TdpProfile> Map = new()
    {
        ["battery"] = new(StapmW: 8,  FastW: 12, SlowW: 10, TctlC: 90),
        ["windows"] = new(StapmW: 15, FastW: 20, SlowW: 17, TctlC: 92),
        ["gaming"]  = new(StapmW: 25, FastW: 33, SlowW: 28, TctlC: 95),
        ["ai"]      = new(StapmW: 25, FastW: 25, SlowW: 25, TctlC: 90),  // sustained: fast ≈ slow ≈ stapm
        ["standby"] = new(StapmW: 15, FastW: 20, SlowW: 17, TctlC: 92),
    };

    public static TdpProfile? For(string mode) => Map.TryGetValue(mode, out var p) ? p : null;

    /// <summary>Update a mode's TDP preset (clamped to sane bounds). Returns the stored value.</summary>
    public static TdpProfile Set(string mode, TdpProfile p)
    {
        int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
        var safe = new TdpProfile(Clamp(p.StapmW, 5, 40), Clamp(p.FastW, 5, 45), Clamp(p.SlowW, 5, 45), Clamp(p.TctlC, 60, 95));
        Map[mode] = safe;
        return safe;
    }
}
