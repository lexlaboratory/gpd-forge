// GPD Forge — which running processes the Games editor offers to freeze during a game. GPL-3.0-or-later.
//
// F5 (2026-09-25). The editor shows a checklist, and a checklist of 200 process names is useless: the
// point is the few heavy background apps worth suspending. So it offers the measured suggestions
// (GameFreezer.Suggested) when they run, plus anything else holding a lot of memory — minus what must
// never be frozen (FreezerService's protected list) and the Windows plumbing that is technically
// freezable but takes the session with it (audio, input, the Start menu). The ranking is pure; the
// process snapshot is the only OS call, and a process that exits mid-read is simply skipped.
using System.Diagnostics;
using GpdForge.Profiles;

namespace GpdForge.SystemControl;

public sealed record FreezeCandidate(string Name, long MemoryMb, bool Suggested);

public static class FreezeCandidates
{
    /// <summary>An unlisted process needs this much working set to be worth offering.</summary>
    public const long MinMemoryMb = 200;

    /// <summary>At most this many rows: a checklist, not a task manager.</summary>
    public const int MaxRows = 12;

    // Freezable in the NT sense, but freezing them breaks the session under the game: game audio
    // (audiodg), typing (ctfmon, TextInputHost), the shell's own hosts, Defender (which refuses anyway).
    private static readonly HashSet<string> NeverOffered = new(StringComparer.Ordinal)
    {
        "idle", "system", "registry", "memory compression", "smss", "audiodg", "fontdrvhost", "sihost",
        "ctfmon", "textinputhost", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "runtimebroker", "applicationframehost", "msmpeng", "nissrv", "securityhealthservice", "lsaiso",
        "conhost", "winlogon", "presentmon",
    };

    /// <summary>
    /// Ranks <paramref name="processes"/> (name, working-set bytes) into the checklist: per name, memory
    /// summed over its processes; suggested names first, then by memory. <paramref name="game"/> — the
    /// rule's own match — is left out: a game cannot be frozen behind itself.
    /// </summary>
    public static IReadOnlyList<FreezeCandidate> Rank(IEnumerable<(string Name, long Bytes)> processes, string? game = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var suggested = new HashSet<string>(GameFreezer.Suggested, StringComparer.Ordinal);
        return processes
            .Select(p => (Name: AppRulePolicy.Normalize(p.Name), p.Bytes))
            .Where(p => p.Name.Length > 0 && Offerable(p.Name) && !AppRulePolicy.Matches(game, p.Name))
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g => new FreezeCandidate(g.Key, g.Sum(p => Math.Max(0, p.Bytes)) / (1024 * 1024), suggested.Contains(g.Key)))
            .Where(c => c.Suggested || c.MemoryMb >= MinMemoryMb)
            .OrderByDescending(c => c.Suggested).ThenByDescending(c => c.MemoryMb).ThenBy(c => c.Name, StringComparer.Ordinal)
            .Take(MaxRows)
            .ToArray();
    }

    private static bool Offerable(string name) =>
        !FreezerService.IsProtected(name) && !NeverOffered.Contains(name) && !name.StartsWith("gpdforge", StringComparison.Ordinal);

    /// <summary>Every running process's name and working set. The daemon (LocalSystem) sees the user's.</summary>
    public static IReadOnlyList<(string Name, long Bytes)> Snapshot()
    {
        var list = new List<(string, long)>();
        foreach (var p in Process.GetProcesses())
        {
            try { list.Add((p.ProcessName, p.WorkingSet64)); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* exited, or no access */ }
            finally { p.Dispose(); }
        }
        return list;
    }
}
