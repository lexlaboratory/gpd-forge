// GPD Forge — a game's Anti-Lag / Chill over its mode's Radeon profile. GPL-3.0-or-later.
//
// GpuModeProfiles hangs the Radeon profile off the MODE, on purpose (see its header). F1's per-game
// `gpu { antiLag, chill }` does not undo that: it is a delta the agent layers over the mode's profile
// while the game is in front, carried in GpuDesiredState like the frame cap. The merge lives here,
// pure, because the agent itself runs ADLX in the user's session and cannot be unit-tested.
//
// AMD's driver refuses Chill together with Anti-Lag or Boost (GpuProfile.Conflict). A game that asks
// for one therefore turns the others OFF rather than producing a profile the agent would skip as
// conflicting — "Chill for this game" in `gaming` (Anti-Lag on) means Chill instead of Anti-Lag.
namespace GpdForge.Gpu;

public static class GpuFeatureOverride
{
    /// <summary>The profile the agent should apply: the mode's, with the game's features over it.
    /// Returns <paramref name="mode"/> unchanged when the game has no opinion, including null (the
    /// mode leaves the GPU alone).</summary>
    public static GpuProfile? Merge(GpuProfile? mode, bool? antiLag, bool? chill)
    {
        if (antiLag is null && chill is null) return mode;

        var start = mode ?? new GpuProfile("Default");
        bool al = antiLag ?? start.AntiLag;
        bool ch = chill ?? start.Chill;
        bool boost = start.Boost;
        if (chill == true) { al = false; boost = false; }
        else if (antiLag == true) ch = false;
        else if (ch) boost = false;   // mode's Chill kept: the pair it excludes stays excluded

        return new GpuProfile(start.Name + " + game", AntiLag: al, Chill: ch, Boost: boost);
    }

    /// <summary>What the agent compares tick to tick: it writes to the GPU only when this changes, so
    /// a game coming to the front (or leaving) re-applies, and a steady state never does.</summary>
    public static string Key(string mode, bool? antiLag, bool? chill) =>
        $"{mode}|{Tri(antiLag)}|{Tri(chill)}";

    /// <summary>
    /// The key to reconcile towards this tick, or null to leave the GPU alone for it: no mode, or the
    /// desired state could not be read. F1 audit round 1 (2026-09-25): an unreadable /gpu/desired used
    /// to mean "no game opinion", so one non-2xx wrote the mode's Anti-Lag over the game's Chill and the
    /// next good read wrote it back — two ADLX writes and a visible flip for a hiccup. Only a desired
    /// state that WAS read, with no features in it, means the mode alone decides.
    /// </summary>
    public static string? ReconcileKey(string? mode, bool desiredRead, bool? antiLag, bool? chill) =>
        mode is null || !desiredRead ? null : Key(mode, antiLag, chill);

    private static string Tri(bool? b) => b is bool v ? (v ? "on" : "off") : "mode";
}
