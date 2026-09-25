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

    /// <summary>In memory only, starting at <c>windows</c> — tests and tools.</summary>
    public ModeState() : this(null) { }

    public ModeState(ModeStore? store, ILogger<ModeState>? logger = null)
    {
        _store = store;
        _logger = logger;
        var saved = store?.Read();
        Restored = saved is not null;
        _active = saved ?? ModeCatalogue.Windows;
    }

    /// <summary>True when the starting mode was read back from the store — a choice the user made
    /// before the restart, which the auto-profile engine must not treat as a mode to switch away from
    /// (audit round 2, 2026-09-25; see FocusProfileLoop).</summary>
    public bool Restored { get; }

    public string Active
    {
        get { lock (_gate) return _active; }
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            lock (_gate)
            {
                if (string.Equals(_active, value, StringComparison.Ordinal)) return;
                _active = value;

                // Only a mode the catalogue knows is saved. POST /mode accepts any name (ProfileApplier
                // answers UnknownMode), and a typo must not become what the next start applies.
                if (_store is null || !ModeCatalogue.Exists(value)) return;
                try { _store.Write(value); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The switch still applies for this run; it just will not survive a restart.
                    _logger?.LogWarning(ex, "Mode '{Mode}' applies now but could not be saved; a restart will start in the last saved mode.", value);
                }
            }
        }
    }
}
