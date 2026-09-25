// GPD Forge - focus-process profile switching with anti-flapping hysteresis. GPL-3.0-or-later.
//
// Given the foreground app (and AC state) each tick, decides WHEN to switch usage mode. A brief
// alt-tab must not flip modes, so a target must persist for `stabilityTicks` before it wins.
namespace GpdForge.Profiles;

public sealed class FocusProfileEngine
{
    private readonly IModeResolver _rules;
    private readonly int _stabilityTicks;
    private string _active;
    private string? _candidate;
    private int _candidateTicks;

    /// <summary>Ticks a new target must hold before it wins (~4.5 s at the worker's 1.5 s). The focus
    /// loop settles a rule's game profile on the same count, so the two move together.</summary>
    public const int DefaultStabilityTicks = 3;

    public FocusProfileEngine(string initial = "windows", IModeResolver? rules = null, int stabilityTicks = DefaultStabilityTicks)
    {
        _active = initial;
        _rules = rules ?? ModeRules.Default();
        _stabilityTicks = Math.Max(1, stabilityTicks);
    }

    public string Active => _active;

    /// <summary>The mode a foreground app + power state resolve to (before hysteresis).</summary>
    public string Resolve(string? foregroundProcess, bool acConnected)
        => _rules.ModeFor(foregroundProcess) ?? (acConnected ? "windows" : "battery");

    /// <summary>Takes <paramref name="mode"/> as the engine's own starting point without switching to
    /// it: later ticks switch only when the resolution moves away from it. Used for a mode restored at
    /// start (see FocusProfileLoop), which the engine did not choose and must not undo on its first
    /// ticks just because the foreground has no rule.</summary>
    public void Adopt(string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        _active = mode;
        _candidate = null;
        _candidateTicks = 0;
    }

    /// <summary>Feed one sample. Returns the new mode if a switch happened this tick, else null.</summary>
    public string? Tick(string? foregroundProcess, bool acConnected)
    {
        string target = Resolve(foregroundProcess, acConnected);

        if (target == _active) { _candidate = null; _candidateTicks = 0; return null; }

        if (target == _candidate) _candidateTicks++;
        else { _candidate = target; _candidateTicks = 1; }

        if (_candidateTicks >= _stabilityTicks)
        {
            _active = target;
            _candidate = null;
            _candidateTicks = 0;
            return _active;
        }
        return null;
    }
}
