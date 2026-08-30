// GPD Forge — detecting inference WE did not start, so Modern Standby cannot suspend it. GPL-3.0-or-later.
//
// WHY THIS EXISTS. AntiStandbyService is already a correct ref-counted keep-awake holder, but until
// now the only things that ever took a hold were GPD Forge's own job queue and the manual toggle.
// A model run started by hand — `ollama run`, LM Studio's server, a llama-server process, a training
// script in a terminal — got nothing. That was harmless while STANDBYIDLE was "never": the machine
// could not enter Modern Standby, so an unheld run could not be suspended. As of 2026-08-29 the DC
// idle timeout is 300 s (verified with powercfg), so for the first time a long local inference can be
// suspended mid-run. This file is the missing signal.
//
// WHY IT IS DELIBERATELY HARD TO TRIGGER. This feature is the exact inverse of the bug fixed the day
// before, where the machine sat at 14.4 W / 36 %/h all night because something kept it from ever
// really sleeping. A keep-awake heuristic that is too eager recreates that bug precisely, and the
// obvious implementation — "is ollama running?" — IS that bug: `ollama serve` and the LM Studio tray
// process sit resident 24/7 doing nothing, so gating on process PRESENCE would pin the machine awake
// every night forever. Presence was considered and rejected. The gate is sustained CPU WORK:
//   - utilisation is a delta of Process.TotalProcessorTime between two ticks, divided by elapsed wall
//     time and by processor count, i.e. a fraction of the WHOLE machine's CPU capacity;
//   - hysteresis in the shape FocusProfileEngine already uses: N consecutive above-threshold ticks to
//     engage, M consecutive below-threshold ticks to release, with M < N on purpose. Engaging is
//     expensive if wrong (battery) and releasing is cheap if wrong (see the bounded-hold note), so
//     the asymmetry always errs toward letting the machine sleep.
//
// WHY THE HOLD IS BOUNDED. An open-ended hold is a promise made once on evidence that may have gone
// stale. Instead the hold is re-justified every tick and, on top of that, expires after MaxHold; the
// engine then has to re-earn it through the full engage window. The cost of that re-arming gap is
// EngageTicks * TickInterval (15 s by default), which is an order of magnitude below the 300 s DC
// idle timer, so a genuinely busy process can never actually be suspended by the expiry — the idle
// timer needs 300 s of *continuous* idle to fire. The benefit is that a hold can never outlive the
// evidence for it because of a bug in the streak bookkeeping. Note that both the re-justification and
// the ceiling live INSIDE Tick, which is why InferenceHoldWorker.cs is required to keep ticking even
// when sampling fails — see the failure note there.
//
// ABSENT IS NOT THE SAME AS UNMEASURED, and conflating them was a real bug here. A watched process
// can leave the sample for two very different reasons: it exited, or we were refused/unable to read
// it (TotalProcessorTime throwing AccessDenied because the daemon is unelevated and ollama.exe is
// not, or Process.GetProcessesByName throwing and taking a whole watched name with it). Reporting
// the second as "watched process exited" is a lie, and acting on it drops the keep-awake mid-run,
// which is the exact suspension this feature exists to prevent. So the two travel separately all the
// way through:
//   - ProcessCpuSample.TotalProcessorTime is NULL for "seen, but not measurable";
//   - Tick's unreadableNames carries "this whole watched name could not be enumerated";
//   - only a process that was genuinely enumerable and genuinely gone counts as an exit;
//   - everything unmeasured is republished as InferenceHoldStatus.Unmeasured, so the panel can say
//     "we cannot see ollama" instead of the false "no sustained inference work".
//
// HOW THAT IS RECONCILED WITH NEVER HOLDING FOREVER. There is a real tension: an unmeasurable process
// must not be called idle (dishonest, and it drops a live run), yet it must not hold the machine awake
// indefinitely either (an unelevated daemon would then never sleep, which is the 14.4 W bug wearing a
// different hat). The resolution, stated so it can be argued with: unmeasured counts as BELOW
// threshold — the release streak advances and the hold goes away after ReleaseTicks — but it does NOT
// count as an exit, so the release is attributed truthfully and the reason string says we lost sight
// of the process rather than that it stopped. Failing to measure therefore biases toward LETTING THE
// MACHINE SLEEP, always, and the honesty is paid for in the report rather than in battery. The lost
// coverage (a real run we cannot see is not protected) is the correct half to lose: it costs one
// interrupted run, whereas the other half costs every night's battery, silently.
//
// WHY A RECYCLED PID IS A DIFFERENT PROCESS, NOT A DIP. Windows reuses PIDs quickly, and a track is
// per-PID state that decides whether the machine stays awake. Suppressing only the CPU *fraction*
// across a recycle (what this file used to do) left Busy/BusySince/Above/FirstAbove standing, so a
// python process spawned four seconds ago inherited its predecessor's earned hold and was reported as
// "working since 12:00:01". Recycling is now detected two ways and always wipes the WHOLE track, not
// just the baseline: by process start time when the sampler can supply it (exact), and by the CPU
// total going backwards when it cannot (inferred, and only catches a successor that has burned less
// CPU than its predecessor). The predecessor is counted as an exit, because it is one.
//
// WHAT THIS HONESTLY CANNOT SEE. This measures CPU only. Inference that is fully GPU-resident and
// spends its wall time blocked on the GPU can sit below the threshold, and this file will not hold
// for it — it reports CpuFraction and lets the reader see why, rather than inventing a "probably
// busy". A number we did not measure is null here, never a plausible-looking stand-in.
//
// PURITY. The engine below reads no clock and touches no process: it takes an injected timestamp and
// a list of samples. All OS contact lives behind IProcessCpuSampler, so the tests contain zero real
// process access — the same shape as IExecutionStateSink, IProcessRunner and IVramReader.
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GpdForge.Ai;

