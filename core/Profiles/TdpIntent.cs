// GPD Forge — which TDP profile the user wants in force: the mode's preset, or a manual override.
// GPL-3.0-or-later.
//
// Until 2026-09-24 a POST /tdp was a one-off write the daemon forgot at once. The next thing to touch
// TDP put the MODE PRESET back: the thermal guardian's throttle-clear "restore", the resume restore,
// and the throttle ceiling itself, which was computed under the preset and so could RAISE a manual
// value that was already lower. A user who set 12 W, then hit a hot spell, came out of it at
// 15/20/17 W with nothing on screen saying why.
//
// The override lives until the mode changes — ProfileApplier clears it on every mode apply,
// including a re-selection of the same mode, because picking a mode is asking for its preset. It is
// also keyed to the mode it was set in, so a mode switch that bypassed ProfileApplier still cannot
// carry it into another mode. In memory only: after a restart the daemon applies the mode preset
// (ForgeWorker's startup apply), which is what a restart has always meant for every other setting
// that is not persisted.
using GpdForge.Tdp;

namespace GpdForge.Profiles;

public sealed class TdpIntent
{
    /// <summary>The manual band: the same 5–40 W STAPM range <see cref="ModeProfiles.Set"/> clamps
    /// presets to, so a hand-set value can never be one a preset could not be.</summary>
    public const int ManualMinW = 5;
    public const int ManualMaxW = 40;

    /// <summary>The Tctl a manual write used unconditionally before 2026-09-24; now only the fallback
    /// for a mode with no preset.</summary>
    public const int FallbackTctlC = 90;

    private readonly Lock _gate = new();
    private (string Mode, TdpProfile Profile)? _manual;

    public static bool IsManualInRange(int stapmW) => stapmW is >= ManualMinW and <= ManualMaxW;

    /// <summary>
    /// A manual write: flat (STAPM = fast = slow — the user asked for a number of watts, not for a
    /// boost budget) at the ACTIVE MODE's thermal limit. It used Tctl 90 whatever the mode, so in
    /// `windows` (92) or `gaming` (95) asking for fewer watts also quietly lowered the thermal limit.
    /// </summary>
    public static TdpProfile ManualProfile(int stapmW, string mode)
    {
        int tctl = ModeProfiles.For(mode)?.TctlC ?? FallbackTctlC;
        return new TdpProfile(stapmW, stapmW, stapmW, tctl);
    }

    /// <summary>Remember a manual profile as the override for <paramref name="mode"/>.</summary>
    public void SetManual(string mode, TdpProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        lock (_gate) _manual = (mode, profile);
    }

    public void ClearManual()
    {
        lock (_gate) _manual = null;
    }

    /// <summary>The override, if one was set in <paramref name="mode"/> (compared ignoring case, as
    /// the preset table is); otherwise null.</summary>
    public TdpProfile? Manual(string mode)
    {
        lock (_gate)
            return _manual is var (m, p) && string.Equals(m, mode, StringComparison.OrdinalIgnoreCase) ? p : null;
    }

    /// <summary>What should be in force for <paramref name="mode"/> when no transient owner (the
    /// guardian, the charge guard, the tuner, auto-FPS) is holding TDP: the manual override if there
    /// is one, else the mode's preset. Null for an unknown mode with no override.</summary>
    public TdpProfile? Resolve(string mode) => Manual(mode) ?? ModeProfiles.For(mode);
}
