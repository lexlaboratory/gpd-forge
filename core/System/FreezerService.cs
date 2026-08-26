// GPD Forge - Freezer: suspend/resume background processes to free CPU/RAM. GPL-3.0-or-later.
//
// Like GPD Tool's Freezer: while a game or a local inference job runs, background apps
// (browsers, chat clients, launchers) can be suspended so they stop competing for CPU/RAM,
// then resumed afterwards. Suspension is a real NT process freeze (all threads stop) via
// ntdll's NtSuspendProcess/NtResumeProcess, taking a handle with OpenProcess.
//
// The freeze/thaw bookkeeping (what is frozen, which PIDs belong to a name) lives in
// FreezerService and is fully unit-testable: the OS calls sit behind IProcessSuspender and
// the process enumeration behind IProcessLister, both faked in tests so no real process is
// ever touched. Critical OS/session processes are never suspended.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GpdForge.SystemControl;

/// <summary>Suspends or resumes a single process by PID. Abstracted so FreezerService stays unit-testable.</summary>
public interface IProcessSuspender
{
    /// <summary>Freeze every thread of the process. Throws on failure (e.g. access denied / gone).</summary>
    void Suspend(int pid);

    /// <summary>Resume a previously frozen process. Throws on failure.</summary>
    void Resume(int pid);
}

/// <summary>A running process reduced to what the Freezer needs: PID and process name (no extension).</summary>
public readonly record struct ProcessRef(int Pid, string Name);

/// <summary>Lists running processes by name. Defaults to System.Diagnostics.Process; fakeable in tests.</summary>
public interface IProcessLister
{
    /// <summary>All running processes whose name matches (name given without the ".exe" extension).</summary>
    IReadOnlyList<ProcessRef> ByName(string name);
}

/// <summary>Real enumeration via <see cref="Process.GetProcessesByName(string)"/>.</summary>
public sealed class DiagnosticsProcessLister : IProcessLister
{
    public IReadOnlyList<ProcessRef> ByName(string name)
    {
        var list = new List<ProcessRef>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { list.Add(new ProcessRef(p.Id, p.ProcessName)); }
            catch { /* the process may have exited between enumeration and read */ }
            finally { p.Dispose(); }
        }
        return list;
    }
}

/// <summary>
/// Real process suspender. Freezes/thaws a process with ntdll's undocumented-but-stable
/// NtSuspendProcess / NtResumeProcess, using a PROCESS_SUSPEND_RESUME handle from OpenProcess.
/// Runs correctly as the SYSTEM service (which owns the required access).
/// </summary>
public sealed partial class NtProcessSuspender : IProcessSuspender
{
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;

    // BOOL/HANDLE/DWORD are all blittable, so no [MarshalAs] is needed under source-generated marshalling.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, int bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(IntPtr hObject);

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(IntPtr processHandle);

    [LibraryImport("ntdll.dll")]
    private static partial int NtResumeProcess(IntPtr processHandle);

    public void Suspend(int pid)
    {
        IntPtr h = Open(pid);
        try { Check(NtSuspendProcess(h), pid, "suspend"); }
        finally { _ = CloseHandle(h); }
    }

    public void Resume(int pid)
    {
        IntPtr h = Open(pid);
        try { Check(NtResumeProcess(h), pid, "resume"); }
        finally { _ = CloseHandle(h); }
    }

    private static IntPtr Open(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, 0, (uint)pid);
        if (h == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess failed for pid {pid}.");
        return h;
    }

    private static void Check(int ntStatus, int pid, string op)
    {
        // NTSTATUS: 0 == STATUS_SUCCESS. Anything else is a failure.
        if (ntStatus != 0)
            throw new InvalidOperationException(
                $"Nt{op[0..1].ToUpperInvariant()}{op[1..]}Process failed for pid {pid} (NTSTATUS 0x{ntStatus:X8}).");
    }
}

