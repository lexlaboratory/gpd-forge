// GPD Forge — talking to AMD's ADLX from C#, without a native shim. GPL-3.0-or-later.
//
// ADLX is AMD's official library for the Radeon settings that Adrenalin exposes — Anti-Lag, Chill,
// Boost, Image Sharpening — plus per-application profiles. It is the only supported way to reach
// them; the legacy ADL library in atiadlxx.dll exports 1367 flat C functions but none of those
// features (measured on this device, 2026-08-29).
//
// AMD's documented route from C# is SWIG plus a C++ compiler, producing a native binding DLL. That
// is rejected here for a concrete, local reason: an unsigned native DLL is exactly what Smart App
// Control blocks on this machine, repeatedly and non-deterministically. It has blocked cargo
// build-scripts and freshly built executables six times in one day. Shipping a component whose
// loading is a coin flip, into a daemon that manages power, is not acceptable.
//
// So this calls ADLX's C interface directly. ADLX is COM-shaped: `ADLXInitialize` hands back a
// pointer to an IADLXSystem whose first field is a pointer to a vtable of function pointers, and
// every method is invoked by index into that table. .NET can do this with
// Marshal.GetDelegateForFunctionPointer and no native code of our own.
//
// THE RISK, AND HOW IT IS CONTAINED. A wrong slot index does not fail cleanly — it calls an
// arbitrary function pointer inside a graphics driver. That would be an unacceptable thing to guess
// at, so the layout is not guessed: it is transcribed from the ADLX SDK's own IADLXSystemVtbl, and
// before this class reports itself usable it CALLS a method whose answer can be checked against a
// fact obtained another way. TotalSystemRAM (slot 10) returns the machine's RAM in MB; if the vtable
// were misaligned, that number would be garbage. Verifying it turns "the layout is probably right"
// into "the layout produced the correct answer on this machine, just now". If the canary disagrees,
// ADLX is reported unavailable and no further call is made.
//
// Everything here is also behind GPDFORGE_ENABLE_GPU_PROFILES, separate from the hardware gate on
// purpose: this is a user-mode driver API, unrelated to the MSR/EC paths, and a fault here must not
// be able to take down power control that has been validated on the metal.
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GpdForge.Gpu;

/// <summary>Why ADLX is or is not usable. Never a bare bool: "not installed" and "the vtable did not
/// behave" are different facts, and the second one is a reason to stop trusting the library.</summary>
public enum AdlxStatus
{
    /// <summary>Not attempted (the feature gate is closed).</summary>
    Disabled,
    /// <summary>amdadlx64.dll is not present — no AMD driver, or not a Radeon system.</summary>
    NotInstalled,
    /// <summary>The library is present but initialisation failed.</summary>
    InitFailed,
    /// <summary>Initialised, but the vtable canary returned an implausible answer. Treated as unusable.</summary>
    LayoutMismatch,
    /// <summary>Initialised and verified.</summary>
    Ready,
}

/// <summary>What a probe found. <paramref name="Version"/> is null when it could not be read.</summary>
public sealed record AdlxProbe(AdlxStatus Status, string? Version, string Detail);

/// <summary>
/// Owns the ADLX handle. Deliberately does nothing beyond initialise, verify and terminate — the
/// 3D-settings interfaces are layered on top only once this foundation is proven on real hardware.
/// </summary>
public sealed partial class AdlxInterop : IDisposable
{
    // Only these are flat exports; amdadlx64.dll exports exactly 9 symbols and the rest of the API
    // lives behind vtables (verified by reading the PE export table on this device).
    private const string Dll = "amdadlx64.dll";

    [LibraryImport(Dll)]
    private static partial int ADLXQueryFullVersion(out ulong fullVersion);

    [LibraryImport(Dll)]
    private static partial int ADLXInitialize(ulong version, out IntPtr ppSystem);

    [LibraryImport(Dll)]
    private static partial int ADLXTerminate();

    private const int ADLX_OK = 0;

    /// <summary>
    /// Slot indices into IADLXSystemVtbl, transcribed from the SDK header — NOT inferred. The order
    /// is load-bearing: calling the wrong index calls the wrong driver function.
    ///   0 GetHybridGraphicsType   1 GetGPUs                        2 QueryInterface
    ///   3 GetDisplaysServices     4 GetDesktopsServices            5 GetGPUsChangedHandling
    ///   6 EnableLog               7 Get3DSettingsServices          8 GetGPUTuningServices
    ///   9 GetPerformanceMonitoringServices                        10 TotalSystemRAM
    ///  11 GetI2C
    /// </summary>
    private const int SlotGet3DSettingsServices = 7;
    private const int SlotTotalSystemRAM = 10;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OutUIntFn(IntPtr pThis, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OutPtrFn(IntPtr pThis, out IntPtr value);

    private IntPtr _system;
    private bool _initialised;
    private readonly ILogger? _logger;

    public AdlxInterop(ILogger? logger = null) => _logger = logger;

    /// <summary>The IADLXSystem pointer, or IntPtr.Zero when not initialised. For layers above.</summary>
    public IntPtr System => _system;

    /// <summary>Reads a function pointer out of an object's vtable. `pThis` points at the object, whose
    /// first field is the vtable pointer; slots are pointer-sized entries from there.</summary>
    internal static IntPtr VtableSlot(IntPtr pThis, int slot)
    {
        var vtbl = Marshal.ReadIntPtr(pThis);
        return Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
    }

    /// <summary>Whether the library is even present, without loading it.</summary>
    public static bool LibraryPresent()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return File.Exists(Path.Combine(system32, Dll));
    }