/// <summary>
/// One observation of one watched process: its identity plus the cumulative CPU time it has consumed
/// since it started. Cumulative rather than a percentage on purpose — a rate is a derived value, and
/// deriving it here would hide which two samples it came from.
/// </summary>
/// <param name="TotalProcessorTime">
/// Null means "this process exists but we could not read its CPU time" (access denied, or it exited
/// between enumeration and the read). Null is NOT idle and NOT exited; see the header.
/// </param>
/// <param name="StartTime">
/// The process's start time when the sampler could read it, used to detect PID reuse exactly. Null
/// means unknown, and the engine falls back to inferring reuse from the CPU total going backwards.
/// </param>
public readonly record struct ProcessCpuSample(
    int Pid,
    string Name,
    TimeSpan? TotalProcessorTime,
    DateTimeOffset? StartTime = null);

/// <summary>
/// Something we know we could not measure this tick, carried alongside the samples so the engine and
/// the API can distinguish it from an idle process and from an exited one.
/// </summary>
/// <param name="Pid">Null when the failure took a whole watched name, so there is no PID to name.</param>
/// <param name="Why">Short human-readable cause, shown verbatim in the panel.</param>
public sealed record UnmeasuredProcess(string Name, int? Pid, string Why);

/// <summary>
/// One sampler pass. Split from a bare list because "we enumerated ollama and there were none" and
/// "we could not enumerate ollama at all" are opposite facts that a list cannot tell apart.
/// </summary>
public sealed record ProcessSampleResult(
    IReadOnlyList<ProcessCpuSample> Samples,
    IReadOnlyList<string> UnreadableNames);

/// <summary>
/// The OS side of activity detection, abstracted so the engine is unit-testable with a fake.
/// Implementations must never invent a measurement: a process whose CPU time is unreadable is
/// reported with a null <see cref="ProcessCpuSample.TotalProcessorTime"/>, and a watched name that
/// could not be enumerated at all is reported through <see cref="SampleDetailed"/>.
/// </summary>
public interface IProcessCpuSampler
{
    /// <summary>
    /// The measurable samples only. Kept as the interface's primary method so existing callers and
    /// fakes are unaffected, but it cannot express an enumeration failure — prefer
    /// <see cref="SampleDetailed"/> on any path that acts on the result.
    /// </summary>
    IReadOnlyList<ProcessCpuSample> Sample(IReadOnlyList<string> watchedNames);

    /// <summary>
    /// The full truth of one pass, including which watched names could not be enumerated. The default
    /// implementation degrades honestly for a fake that only implements <see cref="Sample"/>: it
    /// reports no unreadable names, which is exactly what such a fake is claiming.
    /// </summary>
    ProcessSampleResult SampleDetailed(IReadOnlyList<string> watchedNames)
        => new(Sample(watchedNames), []);
}