/// <summary>
/// Freezes/thaws background processes by name and remembers what it froze. All bookkeeping is
/// in-memory and thread-safe. The actual OS suspend/resume is delegated to
/// <see cref="IProcessSuspender"/> and enumeration to <see cref="IProcessLister"/>, so this
/// class is unit-testable without touching a real process.
/// </summary>
public sealed class FreezerService(
    IProcessSuspender suspender,
    IProcessLister? lister = null,
    ILogger<FreezerService>? logger = null)
{
    // Never suspend these — freezing them can hang or crash the session or the whole OS
    // (and freezing our own service would deadlock the API). Compared without ".exe" and
    // case-insensitively.
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "dwm", "explorer", "GpdForge.Service", "dotnet",
    };

    private readonly IProcessSuspender _suspender = suspender;
    private readonly IProcessLister _lister = lister ?? new DiagnosticsProcessLister();
    private readonly ILogger<FreezerService>? _logger = logger;

    // name (normalized, no extension) -> the set of PIDs we suspended for it.
    private readonly Dictionary<string, HashSet<int>> _frozen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Names currently frozen (each has at least one suspended process).</summary>
    public IReadOnlyCollection<string> Frozen
    {
        get { lock (_gate) { return _frozen.Keys.ToArray(); } }
    }

    /// <summary>True if the process name is on the never-freeze list (accepts an ".exe" suffix).</summary>
    public static bool IsProtected(string name) => ProtectedNames.Contains(NormalizeName(name));

    /// <summary>
    /// Suspends every running process named <paramref name="name"/> (with or without ".exe") and
    /// records their PIDs so <see cref="Thaw"/> can resume exactly those. Protected processes are
    /// never touched. Idempotent per PID. Returns the number of processes newly suspended.
    /// </summary>
    public int FreezeByName(string name)
    {
        string key = NormalizeName(name);
        if (key.Length == 0) return 0;
        if (IsProtected(key))
        {
            _logger?.LogWarning("Freezer: refusing to freeze protected process '{Name}'.", key);
            return 0;
        }

        int suspended = 0;
        lock (_gate)
        {
            if (!_frozen.TryGetValue(key, out var pids))
                pids = new HashSet<int>();

            foreach (var proc in _lister.ByName(key))
            {
                if (IsProtected(proc.Name)) continue;   // defense in depth
                if (pids.Contains(proc.Pid)) continue;  // already frozen — don't stack suspend counts
                try
                {
                    _suspender.Suspend(proc.Pid);
                    pids.Add(proc.Pid);
                    suspended++;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Freezer: could not suspend pid {Pid} ({Name}).", proc.Pid, proc.Name);
                }
            }

            if (pids.Count > 0) _frozen[key] = pids;
        }

        if (suspended > 0)
            _logger?.LogInformation("Freezer: suspended {Count} process(es) named '{Name}'.", suspended, key);
        return suspended;
    }

    /// <summary>Resumes every process frozen under <paramref name="name"/> and forgets them. Returns how many resumed.</summary>
    public int Thaw(string name)
    {
        string key = NormalizeName(name);
        if (key.Length == 0) return 0;

        int resumed = 0;
        lock (_gate)
        {
            if (!_frozen.TryGetValue(key, out var pids)) return 0;
            foreach (int pid in pids)
            {
                try { _suspender.Resume(pid); resumed++; }
                catch (Exception ex) { _logger?.LogWarning(ex, "Freezer: could not resume pid {Pid}.", pid); }
            }
            _frozen.Remove(key);
        }

        _logger?.LogInformation("Freezer: resumed {Count} process(es) named '{Name}'.", resumed, key);
        return resumed;
    }

    /// <summary>Resumes everything the Freezer has frozen (e.g. on shutdown). Returns how many resumed.</summary>
    public int ThawAll()
    {
        int resumed = 0;
        lock (_gate)
        {
            foreach (int pid in _frozen.Values.SelectMany(s => s))
            {
                try { _suspender.Resume(pid); resumed++; }
                catch (Exception ex) { _logger?.LogWarning(ex, "Freezer: could not resume pid {Pid}.", pid); }
            }
            _frozen.Clear();
        }
        return resumed;
    }

    /// <summary>Trim surrounding whitespace and a trailing ".exe" so names compare consistently.</summary>
    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }
}
