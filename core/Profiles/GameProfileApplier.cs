// GPD Forge — layers a rule's per-game overrides over its mode, and takes them off again. GPL-3.0-or-later.
//
// Called by FocusProfileLoop once a ruled game has settled in front (and the mode is the rule's), and
// again when it leaves. It owns NO hardware path of its own — each override goes through the owner
// that already governs that setting, so every existing guard keeps working:
//
//   stapmW      → TdpIntent's game layer. The loop then writes it through ProfileApplier (rival yield,
//                 guardian hold, one write gate), and the guardian's ceiling, the throttle-clear
//                 restore, the resume restore and the 30 s reassert all resolve through the intent.
//   frameCapFps → GpuDesiredState, checked first against the driver's reported range and the auto-FPS
//                 target (FrameRateGovernance) exactly as POST /gpu/frame-cap checks it.
//   fanMode     → FanState, through FanOverride: never saved to fan.json, and put back on exit.
//   gpu         → GpuDesiredState's Anti-Lag / Chill, merged over the mode's profile by the agent.
//   freeze      → recorded only; F5 acts on it.
//
// Restores are conditional: each one undoes ONLY what is still this profile's. A cap the user set
// mid-game, a fan they changed, stay theirs — the game leaving is not a reason to undo them.
using GpdForge.Api;
using GpdForge.Fan;
using GpdForge.Gpu;
using Microsoft.Extensions.Logging;

namespace GpdForge.Profiles;

public sealed class GameProfileApplier(
    TdpIntent intent,
    FanState fan,
    FanOverride fanOverride,
    GpuDesiredState gpu,
    GpuAgentState agent,
    AutoFpsState autoFps,
    ActiveGameProfileState active,
    TimeProvider? time = null,
    ILogger<GameProfileApplier>? logger = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // What Begin changed, so End can undo exactly that. Only the focus loop's thread touches these.
    private AppRule? _rule;
    private long? _capVersion;
    private int? _capRestore;
    private (bool? AntiLag, bool? Chill)? _features;

    /// <summary>The rule whose profile is layered now, or null.</summary>
    public AppRule? Rule => _rule;

    /// <summary>
    /// Layers <paramref name="rule"/>'s overrides for <paramref name="game"/> in <paramref name="mode"/>.
    /// Returns true when it set a TDP layer: the caller must then write the mode's TDP (ProfileApplier
    /// resolves the layer), which is how the watts reach the silicon through the one write gate.
    /// </summary>
    public bool Begin(AppRule rule, string game, string mode)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        var o = rule.Overrides;
        var skipped = new List<SkippedOverride>();
        var now = _time.GetUtcNow();

        int? stapm = o?.StapmW;
        if (stapm is int w) intent.SetGame(mode, TdpIntent.GameProfile(w, mode));

        string? fanMode = o?.FanMode;
        if (fanMode is not null) fanOverride.Apply(fan, fanMode);

        int? capApplied = null;
        if (o?.FrameCapFps is int fps)
        {
            if (CapRefusal(fps, now) is string why) skipped.Add(new SkippedOverride("frameCapFps", why));
            else
            {
                _capRestore = PreviousCap(now);
                gpu.RequestFrameCap(fps == RuleOverridesPolicy.FrameCapOff ? null : fps, now);
                _capVersion = gpu.CapVersion;
                capApplied = fps;
            }
        }

        if (o?.Gpu is { IsEmpty: false } g)
        {
            gpu.RequestFeatures(g.AntiLag, g.Chill);
            _features = (g.AntiLag, g.Chill);
        }

        _rule = rule;
        active.Set(new ActiveGameProfile(
            game, rule.Id, rule.Match, mode,
            new AppliedOverrides(stapm, capApplied, fanMode, o?.Gpu?.AntiLag, o?.Gpu?.Chill),
            skipped, o?.Freeze ?? [], now));

        // ASCII separators: the service console writes in the OEM code page, where a middle dot became
        // byte 0xFA and turned the log into "binary" for grep (measured on the daemon, 2026-09-25).
        logger?.LogInformation("Game profile '{Match}' applied for {Game} in {Mode}: {Stapm}, {Cap}, fan {Fan}{Skipped}",
            rule.Match, game, mode,
            stapm is int s ? $"{s} W" : "mode TDP",
            capApplied switch { null => "mode cap", 0 => "cap off", int c => $"{c} FPS" },
            fanMode ?? "unchanged",
            skipped.Count == 0 ? "" : " (skipped: " + string.Join("; ", skipped.Select(x => $"{x.Field}: {x.Reason}")) + ")");

        return stapm is not null;
    }

    /// <summary>
    /// Takes the profile off. Returns true when its TDP layer was in force for
    /// <paramref name="modeAfter"/> — the mode that will be active once the caller is done — so the
    /// caller must write that mode's TDP again. False when the mode has moved away (its own apply
    /// already wrote, and the layer, keyed to the old mode, was never in its way).
    /// </summary>
    public bool End(string modeAfter)
    {
        if (_rule is null) return false;
        var now = _time.GetUtcNow();

        bool tdpWasInForce = intent.Game(modeAfter) is not null;
        intent.ClearGame();

        fanOverride.Restore(fan);

        if (_capVersion is long v && gpu.CapVersion == v) gpu.RequestFrameCap(_capRestore, now);
        if (_features is var (antiLag, chill) && gpu.AntiLag == antiLag && gpu.Chill == chill)
            gpu.RequestFeatures(null, null);

        logger?.LogInformation("Game profile '{Match}' removed; back to the mode's settings.", _rule.Match);
        _rule = null;
        _capVersion = null;
        _capRestore = null;
        _features = null;
        active.Clear();
        return tdpWasInForce;
    }

    /// <summary>Why this cap must not be requested, or null. The same checks POST /gpu/frame-cap
    /// makes — except that a silent agent does not refuse it: this is desired state, and an agent
    /// that starts later converges on it.</summary>
    private string? CapRefusal(int fps, DateTimeOffset now)
    {
        int? cap = fps == RuleOverridesPolicy.FrameCapOff ? null : fps;
        var (report, usable, _) = agent.Current(now);
        var frtc = usable ? report?.Settings?.FrameRateTargetControl : null;
        if (frtc is { Supported: false }) return "This GPU does not support a driver frame-rate cap.";
        if (GpuDesiredState.Reject(cap, frtc?.Min, frtc?.Max) is string range) return range;
        return FrameRateGovernance.Conflict(autoFps.Enabled, autoFps.TargetFps, cap);
    }

    /// <summary>The cap to go back to: the last one requested, else what the driver reported before the
    /// game (the user's own Adrenalin setting), else off. Never "stop reconciling" — the agent would
    /// then leave the game's cap in the driver for good.</summary>
    private int? PreviousCap(DateTimeOffset now)
    {
        if (gpu.Requested) return gpu.FrameCapFps;
        var (report, usable, _) = agent.Current(now);
        return usable && report?.Settings?.FrameRateTargetControl is { Supported: true, Enabled: true, Value: int v } ? v : null;
    }
}
