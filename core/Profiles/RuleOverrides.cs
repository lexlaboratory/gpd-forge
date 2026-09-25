// GPD Forge — per-game overrides carried by an app rule. GPL-3.0-or-later.
//
// Until F1 (2026-09-25) a rule could only pick a MODE, so "Elden Ring at 22 W with a 60 FPS cap and
// the fan on Aggressive" meant editing the gaming preset for every game at once. The session of
// 2026-09-24 measured why that matters on this handheld: it holds ~22 W at ~88 °C with the fan
// maxed, and uncapped 130–144 FPS on the 60 Hz panel gave a 1 % low of 2–17 against ~30 capped.
// Those are per-game numbers, so they live on the rule that names the game.
//
// Every field is optional and null means "the mode decides" — a rule with no overrides behaves
// exactly as before F1. The values are applied through the owners that already exist (TdpIntent,
// GpuDesiredState, FanState) by GameProfileApplier; nothing here touches hardware.
using System.Text.Json.Serialization;

namespace GpdForge.Profiles;

/// <summary>Radeon features a game wants regardless of its mode's GPU profile. Null = the mode's.</summary>
public sealed record GpuOverrides(bool? AntiLag = null, bool? Chill = null)
{
    [JsonIgnore] public bool IsEmpty => AntiLag is null && Chill is null;
}

/// <param name="StapmW">Sustained limit for this game, applied flat at the mode's Tctl like a manual
/// value. Within the 5–40 W band every preset is clamped to.</param>
/// <param name="FrameCapFps">Driver frame cap: null = whatever the mode left, 0 = cap off.</param>
/// <param name="FanMode">Auto / Quiet / Balanced / Aggressive, or null for the user's own setting.</param>
/// <param name="Freeze">Process names to suspend while the game runs. STORED ONLY until F5 acts on
/// it — carried now so a profile saved today does not have to be re-entered then.</param>
public sealed record RuleOverrides(
    int? StapmW = null,
    int? FrameCapFps = null,
    string? FanMode = null,
    GpuOverrides? Gpu = null,
    IReadOnlyList<string>? Freeze = null)
{
    [JsonIgnore] public bool IsEmpty =>
        StapmW is null && FrameCapFps is null && FanMode is null && (Gpu is null || Gpu.IsEmpty) && (Freeze is null || Freeze.Count == 0);

    // Value equality for the list too. The focus loop compares the rule it applied with the one the
    // store holds now, to notice an edit made mid-game; by reference every reload would look like one.
    public bool Equals(RuleOverrides? other) =>
        other is not null
        && StapmW == other.StapmW && FrameCapFps == other.FrameCapFps && FanMode == other.FanMode
        && Equals(Gpu, other.Gpu)
        && (Freeze ?? []).SequenceEqual(other.Freeze ?? [], StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(StapmW); h.Add(FrameCapFps); h.Add(FanMode); h.Add(Gpu);
        foreach (var f in Freeze ?? []) h.Add(f, StringComparer.Ordinal);
        return h.ToHashCode();
    }
}

/// <summary>Why an override was refused: a stable code for clients, and a sentence for the user.</summary>
public sealed record OverrideError(string Code, string Message);

/// <summary>A rule the store refused. <see cref="Code"/> is on the wire next to the message.</summary>
public sealed class AppRuleRejectedException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}
