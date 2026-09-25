// GPD Forge — a game's fan mode, layered over the user's without replacing it. GPL-3.0-or-later.
//
// FanState.Mode is what FanWorker drives every second, so a game profile sets it there — the same
// owner the fan endpoint uses, not a parallel one FanWorker would have to learn about. What must NOT
// happen is that the game's mode becomes the user's preference:
//
//  - it is never written to fan.json. GameProfileApplier has no FanPreferenceStore at all, and the
//    paths that DO save (POST /fan with only a duty, the settings export) ask PersistableMode, which
//    answers the user's own mode while a game holds the fan;
//  - leaving the game puts back the mode that was in force before it — unless the user changed the
//    fan meanwhile (POST /fan, a settings import, /panic's emergency Aggressive). Then that change is
//    theirs, it stands, and Release() forgets the restore.
using GpdForge.Api;

namespace GpdForge.Fan;

public sealed class FanOverride
{
    private readonly Lock _gate = new();
    private string? _applied;
    private string? _previous;

    /// <summary>Whether a game currently holds the fan mode.</summary>
    public bool Active { get { lock (_gate) return _applied is not null; } }

    /// <summary>Sets the game's mode, remembering the one it replaces. A second apply (the rule was
    /// edited mid-game) keeps the ORIGINAL previous mode, never the first game value.</summary>
    public void Apply(FanState fan, string mode)
    {
        ArgumentNullException.ThrowIfNull(fan);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        lock (_gate)
        {
            if (_applied is null || fan.Mode != _applied) _previous = fan.Mode;
            _applied = mode;
            fan.Mode = mode;
        }
    }

    /// <summary>Puts the pre-game mode back if the fan is still on the game's; forgets either way.</summary>
    public void Restore(FanState fan)
    {
        ArgumentNullException.ThrowIfNull(fan);
        lock (_gate)
        {
            if (_applied is not null && fan.Mode == _applied && _previous is not null) fan.Mode = _previous;
            _applied = null;
            _previous = null;
        }
    }

    /// <summary>The user (or /panic) took the fan: whatever they set stays after the game.</summary>
    public void Release()
    {
        lock (_gate) { _applied = null; _previous = null; }
    }

    /// <summary>The mode to SAVE as the user's preference: theirs while a game holds the fan.</summary>
    public string PersistableMode(FanState fan)
    {
        ArgumentNullException.ThrowIfNull(fan);
        lock (_gate)
            return _applied is not null && fan.Mode == _applied && _previous is not null ? _previous : fan.Mode;
    }
}