/// <summary>
/// Real sampler: <see cref="Process.GetProcessesByName(string)"/> + <c>TotalProcessorTime</c>. Both
/// are ordinary read-only Win32/.NET calls — no driver, no elevation, no hardware write — so this is
/// at the same trust level as the WMI reads elsewhere in this repo and is NOT gated behind
/// GPDFORGE_ENABLE_HARDWARE. Every read is individually guarded because a process list is stale the
/// instant it is produced; a racing exit must drop one sample, never fail the tick — but it is
/// guarded into a NULL MEASUREMENT rather than into silence, because silence here is indistinguishable
/// from an exit and gets reported as one.
/// </summary>
public sealed class SystemProcessCpuSampler(ILogger<SystemProcessCpuSampler>? logger = null) : IProcessCpuSampler
{
    public IReadOnlyList<ProcessCpuSample> Sample(IReadOnlyList<string> watchedNames)
        => SampleDetailed(watchedNames).Samples;

    public ProcessSampleResult SampleDetailed(IReadOnlyList<string> watchedNames)
    {
        var result = new List<ProcessCpuSample>();
        var unreadable = new List<string>();
        var seen = new HashSet<int>();

        foreach (var name in watchedNames)
        {
            Process[] found;
            try { found = Process.GetProcessesByName(name); }
            catch (Exception ex)
            {
                // We do not know whether processes of this name exist. Saying nothing here would be
                // read downstream as "none exist", i.e. as an exit for anything already tracked.
                logger?.LogDebug(ex, "Enumerating {Name} failed", name);
                unreadable.Add(name);
                continue;
            }

            foreach (var p in found)
            {
                using (p)
                {
                    int pid;
                    string pname;
                    try { pid = p.Id; pname = p.ProcessName; }
                    catch (Exception ex)
                    {
                        // No identity at all: nothing honest can be said about this entry, not even
                        // that it is unmeasured, because we cannot name it.
                        logger?.LogDebug(ex, "Identity unavailable for a {Name} process", name);
                        continue;
                    }
                    if (!seen.Add(pid)) continue;

                    TimeSpan? cpu = null;
                    DateTimeOffset? started = null;
                    try { cpu = p.TotalProcessorTime; }
                    catch (Exception ex)
                    {
                        // Exited mid-enumeration, or access denied (an elevated process read from a
                        // non-elevated host). Emitted with a null measurement: present, unmeasured.
                        logger?.LogDebug(ex, "CPU time unavailable for a {Name} process", name);
                    }
                    // Start time is a best-effort extra used only to detect PID reuse; it is denied
                    // in the same cases CPU time is, and its absence degrades to the inferred check.
                    try { started = p.StartTime; }
                    catch (Exception ex) { logger?.LogTrace(ex, "Start time unavailable for pid {Pid}", pid); }

                    result.Add(new ProcessCpuSample(pid, pname, cpu, started));
                }
            }
        }
        return new ProcessSampleResult(result, unreadable);
    }
}

/// <summary>What the engine decided this tick.</summary>
public enum InferenceHoldAction
{
    /// <summary>Nothing changed; whatever hold state was in force stays in force.</summary>
    None,

    /// <summary>Take the keep-awake hold (0→1 edge only; the engine never asks twice).</summary>
    Engage,

    /// <summary>Drop the keep-awake hold (1→0 edge only).</summary>
    Release,
}

/// <summary>
/// Attribution for the UI: which process is keeping the machine awake, how hard it is working, and
/// since when. <paramref name="CpuFraction"/> is a fraction of the whole machine's CPU capacity and
/// is null when the latest tick produced no usable measurement — the process stays busy on its
/// streak, but we will not print a number we did not take.
/// </summary>
public sealed record InferenceProcess(int Pid, string Name, double? CpuFraction, DateTimeOffset BusySince);

