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

    /// <summary>In memory only, starting at <c>windows</c> — tests and tools.</summary>
    public ModeState() : this(null) { }

    public ModeState(ModeStore? store, ILogger<ModeState>? logger = null)
    {
        _store = store;
        _logger = logger;
        var saved = store?.ReadEntry();
        Restored = saved is { ChosenByUser: true };
        _chosenByUser = Restored;
        _active = Restored ? saved!.Mode : ModeCatalogue.Windows;
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

    /// <summary>A switch the daemon made on its own — auto-profiles, the AC/battery switch. Saved, so a
    /// restart does not resurrect an older user pick over it, but not restored as a choice.</summary>
    public void SwitchAutomatically(string mode) => Set(mode, chosenByUser: false);

    private void Set(string value, bool chosenByUser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        lock (_gate)
        {
            // Same mode: nothing to write, unless the user is now re-picking what an automatic switch
            // chose — that makes it theirs. An automatic switch never demotes the user's own pick.
            if (string.Equals(_active, value, StringComparison.Ordinal) && (_chosenByUser || !chosenByUser)) return;
            _active = value;
            _chosenByUser = chosenByUser;

            // Only a mode the catalogue knows is saved. POST /mode accepts any name (ProfileApplier
            // answers UnknownMode), and a typo must not become what the next start applies.
            if (_store is null || !ModeCatalogue.Exists(value)) return;
            try { _store.Write(value, chosenByUser); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The switch still applies for this run; it just will not survive a restart.
                _logger?.LogWarning(ex, "Mode '{Mode}' applies now but could not be saved; a restart will start in the last saved mode.", value);
            }
        }
    }
}
