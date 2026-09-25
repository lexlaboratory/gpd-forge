// GPD Forge — Forge Advisor: the rules (plan F3). GPL-3.0-or-later.
//
// Pure: a snapshot of what the game is doing in, the changes worth proposing out. Nothing here writes
// anything; a suggestion reaches the game's profile only when the user presses Apply (AdvisorService).
//
// The thresholds come from the session of 2026-09-24 on this handheld: uncapped, a game ran at
// 130–144 FPS on the 60 Hz panel with a 1 % low of 2–17, and capped at 60 the 1 % low rose to ~30; the
// thermal guardian settles at ~22 W at ~88 °C with the fan maxed. Frames the panel cannot show still
// cost watts and heat, and a limit that swings between boost and throttle is what ruins the lows.
using GpdForge.Profiles;
using GpdForge.Sessions;
using GpdForge.Telemetry;

namespace GpdForge.Advisor;

/// <param name="Game">The game's process name as the session recorder / frame source reports it.</param>
/// <param name="Live">Frame pacing over the last 10 s, when this game is the one presenting now.</param>
/// <param name="LastSession">The game's most recent recorded session: the evidence when it is not live.</param>
/// <param name="RefreshHz">The panel's current refresh rate; null when it could not be read.</param>
/// <param name="FrameCapFps">The frame cap in force now (null or 0 = none). Only judged with live data —
/// a recorded session carries the cap it ran with.</param>
/// <param name="GuardianThrottling">The thermal guardian is holding the limit down right now.</param>
/// <param name="LearnedCeilingW">Where the guardian settles for this game (ThermalCeilingLearner).</param>
/// <param name="StapmW">The sustained limit in force now; null when unknown.</param>
/// <param name="Profile">The game's stored overrides, so advice already taken is not repeated.</param>
/// <param name="OnBattery">Running on battery now (plan F6): the charger was unplugged.</param>
/// <param name="Mode">The active mode now; null when unknown.</param>
/// <param name="ModeHistory">How the game ran in gaming and gaming-battery (SessionMath.CompareModes).</param>
public sealed record AdvisorInput(
    string Game,
    FramePacingMetrics? Live,
    GameSession? LastSession,
    int? RefreshHz,
    int? FrameCapFps,
    bool GuardianThrottling,
    double? LearnedCeilingW,
    int? StapmW,
    RuleOverrides? Profile,
    bool OnBattery = false,
    string? Mode = null,
    IReadOnlyList<ModeStats>? ModeHistory = null);

/// <summary>One proposed change. <see cref="Id"/> is "kind:game[:value]" — stable while the advice is
/// the same, so a dismissal holds across polls and restarts, and a changed value is new advice.</summary>
/// <param name="StapmW">The sustained limit Apply writes into the profile, or null.</param>
/// <param name="FrameCapFps">The frame cap Apply writes into the profile, or null.</param>
public sealed record AdvisorSuggestion(
    string Id,
    string Game,
    string Kind,
    string Title,
    string Detail,
    int? StapmW,
    int? FrameCapFps)
{
    /// <summary>False for advice there is no profile field for yet (RSR / resolution arrive in F4).</summary>
    public bool Applicable => StapmW is not null || FrameCapFps is not null;
}

public static class AdvisorRules
{
    /// <summary>Live metrics over less than this are a loading screen or a menu, not the game.</summary>
    public const double MinLiveSpanSeconds = 5;

    /// <summary>FPS must beat the refresh by this factor before frames are called wasted: a game a
    /// couple of frames over 60 is jitter around the refresh, not a missing cap.</summary>
    public const double OverRefreshFactor = 1.1;

    /// <summary>1 % low under this share of the average is the stutter the 2026-09-24 session felt.</summary>
    public const double PoorLowShare = 0.5;

    /// <summary>A game whose 1 % low still beats the refresh by this factor has watts to spare.</summary>
    public const double LightGameLowFactor = 1.5;

