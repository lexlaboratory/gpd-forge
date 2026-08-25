// GPD Forge - foreground app detection (Win32). GPL-3.0-or-later.
// Read-only, no elevation needed. Abstracted so FocusProfileEngine stays unit-testable.
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GpdForge.Profiles;

public interface IForegroundApp
{
    /// <summary>Process name of the foreground window (no extension), or null if unavailable.</summary>
    string? Current();
}

public sealed partial class Win32ForegroundApp : IForegroundApp
{
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public string? Current()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            _ = GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
