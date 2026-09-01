// GPD Forge — the one place that knows what a mode IS. GPL-3.0-or-later.
//
// Before this file, five modes were enumerated in seven places: ModeProfiles.Map (TDP presets),
// AppRulePolicy.Modes (which ones a per-app rule may select), GpuModeProfiles.Defaults (Radeon
// settings), ui/src/pages/shared.tsx (labels and blurbs), ui/src/types.ts (the ModeId union), the
// mock daemon's MODES and PROFILES, and a literal `mode.Active == "gaming"` in ForgeWorker deciding
// whether auto-FPS may engage.
//
// None of those lists knew about the others. Adding a mode meant finding all seven, and the failure
// mode of missing one is quiet: a mode that has a TDP preset but no GPU profile applies stale Radeon
// settings; one missing from AppRulePolicy cannot be selected by a rule and says nothing about why.
//
// So this is the catalogue, and the C# side reads from it. A consistency test
// (ModeCatalogueTests) fails if a mode exists here and not in the places that must mirror it,
// which is what stops the list scattering again the next time it grows.
using GpdForge.Tdp;

namespace GpdForge.Profiles;

/// <param name="Id">Wire identifier. Appears in URLs (<c>POST /profiles/{mode}</c>) and in stored
/// rules, so it is lowercase and stable — renaming one breaks saved user rules.</param>
/// <param name="Label">What a human sees.</param>
/// <param name="Blurb">One line explaining what the mode is for.</param>
/// <param name="SelectableByAppRule">Whether a per-app rule may switch to it. False for system
/// states: a rule that put the machine into standby mode on focus would be a trap.</param>
/// <param name="Sustained">Flat power delivery — fast ≈ slow ≈ stapm, no boost headroom.</param>
/// <param name="AutoFpsEligible">Whether the auto-FPS governor may engage automatically in this
/// mode. See the remarks on <see cref="ModeCatalogue"/>.</param>
/// <param name="RecommendedFrameCapFps">The driver-level frame cap this mode asks for, or null to
/// leave the user's cap alone.</param>
public sealed record ModeDefinition(
    string Id,
    string Label,
    string Blurb,
    bool SelectableByAppRule,
    bool Sustained,
    bool AutoFpsEligible,
    int? RecommendedFrameCapFps,
    TdpProfile DefaultTdp);

/// <remarks>
/// On <c>AutoFpsEligible</c> and frame caps, because the interaction is the subtle part:
///
/// Auto-FPS raises power to REACH a frame rate. FRTC makes the driver refuse to EXCEED one. Most
/// pairings are fine, and one is pathological — a cap below an active target makes the governor
/// climb forever chasing frames the driver is withholding, so the machine runs hot and loud for
/// nothing while no error appears anywhere.
///
/// `gaming` therefore has a target and no cap; `gaming-battery` has a cap and is NOT auto-FPS
/// eligible. Those are two coherent strategies rather than two settings that happen to coexist.
/// </remarks>
public static class ModeCatalogue
{
    public const string Gaming = "gaming";
    public const string GamingBattery = "gaming-battery";
    public const string Ai = "ai";
    public const string Windows = "windows";
    public const string Battery = "battery";
    public const string Standby = "standby";

    public static readonly IReadOnlyList<ModeDefinition> All =
    [
        new(Gaming, "Gaming", "Auto-TDP to a target FPS, reactive fan, full boost headroom.",
            SelectableByAppRule: true, Sustained: false, AutoFpsEligible: true,
            RecommendedFrameCapFps: null,
            new TdpProfile(StapmW: 25, FastW: 33, SlowW: 28, TctlC: 95)),

        // Measured design, not a guess. On the reference device the pack holds 40 Wh and the system
        // draws ~9 W before the SoC does anything, so 15 W sustained lands near 24 W total — about
        // 1.6 h against roughly 1.1 h for `gaming`.
        //
        // Tctl is 90 rather than 95 deliberately: a lower ceiling means the fan spins less, and the
        // fan is part of that ~9 W of overhead. Fast/slow keep headroom (unlike `ai`, which is
        // flattened on purpose) because a throttled shader-compile spike costs a visible hitch and
        // saves nothing across a session.
        //
        // The 45 fps cap is the real lever, not the TDP. An uncapped game converts every watt it is
        // allowed into frames nobody sees; this panel reports 60 Hz with no other supported mode, so
        // the cap stops the work at the source and the SoC clocks down on its own.
        new(GamingBattery, "Gaming (battery)", "Frame-capped and cooler, for the longest session away from a charger.",
            SelectableByAppRule: true, Sustained: false, AutoFpsEligible: false,
            RecommendedFrameCapFps: 45,
            new TdpProfile(StapmW: 15, FastW: 20, SlowW: 17, TctlC: 90)),

        new(Ai, "Agents / AI", "Sustained CPU, VRAM/UMA, anti-standby, local API.",
            SelectableByAppRule: true, Sustained: true, AutoFpsEligible: false,
            RecommendedFrameCapFps: null,
            new TdpProfile(StapmW: 25, FastW: 25, SlowW: 25, TctlC: 90)),

        new(Windows, "Windows", "Balanced power, quiet fan, hotkeys.",
            SelectableByAppRule: true, Sustained: false, AutoFpsEligible: false,
            RecommendedFrameCapFps: null,
            new TdpProfile(StapmW: 15, FastW: 20, SlowW: 17, TctlC: 92)),

        new(Battery, "Battery", "Low TDP floor, longest runtime.",
            SelectableByAppRule: true, Sustained: false, AutoFpsEligible: false,
            RecommendedFrameCapFps: null,
            new TdpProfile(StapmW: 8, FastW: 12, SlowW: 10, TctlC: 90)),

        // A system state, not a usage mode: it exists so the resume restore has something to apply.
        // Not selectable by an app rule — see ModeDefinition.SelectableByAppRule.
        new(Standby, "Standby Doctor", "Restore TDP+fan+HID on resume, fix drain.",
            SelectableByAppRule: false, Sustained: false, AutoFpsEligible: false,
            RecommendedFrameCapFps: null,
            new TdpProfile(StapmW: 15, FastW: 20, SlowW: 17, TctlC: 92)),
    ];

    public static ModeDefinition? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool Exists(string? id) => Find(id) is not null;

    /// <summary>Modes a per-app rule may select. Excludes system states.</summary>
    public static IReadOnlyList<string> SelectableIds =>
        All.Where(m => m.SelectableByAppRule).Select(m => m.Id).ToArray();

    public static IReadOnlyList<string> Ids => All.Select(m => m.Id).ToArray();

    /// <summary>
    /// Whether the auto-FPS governor may engage on its own in this mode.
    ///
    /// Replaces `mode.Active == "gaming"` in ForgeWorker. That literal meant adding any second
    /// gaming-shaped mode silently either inherited the governor or did not, depending on spelling,
    /// with nothing to state the intent.
    /// </summary>
    public static bool AutoFpsEligible(string? id) => Find(id)?.AutoFpsEligible ?? false;

    /// <summary>The frame cap a mode asks the driver for, or null to leave the user's cap alone.</summary>
    public static int? RecommendedFrameCap(string? id) => Find(id)?.RecommendedFrameCapFps;
}
