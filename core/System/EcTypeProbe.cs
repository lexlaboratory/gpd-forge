// GPD Forge — the LHM metadata probe, kept OUT of Main on purpose. GPL-3.0-or-later.
//
// This body used to sit inline in Program.cs. That was a latent way to make the daemon unstartable.
//
// `typeof(LibreHardwareMonitor.Hardware.Computer)` inside Main means the JIT resolves that assembly
// when it compiles Main — before a single statement runs, and regardless of which argument was
// passed or whether the hardware gate is even open. On 2026-08-29 Smart App Control blocked
// LibreHardwareMonitorLib.dll (0x800711C7) in both Debug and Release, and every entry point died at
// startup with a FileLoadException, including the read-only probes that have nothing to do with it.
// The driverless WMI telemetry path, which is the DEFAULT and needs none of this, went down with it.
//
// Moving the reference into a separate class defers the load to the moment this method is actually
// called. LHM is an OPTIONAL enrichment; a machine that refuses to load it should lose the optional
// sensors, not the daemon.
using System.Reflection;

namespace GpdForge.SystemControl;

public static class EcTypeProbe
{
    /// <summary>
    /// Prints the PawnIO/EC-related types and resources the bundled LibreHardwareMonitorLib exposes.
    /// Metadata only — loads no driver and needs no elevation.
    /// </summary>
    public static void Run()
    {
        Assembly asm;
        try
        {
            asm = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
        }
        catch (Exception e) when (e is FileLoadException or FileNotFoundException or BadImageFormatException)
        {
            // Say which failure this is. "Blocked by policy" and "not deployed" look identical from a
            // stack trace and lead to completely different fixes.
            Console.WriteLine($"LibreHardwareMonitorLib could not be loaded: {e.Message}");
            Console.WriteLine("If this is 0x800711C7 it is Smart App Control refusing the DLL, not a missing file.");
            return;
        }

        Console.WriteLine($"LHM assembly: {asm.GetName().Name} {asm.GetName().Version}  ({asm.Location})");
        Type?[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }

        Console.WriteLine("Types matching Pawn/Lpc/Ring0/Kernel/Ec:");
        foreach (var t in types.Where(t => t?.FullName is string f &&
            (f.Contains("Pawn") || f.Contains("Lpc") || f.Contains("Ring0") || f.Contains("Kernel")
             || f.Contains(".EmbeddedController") || f.Contains(".Ec"))))
            Console.WriteLine($"  {t!.FullName}");

        Console.WriteLine("Resources matching Pawn/Lpc/.bin:");
        foreach (var r in asm.GetManifestResourceNames().Where(r =>
            r.Contains("Pawn") || r.Contains("Lpc") || r.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"  {r}");
    }
}