/// <summary>The engine's verdict for one tick, including full attribution for the UI.</summary>
public sealed record InferenceHoldDecision(
    InferenceHoldAction Action,
    bool Holding,
    IReadOnlyList<InferenceProcess> Workers,
    string Reason,
    IReadOnlyList<UnmeasuredProcess> Unmeasured = null!)
{
    /// <summary>
    /// What we could not see this tick. Never merged into <see cref="Workers"/> and never silently
    /// dropped: an empty worker list plus a non-empty this is a materially different claim from an
    /// empty worker list alone.
    /// </summary>
    public IReadOnlyList<UnmeasuredProcess> Unmeasured { get; init; } = Unmeasured ?? [];
}

/// <summary>
/// Everything tunable about the hold. All of it is injectable so the tests can drive the engine
/// deterministically and so a user whose workload this misjudges can retune it without a rebuild.
/// </summary>
/// <param name="WatchedNames">Process names (no .exe) considered candidates for inference work.</param>
/// <param name="BusyCpuFraction">
/// Threshold as a fraction of the whole machine's CPU capacity, not of one core. 0.15 on a 16-thread
/// part is ~2.4 threads pegged: comfortably above an idle server's housekeeping and a background
/// python script, comfortably below any real local token generation, which saturates the CPU.
/// </param>
/// <param name="EngageTicks">Consecutive above-threshold ticks before a process counts as working.</param>
/// <param name="ReleaseTicks">
/// Consecutive below-threshold ticks before it stops counting. Deliberately smaller than
/// <paramref name="EngageTicks"/>: releasing early costs at most a re-arm, holding late costs battery.
/// </param>
/// <param name="TickInterval">Sampling cadence. CPU deltas need a gap wide enough to be meaningful.</param>
/// <param name="MaxHold">Hard ceiling on one uninterrupted hold; see the bounded-hold note in the header.</param>
/// <param name="ProcessorCount">0 means "ask the machine"; tests pass a fixed count.</param>
/// <param name="Enforce">
/// False = observe and report only, never touch AntiStandbyService. See InferenceHoldWorker.cs for
/// why observe-only is the default.
/// </param>
public sealed record InferenceHoldOptions(
    IReadOnlyList<string> WatchedNames,
    double BusyCpuFraction = 0.15,
    int EngageTicks = 3,
    int ReleaseTicks = 2,
    TimeSpan? TickInterval = null,
    TimeSpan? MaxHold = null,
    int ProcessorCount = 0,
    bool Enforce = false)
{
    /// <summary>
    /// Defaults verified against what is actually installed on this machine (2026-08-29): Ollama ships
    /// ollama.exe / "ollama app.exe" / llama-server.exe, LM Studio ships "LM Studio.exe" plus its own
    /// llama-server.exe and python.exe. "ollama app" (the tray shell) is included only because the
    /// CPU gate makes a resident-but-idle process free to watch. koboldcpp is a plausible name taken
    /// from its distribution rather than observed here — see the note in the return value.
    /// python/pythonw are the widest net and the most likely to catch something that is not inference;
    /// they are safe only because presence alone can never take a hold.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultWatchedNames =
    [
        "ollama", "ollama app", "llama-server", "llama-cli", "LM Studio", "koboldcpp", "python", "pythonw",
    ];

    public TimeSpan EffectiveTickInterval => TickInterval ?? TimeSpan.FromSeconds(5);
    public TimeSpan EffectiveMaxHold => MaxHold ?? TimeSpan.FromHours(4);
    public int EffectiveProcessorCount => ProcessorCount > 0 ? ProcessorCount : Math.Max(1, Environment.ProcessorCount);

    /// <summary>
    /// Reads the gate and the watch list from the environment. The reader is injected so this is
    /// testable without mutating the process environment. Convention note: GPDFORGE_AUTO_PROFILES is
    /// default-ON-with-"0"-to-disable because it only changes a label; this one is default-OFF because
    /// getting it wrong costs battery overnight, so it takes an explicit "1".
    ///
    /// EngageTicks/ReleaseTicks are deliberately NOT environment-tunable and are left at the record's
    /// declared defaults, which is what Program.cs ships. The asymmetry between them is a safety
    /// property rather than a preference, so the tests assert it against exactly this object.
    /// </summary>
    public static InferenceHoldOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;

        bool enforce = read("GPDFORGE_INFERENCE_HOLD") == "1";
        var names = ParseNames(read("GPDFORGE_INFERENCE_PROCESSES")) ?? DefaultWatchedNames;

        double cpu = 0.15;
        if (double.TryParse(read("GPDFORGE_INFERENCE_CPU"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double parsed)
            && parsed > 0 && parsed <= 1)
        {
            cpu = parsed;
        }

        return new InferenceHoldOptions(names, BusyCpuFraction: cpu, Enforce: enforce);
    }

    /// <summary>
    /// Comma-separated override, .exe tolerated and stripped because that is how a user reads the name
    /// off Task Manager. Returns null (meaning "use the defaults") for an absent or all-blank value —
    /// an empty watch list would silently disable the feature while looking configured.
    /// </summary>
    public static IReadOnlyList<string>? ParseNames(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var names = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count > 0 ? names : null;
    }
}