    /// <summary>Fewer-watts steps a quarter down at a time: the advisor looks again next session, and a
    /// small step cannot turn a light game into a throttled one.</summary>
    public const double FewerWattsStep = 0.75;

    /// <summary>The cap for a throttled game: 30 divides 60 Hz evenly, so every frame is on screen for
    /// exactly two refreshes and the pacing stays even at the watts the guardian allows.</summary>
    public const int ThrottledCapFps = 30;

    /// <summary>The average gaming-battery must have held before it is suggested on battery: the same
    /// 30 that paces evenly on the 60 Hz panel. Below it the saved watts cost the game.</summary>
    public const double AcceptableBatteryFps = 30;

    public static IReadOnlyList<AdvisorSuggestion> Advise(AdvisorInput input)
    {
        var game = AppRulePolicy.Normalize(input.Game);
        if (game.Length == 0) return [];
        var hz = input.RefreshHz is > 0 ? input.RefreshHz : null;
        var profileCap = input.Profile?.FrameCapFps;

        // Evidence: the game as it runs now, else how it ran last time. A recorded session judges the
        // cap it ran with, not whatever cap is in force for some other app now.
        var live = input.Live is { SpanSeconds: >= MinLiveSpanSeconds, FpsAvg: > 0 } m ? m : null;
        double? avg = live?.FpsAvg ?? input.LastSession?.FpsAvg;
        double? low = live is not null ? live.Fps1PctLow : input.LastSession?.Fps1PctLow;
        var cap = live is not null ? input.FrameCapFps : input.LastSession?.FrameCapFps;
        var hasEvidence = avg is > 0;
        bool Uncapped(int? c) => c is null or <= 0 || (hz is int r && c > r);

        var result = new List<AdvisorSuggestion>();

        // (1) Frames the panel never shows.
        var capRefresh = hasEvidence && hz is int refresh && avg > refresh * OverRefreshFactor && Uncapped(cap)
            && !(profileCap is > 0 && profileCap <= refresh);
        if (capRefresh)
        {
            result.Add(new($"cap_refresh:{game}:{hz}", game, "cap_refresh",
                $"Cap at {hz} FPS",
                $"Running at {avg:0} FPS on a {hz} Hz display: the extra frames are never shown but still cost watts and heat. Capping at the refresh rate steadies the frame pacing.",
                null, hz));
        }

        // (2) Throttled and stuttering. Only from live data: the guardian's state is about now. When (1)
        // applies it goes first — capping at the refresh is the smaller step, and on this device it
        // alone lifted the 1 % low from 2–17 to ~30.
        if (!capRefresh && live is not null && input.GuardianThrottling && live.Fps1PctLow < live.FpsAvg * PoorLowShare)
        {
            if (Uncapped(cap) || cap > ThrottledCapFps)
            {
                if (profileCap != ThrottledCapFps)
                    result.Add(new($"cap_30:{game}:{ThrottledCapFps}", game, "cap_30",
                        $"Cap at {ThrottledCapFps} FPS",
                        $"The thermal guardian is holding the power down and the 1 % low ({live.Fps1PctLow:0}) is under half the average ({live.FpsAvg:0}). A steady {ThrottledCapFps} FPS paces evenly on a 60 Hz panel and fits the watts that are left.",
                        null, ThrottledCapFps));
            }
            else
            {
                result.Add(new($"lower_resolution:{game}", game, "lower_resolution",
                    "Lower the resolution or enable RSR",
                    $"Throttled and stuttering even at {cap} FPS: the game needs less work per frame. Lower its resolution, or enable Radeon Super Resolution.",
                    null, null));
            }
        }

        // (4) Light game: even its slowest frames beat the refresh comfortably.
        var fewer = hasEvidence && hz is int panel && !input.GuardianThrottling && input.StapmW is int now
            && low >= panel * LightGameLowFactor
            && Clamp(now * FewerWattsStep) is var lower && lower < now
            && !(input.Profile?.StapmW <= lower);
        if (fewer)
        {
            var w = Clamp(input.StapmW!.Value * FewerWattsStep);
            result.Add(new($"fewer_watts:{game}:{w}", game, "fewer_watts",
                $"Try {w} W",
                $"Even the 1 % low ({low:0} FPS) is well above the {hz} Hz refresh at {input.StapmW} W. A lower limit saves battery and fan noise; the advisor looks again next session.",
                w, null));
        }

        // (3) Learned ceiling: the guardian ends up here anyway, so start here and skip the swing
        // between boost and throttle. A light game's lower limit wins — the ceiling is where a heavy
        // game settles, not a target.
        if (!fewer && input.LearnedCeilingW is double ceiling && ceiling > 0)
        {
            var w = Clamp(ceiling);
            if (input.Profile?.StapmW != w)
                result.Add(new($"stapm_ceiling:{game}:{w}", game, "stapm_ceiling",
                    $"Hold {w} W",
                    $"In this game the thermal guardian settles at about {ceiling:0.#} W. Holding the sustained limit there avoids swinging between boost and throttle, which is what spoils the 1 % low.",
                    w, null));
        }

        // (5) Unplugged in gaming, and this game's own history says gaming-battery holds up (plan F6).
        // Advice only: the game's rule decides its mode on AC as well, so writing gaming-battery into it
        // would cost the plugged-in sessions; switching the mode now is one tap in the mode menu.
        if (BatteryModeHolds(input) is ModeStats battery)
        {
            var full = input.ModeHistory!.FirstOrDefault(m => m.Mode == ModeCatalogue.Gaming);
            var cost = battery.WhPerHour is double bw ? $" at {bw:0.#} W" : "";
            var versus = full?.FpsAvg is double ff && full.WhPerHour is double fw && battery.WhPerHour is not null
                ? $", against {ff:0} FPS at {fw:0.#} W in Gaming" : "";
            var lowText = battery.Fps1PctLow is double bl ? $" (1 % low {bl:0})" : "";
            result.Add(new($"battery_mode:{game}", game, "battery_mode",
                "Switch to Gaming (battery)",
                $"On battery now. In Gaming (battery) this game held {battery.FpsAvg:0} FPS{lowText}{cost}{versus}. Switching stretches the charge without dropping below a playable frame rate.",
                null, null));
        }

        return result;
    }

