// GPD Forge — auto-tuner sweep state + stepping. GPL-3.0-or-later.
//
// TunerState is the mutable holder the API reads/writes (alongside AutoFpsState, PowerSourceState,
// FanState — see core/Program.cs), plus the sweep-stepping itself: ForgeWorker calls Tick() once per
// telemetry tick while a sweep is running, and Tick() decides whether to keep dwelling at the
// current STAPM, record a point and move to the next one, or finish the sweep. The one-tick-at-a-
// time shape keeps ForgeWorker's loop in charge of cadence/cancellation (same pattern as
// GuardianService.Observe / AutoFpsLoop.TickAsync) while the actual "what to record / what's next"
// decision stays here, unit-testable with synthetic fps/temp readings instead of real hardware.
using GpdForge.Tdp;

namespace GpdForge.Tuner;

/// <summary>Pure sweep-stepping: what STAPM to try after the current one, or null once the sweep has
/// covered up to <paramref name="maxW"/>. No I/O — unit-tested directly.</summary>
public static class TunerSweepPlanner
{
    public static int? NextStapmW(int currentW, int minW, int maxW, int stepW)
    {
        int step = stepW > 0 ? stepW : 1;
        int next = currentW + step;
        return next <= maxW ? next : null;
    }
}

/// <summary>
/// Holds the auto-tuner's config + recorded sweep points, and steps it one tick at a time. Thread-
/// safe: the worker calls <see cref="Tick"/> while the API may concurrently call <see cref="Start"/>
/// or read the properties below (mirrors GuardianService's locking).
/// </summary>
public sealed class TunerState
{
    /// <summary>How many worker ticks (~1 Hz) to hold each STAPM step before recording + advancing —
    /// enough for a real device to settle, short enough that a full sweep still finishes in a
    /// reasonable time.</summary>
    public const int DwellTicks = 5;

    /// <summary>STAPM step size between sweep points, in watts.</summary>
    public const int StepW = 2;

    private const int MinAllowedW = 5, MaxAllowedW = 40;

    private readonly object _lock = new();
    private readonly List<TunePoint> _points = new();
    private int _dwellRemaining;

    public bool Running { get; private set; }
    public TuneGoal Goal { get; private set; } = TuneGoal.MaxFps;
    public int? TargetFps { get; private set; }
    public int MinW { get; private set; } = 8;
    public int MaxW { get; private set; } = 30;
    public int TempCapC { get; private set; } = 95;
    public int CurrentStapmW { get; private set; } = 8;

    /// <summary>Set once a sweep finishes without a usable <see cref="Best"/> — an honest explanation
    /// rather than a silent null (e.g. FPS telemetry wasn't available for the whole sweep).</summary>
    public string? Note { get; private set; }

    public IReadOnlyList<TunePoint> Points { get { lock (_lock) return _points.ToArray(); } }

    /// <summary>The current best pick for the configured goal, recomputed from whatever has been
    /// recorded so far (null before anything usable has been recorded).</summary>
    public TuneResult? Best { get { lock (_lock) return AutoTuner.PickBest(_points, Goal, TargetFps, TempCapC); } }

    /// <summary>
    /// (Re)starts a sweep from <c>minW</c>: clears any previously recorded points, so a repeated
    /// start always reflects a fresh run rather than mixing stale data into the new goal's pick.
    /// Bounds are clamped into the device's safe TDP band and normalized if swapped, so a bad request
    /// can never arm a sweep outside it.
    /// </summary>
    public void Start(TuneGoal goal, int? targetFps, int? minW, int? maxW, int? tempCapC)
    {
        lock (_lock)
        {
            int lo = Math.Clamp(minW ?? MinW, MinAllowedW, MaxAllowedW);
            int hi = Math.Clamp(maxW ?? MaxW, MinAllowedW, MaxAllowedW);
            if (hi < lo) (lo, hi) = (hi, lo);

            Goal = goal;
            TargetFps = targetFps is int t && t > 0 ? t : null;
            MinW = lo;
            MaxW = hi;
            TempCapC = tempCapC ?? TempCapC;
            _points.Clear();
            Note = null;
            CurrentStapmW = lo;
            _dwellRemaining = DwellTicks;
            Running = true;
        }
    }

    /// <summary>
    /// One worker tick while a sweep is running, given the live snapshot's Fps/CpuTempC while
    /// <see cref="CurrentStapmW"/> is the applied STAPM. Returns the STAPM the caller should apply
    /// THIS tick (steady during a dwell; the next step once one completes), or null once the sweep
    /// has finished (nothing left to apply — <see cref="Running"/> is now false).
    /// <para>
    /// Honesty gate: a non-positive <paramref name="fps"/> is not a real measurement (no FPS
    /// telemetry wired yet on this device — see docs/api.md) and is deliberately never recorded, so a
    /// sweep run without FPS telemetry finishes with zero points and <see cref="Best"/> stays null,
    /// with <see cref="Note"/> explaining why — never a faked reading.
    /// </para>
    /// </summary>
    /// <summary>
    /// Feeds one sample into the running sweep. Nullable since 2026-09-01: telemetry reports null for
    /// a sensor it cannot read, and a sweep point recorded against an invented zero would make the
    /// tuner "learn" that every wattage produces no frames and pick the lowest.
    /// </summary>
    public int? Tick(double? fps, double? tempC)
    {
        lock (_lock)
        {
            if (!Running) return null;

            _dwellRemaining--;
            if (_dwellRemaining > 0) return CurrentStapmW;

            // Both readings required, not just the frame rate. PickBest filters points against a
            // thermal cap, so a point with no temperature cannot be judged against it — recording one
            // would put a candidate in the running that the cap was never able to exclude.
            if (fps is double f && f > 0 && tempC is double tc)
                _points.Add(new TunePoint(CurrentStapmW, f, tc));

            int? next = TunerSweepPlanner.NextStapmW(CurrentStapmW, MinW, MaxW, StepW);
            if (next is null)
            {
                Running = false;
                Note = _points.Count == 0
                    ? "Sweep finished but recorded no usable points — FPS stayed 0 the whole sweep (no FPS telemetry wired yet on this device)."
                    : null;
                return null;
            }

            CurrentStapmW = next.Value;
            _dwellRemaining = DwellTicks;
            return CurrentStapmW;
        }
    }

    /// <summary>The flat profile to apply for <see cref="CurrentStapmW"/>: no boost above the
    /// sustained target, so any FPS change is attributable to STAPM alone, and the thermal ceiling is
    /// the configured temp cap.</summary>
    public TdpProfile CurrentProfile()
    {
        int w = CurrentStapmW;
        return new TdpProfile(w, w, w, TempCapC);
    }
}
