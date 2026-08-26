// GPD Forge - per-mode TDP presets. GPL-3.0-or-later.
// Starting points for the Ryzen AI 9 HX 370; tune per device. Watts / °C.
using GpdForge.Tdp;

namespace GpdForge.Profiles;

public static class ModeProfiles
{
    public static readonly IReadOnlyDictionary<string, TdpProfile> Map = new Dictionary<string, TdpProfile>
    {
        ["battery"] = new(StapmW: 8,  FastW: 12, SlowW: 10, TctlC: 90),
        ["windows"] = new(StapmW: 15, FastW: 20, SlowW: 17, TctlC: 92),
        ["gaming"]  = new(StapmW: 25, FastW: 33, SlowW: 28, TctlC: 95),
        ["ai"]      = new(StapmW: 25, FastW: 25, SlowW: 25, TctlC: 90),  // sustained: fast ≈ slow ≈ stapm
        ["standby"] = new(StapmW: 15, FastW: 20, SlowW: 17, TctlC: 92),
    };

    public static TdpProfile? For(string mode) => Map.TryGetValue(mode, out var p) ? p : null;
}