/// <summary>
/// Pure hysteresis engine. Deterministic: same timestamps and samples in, same decisions out. Reads
/// no clock, spawns nothing, and holds no reference to the OS — the only state is per-PID streak
/// bookkeeping carried between ticks.
/// </summary>
public sealed class InferenceHoldEngine
{
    private sealed class Track
    {
        public string Name = "";
        public DateTimeOffset? Start;
        public TimeSpan LastCpu;
        public bool HasBaseline;
        public int Above;
        public int Below;
        public double? Fraction;
        public bool Busy;
        public DateTimeOffset BusySince;
        public DateTimeOffset FirstAbove;

        /// <summary>
        /// Everything a successor on a recycled PID must NOT inherit. The streak state is the part
        /// that decides the hold, so wiping only the baseline (the old behaviour) let a four-second-old
        /// process be reported as the reason the machine had been awake since noon.
        /// </summary>
        public void Reset()
        {
            LastCpu = default;
            HasBaseline = false;
            Above = 0;
            Below = 0;
            Fraction = null;
            Busy = false;
            BusySince = default;
            FirstAbove = default;
        }
    }

    private readonly InferenceHoldOptions _opt;
    private readonly int _cpus;
    private readonly int _engageTicks;
    private readonly int _releaseTicks;
    private readonly Dictionary<int, Track> _tracks = [];
    private DateTimeOffset? _lastTickAt;
    private bool _holding;
    private DateTimeOffset _holdSince;

    public InferenceHoldEngine(InferenceHoldOptions options)
    {
        _opt = options;
        _cpus = Math.Max(1, options.EffectiveProcessorCount);
        _engageTicks = Math.Max(1, options.EngageTicks);
        // Clamped to at most the engage count so a misconfiguration can never make releasing the slow
        // direction. The bias toward sleeping is a property of the design, not of the config file.
        _releaseTicks = Math.Clamp(options.ReleaseTicks, 1, _engageTicks);
    }

    /// <summary>True while the engine believes a hold is justified.</summary>
    public bool Holding => _holding;

    /// <summary>When the current hold was taken. Meaningless (and not exposed) while not holding.</summary>
    public DateTimeOffset? HoldingSince => _holding ? _holdSince : null;

