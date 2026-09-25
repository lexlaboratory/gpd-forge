// GPD Forge — a game's "freeze in background" list, frozen while it plays and ALWAYS thawed. GPL-3.0-or-later.
//
// F5 (2026-09-25). A rule's freeze[] (stored since F1) names background apps that compete with the game
// for the 22 W budget: a local LLM server holding the GPU, a sync client hashing files. While the game's
// profile is in force GameProfileApplier hands the list here; this suspends what is running through the
// existing FreezerService, and thaws exactly that when the profile comes off.
//
// The risk of the feature is a process left suspended: OneDrive frozen for the rest of the day because
// an exit path forgot it. So every path thaws:
//  - the profile coming off (the game leaves the front, exits, the mode changes) → GameProfileApplier.End,
//    which thaws in a finally, whatever else in End throws;
//  - a Begin that throws after freezing → thawed before the exception leaves Begin;
//  - a clean stop → FocusProfileWorker.StopAsync → End, and ForgeWorker's ThawAll behind it;
//  - a crash or a kill, where no code runs → the PIDs are on disk (FreezeRecordStore) from the freeze
//    until the thaw, and the next start resumes them (RecoverAtStartup).
//
// And what it freezes is narrow: never the protected list (FreezerService refuses it too), never a name
// someone already froze by hand — the game leaving must not thaw what the user froze on the Monitor
// page — and nothing that is not running (FreezeByName finds no PID, and the name is not recorded).
using GpdForge.SystemControl;
using Microsoft.Extensions.Logging;

namespace GpdForge.Profiles;

public sealed class GameFreezer(
    FreezerService freezer,
    IProcessSuspender suspender,
    IProcessLister? lister = null,
    FreezeRecordStore? store = null,
    ILogger<GameFreezer>? logger = null)
{
    /// <summary>What the Games editor offers first: the heavy background apps measured on the device
    /// (Ollama, LM Studio, OneDrive, Google Drive), lower-cased like a rule's names. Offered, never
    /// frozen by default — the list is opt-in per game.</summary>
    public static readonly IReadOnlyList<string> Suggested = ["ollama", "lm studio", "onedrive", "googledrivefs"];

    private readonly IProcessLister _lister = lister ?? new DiagnosticsProcessLister();
    private readonly Lock _gate = new();
    private readonly List<string> _held = [];

    /// <summary>The names this game froze and still holds.</summary>
    public IReadOnlyList<string> Held { get { lock (_gate) return _held.ToArray(); } }

    /// <summary>
    /// Pure policy: which of <paramref name="asked"/> to try. Normalised and de-duplicated, minus the
    /// protected list and anything <paramref name="alreadyFrozen"/> (someone else's freeze).
    /// </summary>
    public static IReadOnlyList<string> Plan(IEnumerable<string>? asked, IReadOnlyCollection<string> alreadyFrozen)
    {
        ArgumentNullException.ThrowIfNull(alreadyFrozen);
        var taken = new HashSet<string>(alreadyFrozen.Select(AppRulePolicy.Normalize), StringComparer.Ordinal);
        return (asked ?? [])
            .Select(AppRulePolicy.Normalize)
            .Where(n => n.Length > 0 && !FreezerService.IsProtected(n) && !taken.Contains(n))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Freezes what <see cref="Plan"/> allows and is running. Returns the names actually frozen (at least
    /// one process suspended). Never throws: a freeze is a nicety, and a failure half-way thaws what it
    /// had frozen rather than leave a partial set nobody tracks.
    /// </summary>
    public IReadOnlyList<string> Freeze(IEnumerable<string>? names)
    {
        Thaw();   // one game at a time: a list still held is never stacked under another
        var plan = Plan(names, freezer.Frozen);
        if (plan.Count == 0) return [];
        lock (_gate)
        {
            try
            {
                foreach (var name in plan)
                {
                    if (freezer.FreezeByName(name) == 0) continue;   // not running (or refused): nothing to hold
                    _held.Add(name);
                    // Recorded after each name, so a crash mid-list still leaves every suspended PID on disk.
                    store?.Write(new FreezeRecord(_held.Select(n => new FrozenName(n, freezer.PidsFor(n))).ToArray()));
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Game freeze failed part-way; thawing what it had frozen.");
                ThawHeld();
                return [];
            }
            if (_held.Count > 0) logger?.LogInformation("Game freeze: suspended {Names}.", string.Join(", ", _held));
            return _held.ToArray();
        }
    }

    /// <summary>Thaws everything this game froze, and forgets the record. Never throws. Returns the names thawed.</summary>
    public IReadOnlyList<string> Thaw()
    {
        lock (_gate) return ThawHeld();
    }

    /// <summary>
    /// At startup: resumes the PIDs a previous run froze and never thawed (a crash, a kill). Each PID is
    /// resumed only while a process of the recorded name still holds it — a PID reused by an unrelated
    /// process is left alone. Resuming a process that is not suspended does nothing. Returns how many resumed.
    /// </summary>
    public int RecoverAtStartup()
    {
        if (store?.Read() is not FreezeRecord record) return 0;
        store.Clear();
        int resumed = 0;
        foreach (var entry in record.Names)
        {
            IReadOnlyList<ProcessRef> running;
            try { running = _lister.ByName(entry.Name); }
            catch (Exception ex) { logger?.LogWarning(ex, "Game freeze recovery: could not list '{Name}'.", entry.Name); continue; }
            foreach (var proc in running.Where(p => entry.Pids.Contains(p.Pid)))
            {
                try { suspender.Resume(proc.Pid); resumed++; }
                catch (Exception ex) { logger?.LogWarning(ex, "Game freeze recovery: could not resume pid {Pid}.", proc.Pid); }
            }
        }
        if (resumed > 0)
            logger?.LogInformation("Game freeze recovery: resumed {Count} process(es) a previous run left suspended.", resumed);
        return resumed;
    }

    private IReadOnlyList<string> ThawHeld()
    {
        var names = _held.ToArray();
        foreach (var name in names)
        {
            try { freezer.Thaw(name); }
            catch (Exception ex) { logger?.LogWarning(ex, "Game freeze: could not thaw '{Name}'.", name); }
        }
        _held.Clear();
        store?.Clear();
        if (names.Length > 0) logger?.LogInformation("Game freeze: resumed {Names}.", string.Join(", ", names));
        return names;
    }
}
