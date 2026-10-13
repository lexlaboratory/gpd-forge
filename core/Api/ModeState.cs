// GPD Forge — the active mode. GPL-3.0-or-later.
//
// Moved out of Program.cs in audit round 2 (2026-09-24), when it stopped being a bare property: the
// startup apply writes the active mode's TDP, so the active mode has to be the USER'S mode after a
// restart, not an in-memory `windows` (see core/Profiles/ModeStore.cs).
//
// Saved in the setter rather than at each call site. Three things switch the mode — POST /mode, the
// AC/battery switch in ForgeWorker and FocusProfileWorker — and "remember to save it" is the class of
// instruction this codebase has watched fail; covering every writer by construction is the same
// reasoning as the auditing decorators.
//
// Audit round 3 (2026-09-25): saving is still by construction, but WHO switched is now part of it. The
// setter is the user's pick (POST /mode); the two automatic writers go through SwitchAutomatically. A
// restart restores only the user's pick — an automatic mode was a reaction to the power source and the
// app in front, and restoring it made the engine adopt it: `battery` saved unplugged held 8 W on a
// machine that booted on AC. After an automatic switch the daemon starts in `windows`, as before
// ModeStore, and auto-profiles re-derive the mode from what is true at the new start.
using GpdForge.Profiles;
using Microsoft.Extensions.Logging;

namespace GpdForge.Api;

/// <summary>Active-mode holder for the local API, persisted when given a store.</summary>
public sealed class ModeState
{
    private readonly ModeStore? _store;
    private readonly ILogger? _logger;
    private readonly Lock _gate = new();
    private string _active;
    private bool _chosenByUser;
    private bool _auto;

    /// <summary>In memory only, starting at <c>windows</c> — tests and tools.</summary>
    public ModeState() : this(null) { }

    public ModeState(ModeStore? store, ILogger<ModeState>? logger = null)
    {
        _store = store;
        _logger = logger;
        var saved = store?.ReadEntry();
        // With Auto off nothing switches, so the saved mode is the one in force whoever chose it.
        Restored = saved is { ChosenByUser: true } or { Auto: false };
        _chosenByUser = Restored;
        _active = Restored ? saved!.Mode : ModeCatalogue.Windows;
        _auto = saved?.Auto ?? true;
    }

    /// <summary>True when the starting mode was read back from the store — a choice the user made
    /// before the restart, which the auto-profile engine must not treat as a mode to switch away from
    /// (audit round 2, 2026-09-25; see FocusProfileLoop). False after an automatic switch was the last
    /// thing saved (audit round 3).</summary>
    public bool Restored { get; }

    /// <summary>The active mode. Setting it is the USER's pick (POST /mode), restored after a restart;
    /// automatic writers use <see cref="SwitchAutomatically"/>.</summary>
    public string Active
    {
        get { lock (_gate) return _active; }
        set => Set(value, chosenByUser: true);
    }

    /// <summary>Whether the daemon may switch the mode on its own (auto-profiles). Off after the user
    /// picks a mode (POST /mode) until they turn it back on (POST /mode/auto), and kept across a
    /// restart. It used to live only in the UI: "Manual: Windows" stayed on screen while auto-profiles
    /// moved the handheld to gaming at 25 W (2026-10-10).</summary>
    public bool Auto { get { lock (_gate) return _auto; } }

    /// <summary>True when the mode in force is the user's pick rather than an automatic switch.</summary>
    public bool ChosenByUser { get { lock (_gate) return _chosenByUser; } }

    /// <summary>Turns automatic switching on or off. Off keeps the mode in force as the user's own,
    /// so a restart comes back to it rather than to the default with nothing left to switch it.</summary>
    public void SetAuto(bool enabled)
    {
        lock (_gate)
        {
            if (_auto == enabled && (enabled || _chosenByUser)) return;
            _auto = enabled;
            if (!enabled) _chosenByUser = true;
            Save();
        }
    }

    /// <summary>A switch the daemon made on its own — auto-profiles, the AC/battery switch. Saved, so a
    /// restart does not resurrect an older user pick over it, but not restored as a choice.</summary>
    /// <returns>False, changing nothing, while Auto is off. Refused here rather than only by the
    /// callers: the focus loop reads Auto and switches a few lines later, and a POST /mode landing in
    /// between was overwritten by that switch; the AC/battery switch did not read Auto at all.</returns>
    public bool SwitchAutomatically(string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        lock (_gate)
        {
            if (!_auto) return false;
            SetLocked(mode, chosenByUser: false);
            return true;
        }
    }

    /// <summary>The user's pick (POST /mode): the mode, made theirs, with Auto off — in one step, so no
    /// automatic switch can land between the mode and the Auto flag.</summary>
    public void Pick(string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        lock (_gate)
        {
            _active = mode;
            _chosenByUser = true;
            _auto = false;
            Save();
        }
    }

    /// <summary>The mode, Auto and who chose the mode, read together.</summary>
    public sealed record View(string Active, bool Auto, bool ChosenByUser);

    public View Snapshot() { lock (_gate) return new View(_active, _auto, _chosenByUser); }

    private void Set(string value, bool chosenByUser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        lock (_gate) SetLocked(value, chosenByUser);
    }

    private void SetLocked(string value, bool chosenByUser)
    {
        // Same mode: nothing to write, unless the user is now re-picking what an automatic switch
        // chose — that makes it theirs. An automatic switch never demotes the user's own pick.
        if (string.Equals(_active, value, StringComparison.Ordinal) && (_chosenByUser || !chosenByUser)) return;
        _active = value;
        _chosenByUser = chosenByUser;
        Save();
    }

    /// <summary>Writes the state under the lock. Only a mode the catalogue knows is saved: POST /mode
    /// accepts any name (ProfileApplier answers UnknownMode), and a typo must not become what the next
    /// start applies.</summary>
    private void Save()
    {
        if (_store is null || !ModeCatalogue.Exists(_active)) return;
        try { _store.Write(_active, _chosenByUser, _auto); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The switch still applies for this run; it just will not survive a restart.
            _logger?.LogWarning(ex, "Mode '{Mode}' applies now but could not be saved; a restart will start in the last saved mode.", _active);
        }
    }
}