    /// <summary>
    /// Feed one observation. Returns the action the caller must take — Engage and Release are edges,
    /// emitted at most once per transition, so a caller that simply obeys them can neither leak a hold
    /// nor over-release one.
    /// </summary>
    /// <param name="unreadableNames">
    /// Watched names whose process list could not be enumerated this tick. Tracks under these names
    /// that are missing from <paramref name="samples"/> are treated as unmeasured (release streak
    /// advances, attribution stays honest) instead of as exited.
    /// </param>
    public InferenceHoldDecision Tick(
        DateTimeOffset now,
        IReadOnlyList<ProcessCpuSample> samples,
        IReadOnlyList<string>? unreadableNames = null)
    {
        var previousAt = _lastTickAt;
        _lastTickAt = now;

        var elapsed = previousAt is null ? (TimeSpan?)null : now - previousAt.Value;
        // A non-positive interval is not a measurement window. Refresh the baselines so the NEXT tick
        // is usable, and change nothing else: a stepped clock must not be read as either work or idle.
        bool usable = elapsed is TimeSpan e && e > TimeSpan.Zero;

        var unreadable = unreadableNames is { Count: > 0 }
            ? new HashSet<string>(unreadableNames, StringComparer.OrdinalIgnoreCase)
            : null;

        var unmeasured = new List<UnmeasuredProcess>();
        bool exitedWhileBusy = false;
        bool lostSightWhileBusy = false;

        // A tick we could not measure is not evidence of work. It advances the release streak exactly
        // as an idle tick does — the design errs toward letting the machine sleep — but the caller is
        // told which of the two happened. Returns true if this cleared a standing Busy flag.
        static bool Idle(Track t, int releaseTicks)
        {
            t.Below++;
            t.Above = 0;
            if (t.Busy && t.Below >= releaseTicks) { t.Busy = false; return true; }
            return false;
        }

        var alive = new HashSet<int>();
        foreach (var s in samples)
        {
            if (!alive.Add(s.Pid)) continue;   // duplicate: one process matched by two watch names

            if (_tracks.TryGetValue(s.Pid, out var t))
            {
                // Exact reuse detection: same PID, different process start time is a different
                // process, full stop. The predecessor is gone, so it counts as an exit.
                if (t.Start is DateTimeOffset knownStart && s.StartTime is DateTimeOffset sampledStart
                    && knownStart != sampledStart)
                {
                    exitedWhileBusy |= t.Busy;
                    t.Reset();
                    t.Start = sampledStart;
                }
            }
            else
            {
                t = new Track();
                _tracks[s.Pid] = t;
            }
            t.Name = s.Name;
            t.Start ??= s.StartTime;

            if (s.TotalProcessorTime is not TimeSpan total)
            {
                // Present, but its CPU time is unreadable. Explicitly NOT an exit and explicitly not
                // a zero. The baseline is dropped so that when the process becomes readable again the
                // first delta is not a multi-tick total divided by one tick, which would read as a
                // burst of work that never happened.
                unmeasured.Add(new UnmeasuredProcess(s.Name, s.Pid, "CPU time unreadable"));
                t.HasBaseline = false;
                t.Fraction = null;
                if (usable) lostSightWhileBusy |= Idle(t, _releaseTicks);
                continue;
            }

            double? fraction = null;
            if (usable && t.HasBaseline)
            {
                var delta = total - t.LastCpu;
                // Backwards CPU time means the PID was recycled onto a different process and the
                // sampler could not give us start times to prove it. There is no honest rate across
                // that boundary, and — the part that used to be missing — no honest STREAK across it
                // either, so the whole track goes, not just the baseline.
                if (delta < TimeSpan.Zero)
                {
                    exitedWhileBusy |= t.Busy;
                    t.Reset();
                    t.Start = s.StartTime;
                }
                else
                {
                    fraction = delta.TotalSeconds / (elapsed!.Value.TotalSeconds * _cpus);
                }
            }

            t.LastCpu = total;
            t.HasBaseline = true;

            if (!usable) continue;

            t.Fraction = fraction;
            if (fraction is double f && f >= _opt.BusyCpuFraction)
            {
                if (t.Above == 0) t.FirstAbove = now;
                t.Above++;
                t.Below = 0;
                if (!t.Busy && t.Above >= _engageTicks)
                {
                    t.Busy = true;
                    // Credited from the first above-threshold tick, not from the moment the streak
                    // completed: the process really has been working since then.
                    t.BusySince = t.FirstAbove;
                }
            }
            else
            {
                Idle(t, _releaseTicks);
            }
        }

        foreach (var pid in _tracks.Keys.Where(p => !alive.Contains(p)).ToList())
        {
            var t = _tracks[pid];

            // A PID missing from a sample we could not take is not a PID that exited. Keep the track,
            // advance the release streak (unmeasured is never a reason to keep holding), and say so.
            if (unreadable is not null && unreadable.Contains(t.Name))
            {
                unmeasured.Add(new UnmeasuredProcess(t.Name, pid, "process list unreadable"));
                t.HasBaseline = false;
                t.Fraction = null;
                if (usable) lostSightWhileBusy |= Idle(t, _releaseTicks);

                // Once it has fully released it is costing us nothing to forget, and remembering it
                // forever would leak a track per PID we can never see again. The name-level entry
                // below keeps telling the truth about the blind spot after the PID is dropped.
                if (!t.Busy && t.Below >= _releaseTicks) _tracks.Remove(pid);
                continue;
            }

            // Enumerable and genuinely gone. Release on process exit is immediate and does not wait
            // out the release streak, because there is nothing left to be wrong about.
            exitedWhileBusy |= t.Busy;
            _tracks.Remove(pid);
        }

        // Name-level blind spots, reported even when nothing was ever tracked under the name: an
        // elevated ollama we can never enumerate must not render as "no sustained inference work".
        if (unreadable is not null)
            foreach (var name in unreadable)
                unmeasured.Add(new UnmeasuredProcess(name, null, "process list unreadable"));

        var workers = _tracks
            .Where(kv => kv.Value.Busy)
            .OrderByDescending(kv => kv.Value.Fraction ?? -1)
            .ThenBy(kv => kv.Key)
            .Select(kv => new InferenceProcess(kv.Key, kv.Value.Name, kv.Value.Fraction, kv.Value.BusySince))
            .ToList();

        if (!usable)
            return new InferenceHoldDecision(
                InferenceHoldAction.None, _holding, workers, "no measurement window", unmeasured);

        // Bounded hold: expire it and make the engine re-earn one from scratch. Wiping the streaks is
        // the point — an expiry that left the busy flags standing would re-engage on the next tick and
        // the ceiling would mean nothing.
        if (_holding && now - _holdSince >= _opt.EffectiveMaxHold)
        {
            foreach (var t in _tracks.Values) { t.Busy = false; t.Above = 0; t.Below = 0; }
            _holding = false;
            return new InferenceHoldDecision(
                InferenceHoldAction.Release, false, [], "bounded hold expired; re-evaluating", unmeasured);
        }

        bool shouldHold = workers.Count > 0;

        if (shouldHold && !_holding)
        {
            _holding = true;
            _holdSince = now;
            return new InferenceHoldDecision(InferenceHoldAction.Engage, true, workers,
                $"{workers[0].Name} (pid {workers[0].Pid}) sustained CPU work", unmeasured);
        }

        if (!shouldHold && _holding)
        {
            _holding = false;
            // Order matters: an observed exit is the strongest claim and is only made when the
            // process list was readable. Losing sight is reported as losing sight.
            string why = exitedWhileBusy ? "watched process exited"
                : lostSightWhileBusy ? "watched process could no longer be measured"
                : "watched processes went idle";
            return new InferenceHoldDecision(InferenceHoldAction.Release, false, workers, why, unmeasured);
        }

        string steady = _holding ? "still working"
            : unmeasured.Count > 0
                ? $"no sustained inference work among the processes we could measure ({unmeasured.Count} unmeasured)"
                : "no sustained inference work";
        return new InferenceHoldDecision(InferenceHoldAction.None, _holding, workers, steady, unmeasured);
    }
}