    /// <summary>The game an id names ("kind:game[:value]"), or null for anything else.</summary>
    public static string? GameOf(string? id)
    {
        var parts = id?.Split(':');
        return parts is { Length: 2 or 3 } && parts[1].Length > 0 ? parts[1] : null;
    }

    /// <summary>The game's gaming-battery record when it justifies the switch: on battery, in gaming
    /// (another mode is a choice the user made for another reason), with enough gaming-battery play to
    /// trust, an average at or over <see cref="AcceptableBatteryFps"/> and a 1 % low that is not the
    /// stutter <see cref="PoorLowShare"/> describes.</summary>
    private static ModeStats? BatteryModeHolds(AdvisorInput input)
    {
        if (!input.OnBattery || !string.Equals(input.Mode, ModeCatalogue.Gaming, StringComparison.OrdinalIgnoreCase)) return null;
        var b = input.ModeHistory?.FirstOrDefault(m => m.Mode == ModeCatalogue.GamingBattery);
        if (b is null || b.TotalSeconds < SessionMath.MinBatteryEvidenceSeconds || b.FpsAvg is not double avg || avg < AcceptableBatteryFps)
            return null;
        return b.Fps1PctLow is double low && low < avg * PoorLowShare ? null : b;
    }

    private static int Clamp(double w) =>
        Math.Clamp((int)Math.Round(w, MidpointRounding.AwayFromZero), RuleOverridesPolicy.MinStapmW, RuleOverridesPolicy.MaxStapmW);
}