    /// <summary>
    /// Initialise ADLX and verify the vtable against a known-good fact. Never throws; a failure is a
    /// status, because "the graphics driver API misbehaved" is a thing to report, not to crash on.
    /// </summary>
    /// <param name="expectedRamMb">The machine's RAM in MB, obtained independently (WMI). The canary
    /// is compared against this; pass 0 to skip the comparison (then only implausible values are
    /// rejected, which is weaker and is logged as such).</param>
    public AdlxProbe Initialise(uint expectedRamMb = 0)
    {
        if (!LibraryPresent())
            return new AdlxProbe(AdlxStatus.NotInstalled, null,
                $"{Dll} is not in System32 — this is not a machine with an AMD Radeon driver.");

        ulong fullVersion;
        string? versionText = null;
        try
        {
            if (ADLXQueryFullVersion(out fullVersion) != ADLX_OK)
                return new AdlxProbe(AdlxStatus.InitFailed, null, "ADLXQueryFullVersion did not succeed.");

            // Packed as four 16-bit fields, most significant first.
            versionText = $"{(fullVersion >> 48) & 0xFFFF}.{(fullVersion >> 32) & 0xFFFF}." +
                          $"{(fullVersion >> 16) & 0xFFFF}.{fullVersion & 0xFFFF}";

            // Initialise with the version the INSTALLED driver reports rather than one compiled in.
            // We ship no ADLX SDK of our own, so claiming a version we were built against would be a
            // number with nothing behind it.
            if (ADLXInitialize(fullVersion, out _system) != ADLX_OK || _system == IntPtr.Zero)
                return new AdlxProbe(AdlxStatus.InitFailed, versionText, "ADLXInitialize did not return a system interface.");

            _initialised = true;
        }
        catch (DllNotFoundException e)
        {
            return new AdlxProbe(AdlxStatus.NotInstalled, null, $"{Dll} could not be loaded: {e.Message}");
        }
        catch (EntryPointNotFoundException e)
        {
            return new AdlxProbe(AdlxStatus.InitFailed, null, $"{Dll} is present but lacks an expected export: {e.Message}");
        }

        // --- the canary -------------------------------------------------------------------------
        // Calling through the vtable for the first time, at a slot whose answer we can check.
        uint ramMb;
        try
        {
            var fn = Marshal.GetDelegateForFunctionPointer<OutUIntFn>(VtableSlot(_system, SlotTotalSystemRAM));
            if (fn(_system, out ramMb) != ADLX_OK)
            {
                Terminate();
                return new AdlxProbe(AdlxStatus.LayoutMismatch, versionText,
                    "TotalSystemRAM failed through the vtable — the interface layout does not match this driver.");
            }
        }
        catch (Exception e)
        {
            // A misaligned vtable can surface as almost anything, including an access violation that
            // .NET surfaces as a SEHException. Whatever it is, ADLX is not usable.
            Terminate();
            return new AdlxProbe(AdlxStatus.LayoutMismatch, versionText,
                $"Calling through the ADLX vtable threw ({e.GetType().Name}) — layout mismatch, refusing to use it.");
        }

        if (!IsPlausibleRam(ramMb, expectedRamMb, out var why))
        {
            Terminate();
            return new AdlxProbe(AdlxStatus.LayoutMismatch, versionText, why);
        }

        _logger?.LogInformation("ADLX {Version} ready (vtable verified: TotalSystemRAM = {Ram} MB).", versionText, ramMb);
        return new AdlxProbe(AdlxStatus.Ready, versionText, $"Verified against TotalSystemRAM = {ramMb} MB.");
    }

    /// <summary>
    /// Whether the canary's answer is credible. Split out and public so the judgement is testable
    /// without ADLX: this predicate is the whole difference between trusting the vtable and hoping.
    /// </summary>
    public static bool IsPlausibleRam(uint reportedMb, uint expectedMb, out string why)
    {
        // Independent of any expectation: no real machine reports 0, and 4 TB is far past a handheld.
        if (reportedMb < 512 || reportedMb > 4_194_304)
        {
            why = $"TotalSystemRAM returned {reportedMb} MB, which is not a real amount of memory — " +
                  "the vtable is misaligned. Refusing to call anything else through it.";
            return false;
        }

        if (expectedMb > 0)
        {
            // ADLX and WMI round differently (and firmware reserves a slice), so require the same
            // ballpark rather than equality. A misaligned slot would be wrong by orders of magnitude,
            // not by a few percent.
            var ratio = (double)reportedMb / expectedMb;
            if (ratio is < 0.75 or > 1.25)
            {
                why = $"TotalSystemRAM returned {reportedMb} MB but the system reports {expectedMb} MB — " +
                      "these do not describe the same machine, so the vtable layout is wrong.";
                return false;
            }
        }

        why = string.Empty;
        return true;
    }

    /// <summary>The 3D settings services interface, or IntPtr.Zero. Only valid after a Ready probe.</summary>
    public IntPtr Get3DSettingsServices()
    {
        if (!_initialised || _system == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var fn = Marshal.GetDelegateForFunctionPointer<OutPtrFn>(VtableSlot(_system, SlotGet3DSettingsServices));
            return fn(_system, out var services) == ADLX_OK ? services : IntPtr.Zero;
        }
        catch (Exception e)
        {
            _logger?.LogWarning(e, "ADLX Get3DSettingsServices failed.");
            return IntPtr.Zero;
        }
    }

    private void Terminate()
    {
        if (!_initialised) return;
        try { ADLXTerminate(); } catch { /* tearing down a driver library that already failed */ }
        _initialised = false;
        _system = IntPtr.Zero;
    }

    public void Dispose() => Terminate();
}
