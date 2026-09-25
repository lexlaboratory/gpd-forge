// GPD Forge — GET /profiles/active: the profile in force, as it stands when it is asked. GPL-3.0-or-later.
//
// F1 audit round 1 (2026-09-25). Two things went wrong with answering straight from the record Begin
// made:
//  - it went stale. The overlay's header line is a live status (role=status), not a toast, and it kept
//    saying "22 W · 60 FPS · Aggressive" after the user set 18 W, picked Quiet or changed the cap —
//    beside the very controls that showed otherwise. So each field is checked against its owner here:
//    a manual TDP (TdpIntent outranks the game layer), a fan the user took (FanOverride released), a
//    cap someone requested since (GpuDesiredState.CapVersion moved). Those fields leave `applied` and
//    are named in `superseded`, which the notice renders as "changed by you";
//  - it claimed watts nobody wrote. With another power controller running ProfileApplier yields, and
//    with the guardian throttling below the game's value its ceiling is what holds. Those move stapmW
//    to `skipped` with the reason, for as long as they are true.
//
// F1 audit round 2 (2026-09-25): the Radeon side was reported as applied the moment it was ASKED for.
// The daemon cannot reach ADLX; the user-session agent carries a request out a tick later, and an
// unsupported feature or a failed set was only printed to the agent's own hidden console ("Chill -> NOT
// applied"). So the cap, Anti-Lag and Chill are now checked against the agent's report: once it has
// had time to reconcile (ConfirmAfter), a driver holding something else moves the field to `skipped`
// ("the driver did not take it"), and a silent or stale agent leaves it unconfirmed — skipped, with
// that reason, until the agent reports again — rather than claiming it forever.
//
// A pure function of the record and a snapshot of the owners, so the wire shape is unit-tested against
// tests/contract/api-contract.json with a POPULATED profile: the contract suites only ever saw the
// inactive answer (every field null), which is how a renamed field would have reached the device.
using GpdForge.Gpu;

namespace GpdForge.Profiles;

/// <summary>What the owners hold right now, for <see cref="ActiveGameProfileWire.From"/>.</summary>
/// <param name="ManualStapmW">TdpIntent's manual override for the profile's mode, if any. One equal to the
/// game's own value supersedes nothing: the game's watts are what the device runs at (saving the value
/// just set in the overlay as the profile is exactly that case).</param>
/// <param name="FanHeldByGame">FanOverride still holds the fan for the game.</param>
/// <param name="CapVersion">GpuDesiredState.CapVersion now.</param>
/// <param name="GuardianThrottleW">The guardian's ceiling while it throttles, else null.</param>
/// <param name="RivalsNow">The other power controllers running; asked only when the TDP apply yielded.</param>
/// <param name="Agent">The GPU agent's last report (GpuAgentState.Last), or null when it never reported.</param>
/// <param name="Now">The daemon's clock, for the agent report's freshness.</param>
public sealed record ProfileLiveState(
    int? ManualStapmW,
    bool FanHeldByGame,
    long CapVersion,
    int? GuardianThrottleW,
    Func<IReadOnlyList<string>> RivalsNow,
    GpuAgentReport? Agent,
    DateTimeOffset Now,
    long ImageVersion = 0);

/// <remarks>RSR / RIS (F4) additive: null when the profile did not set them, or no longer holds them.</remarks>
public sealed record GpuFeaturesWire(bool? AntiLag, bool? Chill,
    bool? Rsr = null, int? RsrSharpness = null, bool? Ris = null, int? RisSharpness = null);

public sealed record AppliedWire(int? StapmW, int? FrameCapFps, string? FanMode, GpuFeaturesWire Gpu);

public sealed record SkippedWire(string Field, string Reason);

