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
//
// F1 audit round 1 (2026-09-25) — the notice must be TRUE, so nothing is recorded as applied that
// nothing can apply:
//  - a rule that only picks a mode records no profile at all. It used to record an empty one, so the
//    seeded steam -> gaming rule toasted "Profile steam applied: mode settings" on every settle;
//  - the fan is skipped while fan control is off (the no-op controller: gates closed, unmatched board);
//  - the cap and the Radeon features are skipped while the GPU agent can never apply them: the gate
//    is closed (a default install, without -EnableGpuProfiles — the agent then never reads
//    /gpu/desired), or the agent reported that ADLX is unavailable. An agent that is merely silent
//    with the gate open (not started yet at logon) still gets the request: desired state converges;
//  - leaving the game no longer restores a cap below an auto-FPS target switched on mid-game, and no
//    longer turns off the user's own Adrenalin cap because the agent was not reporting when the game
//    started (the value to restore is now read from its first report instead).
//
// F1 audit round 2: Anti-Lag and Chill are asked for one by one, and one the agent reports the driver
// does NOT support is skipped with the reason — as the cap already was for FRTC. They used to be
// recorded as applied the moment they were requested, while AdlxSettings.SetEnabled refused them and
// the only trace was "Chill -> NOT applied" on the agent's hidden console. Whether a supported one was
// actually taken is checked when the notice is read (ActiveGameProfileWire), against later reports.
//
// F1 audit round 4: the cap to restore is kept on disk (CapRestoreStore) from Begin to End, and
// replayed at startup (RecoverPendingCap). FRTC is a persisted driver setting, so a restart with a
// capped game in front used to leave the game's cap on the driver for good.
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
    ILogger<GameProfileApplier>? logger = null,
    IGpdFanController? fanController = null,
    Func<bool>? gpuGateOpen = null,
    CapRestoreStore? capStore = null)
{
    public const string FanOffReason = "Fan control is not enabled on this device, so the fan keeps its own mode.";
    public const string GpuGateOffReason = "Radeon control is off: install with -EnableGpuProfiles to let GPD Forge set it.";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<bool> _gpuGateOpen = gpuGateOpen
        ?? (() => Environment.GetEnvironmentVariable(GpuProfileService.GateVariable) == "1");

    // What Begin changed, so End can undo exactly that. Only the focus loop's thread touches these.
    private AppRule? _rule;
    private long? _capVersion;
    private int? _capRestore;
    private bool _capRestoreUnknown;
    private bool _capPersisted;
    private DateTimeOffset _begunAt;
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
        _rule = rule;
        var o = rule.Overrides;
        if (o is null || o.IsEmpty) return false;   // the mode did everything this rule asks for

        var skipped = new List<SkippedOverride>();
        var now = _time.GetUtcNow();
        _begunAt = now;

        int? stapm = o.StapmW;
        if (stapm is int w) intent.SetGame(mode, TdpIntent.GameProfile(w, mode));

        string? fanMode = null;
        if (o.FanMode is string f)
        {
            if (fanController is { Available: false }) skipped.Add(new SkippedOverride("fanMode", FanOffReason));
            else { fanOverride.Apply(fan, f); fanMode = f; }
        }

        var gpuOff = GpuRefusal(now);
        int? capApplied = null;
        if (o.FrameCapFps is int fps)
        {
            if ((gpuOff ?? CapRefusal(fps, now)) is string why) skipped.Add(new SkippedOverride("frameCapFps", why));
            else
            {
                (_capRestore, _capRestoreUnknown) = PreviousCap(now);
                gpu.RequestFrameCap(fps == RuleOverridesPolicy.FrameCapOff ? null : fps, now);
                _capVersion = gpu.CapVersion;
                PersistCapRestore();
                capApplied = fps;
            }
        }

        bool? antiLag = null, chill = null;
        if (o.Gpu is { IsEmpty: false } g)
        {
            if (gpuOff is not null) skipped.Add(new SkippedOverride("gpu", gpuOff));
            else
            {
                var settings = UsableSettings(now);
                antiLag = Supported(g.AntiLag, settings?.AntiLag, "antiLag", "Anti-Lag", skipped);
                chill = Supported(g.Chill, settings?.Chill, "chill", "Chill", skipped);
                if (antiLag is not null || chill is not null)
                {
                    gpu.RequestFeatures(antiLag, chill);
                    _features = (antiLag, chill);
                }
            }
        }

        active.Set(new ActiveGameProfile(
            game, rule.Id, rule.Match, mode,
            new AppliedOverrides(stapm, capApplied, fanMode, antiLag, chill),
            skipped, o.Freeze ?? [], now) { CapVersion = _capVersion });

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
    /// Once per focus-loop tick. While the cap to restore is unknown (the agent was not reporting when
    /// the game started), the agent's first report since then is read for it: the agent posts what the
    /// driver holds BEFORE it reconciles the cap in the same tick, so that report is the user's own.
    /// </summary>
    public void Observe()
    {
        // Someone asked for a cap since the game's (the user, a mode): the cap is theirs now, End will
        // not restore over it, and neither must a restart.
        if (_capPersisted && _capVersion is long v && gpu.CapVersion != v)
        {
            capStore?.Clear();
            _capPersisted = false;
        }

        if (!_capRestoreUnknown) return;
        var (report, usable, _) = agent.Current(_time.GetUtcNow());
        if (!usable || report is null || report.AtUtc < _begunAt) return;
        _capRestore = DriverCap(report);
        _capRestoreUnknown = false;
        if (_capPersisted) PersistCapRestore();
    }

    /// <summary>
    /// At startup, before anything else asks for a cap: puts back the cap a game profile owed when the
    /// daemon last stopped (see the header). A request made since the start wins, and a restore that
    /// was never read is withdrawn — which at startup means nothing is asked, and the driver keeps the
    /// user's own. Replayed once: the record is removed either way.
    /// </summary>
    public void RecoverPendingCap()
    {
        if (capStore?.Read() is not PendingCapRestore pending) return;
        capStore.Clear();
        if (_rule is not null || gpu.Requested || pending.Unknown) return;

        logger?.LogInformation("Game profile '{Match}' was in force when the daemon stopped; putting back the cap from before the game ({Cap}).",
            pending.Match, pending.Cap is int c ? $"{c} FPS" : "off");
        _capRestore = pending.Cap;
        _capRestoreUnknown = false;
        RestoreCap(_time.GetUtcNow(), pending.Match);
        _capRestore = null;
    }

    /// <summary>
    /// A clean stop (FocusProfileWorker.StopAsync): <see cref="End"/>, but the record stays. The daemon
    /// is going away, so the agent may never read the restore End just asked for; the next start
    /// replays it, and the same cap twice is harmless.
    /// </summary>
    public void Shutdown(string modeAfter) => End(modeAfter, keepCapRecord: true);

    /// <summary>What the TDP write that carried this profile's watts did. A yield or a guardian hold
    /// is kept on the record, so the notice does not claim watts that were never written.</summary>
    public void RecordTdpOutcome(ApplyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (active.Current is not { Applied.StapmW: not null } p) return;
        var hold = report.Outcome is ApplyOutcome.SkippedConflict or ApplyOutcome.HeldByGuardian
            ? new TdpHold(report.Outcome, report.Rivals)
            : null;
        active.Set(p with { TdpHold = hold });
    }

    /// <summary>
    /// Takes the profile off. Returns true when its TDP layer was in force for
    /// <paramref name="modeAfter"/> — the mode that will be active once the caller is done — so the
    /// caller must write that mode's TDP again. False when the mode has moved away (its own apply
    /// already wrote, and the layer, keyed to the old mode, was never in its way).
    /// </summary>
    public bool End(string modeAfter) => End(modeAfter, keepCapRecord: false);

    private bool End(string modeAfter, bool keepCapRecord)
    {
        if (_rule is null) return false;
        var now = _time.GetUtcNow();

        bool tdpWasInForce = intent.Game(modeAfter) is not null;
        intent.ClearGame();

        fanOverride.Restore(fan);

        bool capStillOurs = _capVersion is long v && gpu.CapVersion == v;
        if (capStillOurs) RestoreCap(now, _rule.Match);
        // Kept on a clean stop only while the cap is still the game's: one the user picked mid-game
        // is theirs, and the next start must not replay the pre-game cap over it.
        if (_capPersisted && !(keepCapRecord && capStillOurs)) capStore?.Clear();
        if (_features is var (antiLag, chill) && gpu.AntiLag == antiLag && gpu.Chill == chill)
            gpu.RequestFeatures(null, null);

        if (active.Current is not null)
            logger?.LogInformation("Game profile '{Match}' removed; back to the mode's settings.", _rule.Match);
        _rule = null;
        _capVersion = null;
        _capRestore = null;
        _capRestoreUnknown = false;
        _capPersisted = false;
        _features = null;
        active.Clear();
        return tdpWasInForce;
    }

    private void PersistCapRestore()
    {
        if (capStore is null) return;
        capStore.Write(new PendingCapRestore(_capRestore, _capRestoreUnknown, _rule?.Match));
        _capPersisted = true;
    }

    private void RestoreCap(DateTimeOffset now, string? match)
    {
        if (_capRestoreUnknown)
        {
            // The agent never reported during the game, so it never applied the game's cap either (it
            // reports before it reconciles). Forcing "off" here is what used to erase the user's own
            // Adrenalin cap; withdrawing the request leaves the driver exactly as the user had it.
            gpu.WithdrawFrameCap();
            logger?.LogWarning("Game profile '{Match}': the cap before the game was never read (the GPU agent was silent); the request is withdrawn and the driver keeps its own.", match);
            return;
        }

        // The same check every other path makes (POST /gpu/frame-cap, /mode, /auto-fps): auto-FPS may
        // have been switched on mid-game, against the GAME's cap, and restoring a lower one under its
        // target is the pairing that runs the machine hot. Off can never conflict.
        if (FrameRateGovernance.Conflict(autoFps.Enabled, autoFps.TargetFps, _capRestore) is string clash)
        {
            logger?.LogInformation("Game profile '{Match}': the cap before the game ({Cap} FPS) is not restored, the cap is turned off instead: {Why}",
                match, _capRestore, clash);
            gpu.RequestFrameCap(null, now);
            return;
        }
        gpu.RequestFrameCap(_capRestore, now);
    }

    /// <summary>Why no GPU override can take effect at all, or null: the gate is closed, or the agent
    /// reported that ADLX is unavailable. A silent agent is not a refusal (see the header).</summary>
    private string? GpuRefusal(DateTimeOffset now)
    {
        if (!_gpuGateOpen()) return GpuGateOffReason;
        var (report, _, _) = agent.Current(now);
        return report is { Available: false } && agent.IsFresh(now)
            ? $"The GPU agent reports Radeon control unavailable: {report.Detail}"
            : null;
    }

    /// <summary>The driver's settings as a fresh, usable agent report states them; null when there is
    /// none (a silent agent refuses nothing: desired state converges once it starts).</summary>
    private GpuSettingsSnapshot? UsableSettings(DateTimeOffset now)
    {
        var (report, usable, _) = agent.Current(now);
        return usable ? report?.Settings : null;
    }

    /// <summary><paramref name="asked"/>, unless the agent reports the driver does not offer the
    /// feature at all — then null, and the refusal is recorded. An unqueried feature is not refused.</summary>
    private static bool? Supported(bool? asked, GpuFeatureState? state, string field, string name, List<SkippedOverride> skipped)
    {
        if (asked is null || state is not { Supported: false }) return asked;
        skipped.Add(new SkippedOverride(field, $"The driver does not offer {name} on this GPU."));
        return null;
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

    /// <summary>The cap to go back to: what the driver reported before the game (the user's own
    /// Adrenalin setting), unless a request is newer than that reading — then the request, which the
    /// agent is about to carry out. Unknown when neither exists yet — Observe then reads it from the
    /// agent's first report.
    ///
    /// F1 audit round 3 (2026-09-25): this used to take any request over the driver. But the agent
    /// applies a request only when its value changes, so a cap the user set in Adrenalin after it is
    /// never corrected back; the device held 45 under a day-old request for 60, and leaving a game
    /// wrote the 60 over the user's 45 (GpuDesiredState.SupersededBy).</summary>
    private (int? Cap, bool Unknown) PreviousCap(DateTimeOffset now)
    {
        var (report, usable, _) = agent.Current(now);
        bool driverIsNewer = usable && report is not null
            && (!gpu.Requested || GpuDesiredState.SupersededBy(gpu.RequestedAtUtc, report.AtUtc));
        if (driverIsNewer) return (DriverCap(report!), false);
        // A request the agent has not reported back on yet, or a silent agent: the request stands.
        return gpu.Requested ? (gpu.FrameCapFps, false) : (null, true);
    }

    private static int? DriverCap(GpuAgentReport report) =>
        report.Settings?.FrameRateTargetControl is { Supported: true, Enabled: true, Value: int v } ? v : null;
}
