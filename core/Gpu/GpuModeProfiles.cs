// GPD Forge — which Radeon settings each mode implies, and applying them. GPL-3.0-or-later.
//
// The user asked for a switch for the AMD profiles that ALSO changes automatically per app, task or
// game. The automatic half is not rebuilt here, because it already exists and works: AppRuleStore
// maps a foreground process to a mode, and FocusProfileEngine switches modes with anti-flapping
// hysteresis. So the GPU profile is attached to the MODE rather than to each rule.
//
// That choice is the whole design. Hanging a GPU profile off every rule would mean a second, parallel
// matching system to keep in step with the first, and two places to look when the wrong thing
// applied. Attaching it to the mode means the GPU follows automatically from every path that already
// sets a mode — the focus worker, a manual switch, the AC/battery rule, and the standby restore —
// without any of them knowing this exists.
//
// The defaults below are opinions about a handheld, and each is argued rather than assumed, because a
// default that silently changes how someone's games look or feel is worse than no default at all.
namespace GpdForge.Gpu;

public static class GpuModeProfiles
{
    /// <summary>
    /// The shipped mapping. Deliberately conservative: nothing here enables an image-altering feature
    /// (Boost lowers resolution during motion; Image Sharpening changes how the picture looks). Those
    /// are choices a person makes about their own eyes, not something a power tool turns on for them.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, GpuProfile> Defaults =
        new Dictionary<string, GpuProfile>(StringComparer.OrdinalIgnoreCase)
        {
            // Latency is what a game wants, and Anti-Lag costs no image quality. Chill stays off: it
            // caps frames during slow movement, which is exactly the stutter-adjacent feeling players
            // report as "it feels off" without knowing why.
            ["gaming"] = new GpuProfile("Gaming", AntiLag: true),

            // On battery the goal is frames-per-watt, and Chill is the one AMD feature that genuinely
            // trades frames for power. It excludes Anti-Lag and Boost by driver rule, which is why
            // this profile sets nothing else — the exclusion is honoured, not fought.
            ["battery"] = new GpuProfile("Battery", Chill: true),

            // Same reasoning as `battery`: frames-per-watt is the goal, and Chill is the one AMD
            // feature that genuinely trades one for the other. It excludes Anti-Lag and Boost by
            // driver rule, which is why nothing else is set here — the exclusion is honoured rather
            // than fought. The frame cap that does the heavy lifting for this mode is not a Radeon
            // *profile* setting; it is FRTC, applied through GpuDesiredState when the mode is
            // selected (see ModeCatalogue.RecommendedFrameCapFps).
            ["gaming-battery"] = new GpuProfile("Gaming (battery)", Chill: true),

            // Inference is compute, not presentation. Anti-Lag and Chill act on the frame pipeline and
            // do nothing for it; leaving them on would only add a variable nobody asked for.
            ["ai"] = new GpuProfile("Agents / AI"),

            // The desktop default: hand the GPU back to whatever the user configured in Adrenalin.
            ["windows"] = new GpuProfile("Windows"),
        };

    /// <summary>The profile for a mode, or null when the mode has no GPU opinion. Null means "leave
    /// the GPU alone", which is different from "turn everything off".</summary>
    public static GpuProfile? For(string mode)
        => Defaults.TryGetValue(mode, out var p) ? p : null;

    /// <summary>The modes that carry a GPU profile. For the UI, so it can show what will happen.</summary>
    public static IReadOnlyCollection<string> Modes => (IReadOnlyCollection<string>)Defaults.Keys;
}