/// <summary>
/// Immutable snapshot for the API. Nulls are load-bearing: HoldingSince is null when nothing is held,
/// LastTickAt is null before the worker has ever run, and a worker's CpuFraction is null when the last
/// tick produced no usable measurement. None of these are ever filled with a plausible-looking number.
/// </summary>
/// <param name="Unmeasured">
/// What we could not see on the last tick. Non-empty means Workers is a claim about a partial view of
/// the machine, and the panel must say so rather than implying nothing is running.
/// </param>
public sealed record InferenceHoldStatus(
    bool Enforcing,
    bool Holding,
    DateTimeOffset? HoldingSince,
    IReadOnlyList<InferenceProcess> Workers,
    DateTimeOffset? LastTickAt,
    string? LastReason,
    IReadOnlyList<string> WatchedNames,
    double BusyCpuFraction,
    IReadOnlyList<UnmeasuredProcess> Unmeasured = null!)
{
    public IReadOnlyList<UnmeasuredProcess> Unmeasured { get; init; } = Unmeasured ?? [];
}

/// <summary>
/// The bridge between the worker (single writer) and the API (many readers). A single immutable
/// snapshot swapped atomically, rather than a bag of mutable properties, so a reader can never see a
/// half-updated state where Holding is true but Workers is still last tick's list.
/// </summary>
public sealed class InferenceHoldState
{
    private InferenceHoldStatus _current;

    public InferenceHoldState(InferenceHoldOptions options)
        => _current = new InferenceHoldStatus(
            options.Enforce, false, null, [], null, null, options.WatchedNames, options.BusyCpuFraction);

    public InferenceHoldStatus Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