public sealed record ActiveGameProfileWire(
    bool Active,
    string? Game,
    Guid? RuleId,
    string? Match,
    string? Mode,
    AppliedWire? Applied,
    IReadOnlyList<SkippedWire> Skipped,
    IReadOnlyList<string> Superseded,
    IReadOnlyList<string> Freeze,
    DateTimeOffset? SinceUtc)
{
    public static readonly ActiveGameProfileWire Inactive = new(false, null, null, null, null, null, [], [], [], null);

    /// <summary>
    /// How long after the profile went on an agent report must be before it can contradict it. The
    /// agent posts what the driver holds and THEN reconciles, every 3 s (GpuAgentLoop.Tick), so the
    /// first report after the profile shows the driver from before. Two ticks, and slack for one HTTP
    /// timeout's worth of lag.
    /// </summary>
    public static readonly TimeSpan ConfirmAfter = TimeSpan.FromSeconds(8);

    public const string AgentSilentReason =
        "not confirmed: the GPU agent is not reporting, so what the driver holds is unknown.";

    public static ActiveGameProfileWire From(ActiveGameProfile? p, ProfileLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (p is null) return Inactive;

        var a = p.Applied;
        var skipped = p.Skipped.Select(s => new SkippedWire(s.Field, s.Reason)).ToList();
        var superseded = new List<string>();

        int? stapm = a.StapmW;
        if (stapm is int w)
        {
            if (live.ManualStapmW is int m && m != w) { superseded.Add("stapmW"); stapm = null; }
            else if (Held(p.TdpHold, w, live) is string why) { skipped.Add(new SkippedWire("stapmW", why)); stapm = null; }
        }

        string? fanMode = a.FanMode;
        if (fanMode is not null && !live.FanHeldByGame) { superseded.Add("fanMode"); fanMode = null; }

        int? cap = a.FrameCapFps;
        if (cap is not null && p.CapVersion is long v && live.CapVersion != v) { superseded.Add("frameCapFps"); cap = null; }

        // The agent's side, in the order the notice lists it.
        var check = new DriverCheck(p, live);
        cap = check.Keep("frameCapFps", cap, s => CapMismatch(s?.FrameRateTargetControl, cap), skipped);
        bool? chill = check.Keep("chill", a.Chill, s => FeatureMismatch(s?.Chill, a.Chill, "Chill"), skipped);
        bool? antiLag = check.Keep("antiLag", a.AntiLag, s => FeatureMismatch(s?.AntiLag, a.AntiLag, "Anti-Lag"), skipped);

        // RSR / RIS: the Display page asking for anything since makes them the user's (superseded);
        // otherwise the agent's report must not contradict the enabled flag that was asked for.
        var img = a.Image;
        if (img is not null && p.ImageVersion is long iv && live.ImageVersion != iv)
        {
            if (img.Rsr is not null || img.RsrSharpness is not null) superseded.Add("rsr");
            if (img.Ris is not null || img.RisSharpness is not null) superseded.Add("ris");
            img = null;
        }
        bool? rsr = check.Keep("rsr", img?.Rsr, s => FeatureMismatch(s?.RadeonSuperResolution, img?.Rsr, "RSR"), skipped);
        bool? ris = check.Keep("ris", img?.Ris, s => FeatureMismatch(s?.ImageSharpening, img?.Ris, "Image Sharpening"), skipped);
        // A sharpness rides with its feature: dropped when the feature was refused, kept when only a
        // sharpness was asked (nothing to contradict it but the value, which the driver range bounds).
        int? rsrSharp = img?.Rsr is not null && rsr is null ? null : img?.RsrSharpness;
        int? risSharp = img?.Ris is not null && ris is null ? null : img?.RisSharpness;

        return new ActiveGameProfileWire(
            true, p.Game, p.RuleId, p.Match, p.Mode,
            new AppliedWire(stapm, cap, fanMode, new GpuFeaturesWire(antiLag, chill, rsr, rsrSharp, ris, risSharp)),
            skipped, superseded, p.Freeze, p.SinceUtc);
    }

    /// <summary>Whether the agent's report can speak for the driver now, and whether it is late enough
    /// to contradict what the profile asked for.</summary>
    private readonly struct DriverCheck(ActiveGameProfile p, ProfileLiveState live)
    {
        /// <summary><paramref name="value"/> when the driver holds it (or cannot contradict it yet);
        /// else null, with the reason added to <paramref name="skipped"/>.</summary>
        public T? Keep<T>(string field, T? value, Func<GpuSettingsSnapshot?, string?> mismatch, List<SkippedWire> skipped)
            where T : struct
        {
            if (value is null) return null;
            var agent = live.Agent;
            string? why =
                agent is null || live.Now - agent.AtUtc > GpuAgentState.Freshness ? AgentSilentReason
                : !agent.Available ? $"the GPU agent reports Radeon control unavailable: {agent.Detail}"
                : agent.AtUtc < p.SinceUtc + ConfirmAfter ? null   // not carried out yet; nothing says otherwise
                : mismatch(agent.Settings);
            if (why is null) return value;
            skipped.Add(new SkippedWire(field, why));
            return null;
        }
    }

    /// <summary>Why the driver's FRTC is not the profile's <paramref name="cap"/> (0 = off), or null —
    /// also when it could not be read: an unanswered query is not a contradiction.</summary>
    private static string? CapMismatch(GpuFeatureState? frtc, int? cap)
    {
        if (frtc is null || cap is null) return null;
        if (!frtc.Supported) return "This GPU does not support a driver frame-rate cap.";
        int? holds = frtc.Enabled ? frtc.Value : 0;
        if (holds is null || holds == cap) return null;
        return $"the driver did not take it: it holds {(holds == 0 ? "no cap" : $"{holds} FPS")}.";
    }

    private static string? FeatureMismatch(GpuFeatureState? state, bool? asked, string name)
    {
        if (state is null || asked is null) return null;
        if (!state.Supported) return $"the driver does not offer {name} on this GPU.";
        return state.Enabled == asked ? null
            : $"the driver did not take it: {name} is {(state.Enabled ? "on" : "off")}.";
    }

    /// <summary>Why the game's <paramref name="watts"/> are not what the device runs at now, or null.</summary>
    private static string? Held(TdpHold? hold, int watts, ProfileLiveState live)
    {
        if (hold is { Outcome: ApplyOutcome.SkippedConflict })
        {
            // Asked live: once the rival exits, the 30 s reassert writes the intent (the game's watts).
            var rivals = live.RivalsNow();
            if (rivals.Count > 0)
                return $"another power controller is running ({string.Join(", ", rivals)}), so GPD Forge left TDP to it.";
        }
        return live.GuardianThrottleW is int ceiling && ceiling < watts
            ? $"held at {ceiling} W by the thermal guardian until the device cools down."
            : null;
    }
}
