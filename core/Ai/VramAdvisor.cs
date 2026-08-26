// GPD Forge — VRAM/UMA advisory (read-only). GPL-3.0-or-later.
//
// On an APU the iGPU's "VRAM" is a slice of system RAM (UMA) whose SIZE is a BIOS setting, applied
// at boot (GOP/_DSM) — not something Windows lets user-mode reassign live. GPD Forge reads the
// current allocation via WMI (driverless, no elevation) and is honest about the rest: there is no
// verified-safe, reversible way to WRITE it from here, so this stays advisory-only. Blindly poking
// a vendor-specific registry/ACPI value to change the UMA split risks a black screen or boot
// failure on a device we can't roll back remotely — exactly the kind of "fake success" this project
// refuses to ship. See docs/ROADMAP.md Phase 3.
using System.Management;
using Microsoft.Extensions.Logging;

namespace GpdForge.Ai;

/// <summary>Read-only snapshot of the iGPU's current VRAM/UMA allocation, plus an honest advisory.</summary>
public readonly record struct VramInfo(
    long ReportedBytes,
    double ReportedMb,
    string? AdapterName,
    bool Available,
    string Advisory);

/// <summary>Pure logic: turn a raw AdapterRAM byte count into a <see cref="VramInfo"/>. No I/O —
/// unit-tested directly.</summary>
public static class VramAdvisor
{
    public const string RequiresBiosAdvisory =
        "UMA/VRAM size is set by the BIOS at boot (GOP/_DSM) and only changes after a reboot. " +
        "GPD Forge reads the current allocation but will not write it blindly — change it in BIOS " +
        "setup, or wait for a verified, reversible write path for this board.";

    public const string UnavailableAdvisory =
        "Could not read AdapterRAM from this device (needs a video controller WMI can enumerate). " +
        RequiresBiosAdvisory;

    public static VramInfo FromAdapterRam(long? rawBytes, string? adapterName)
    {
        if (rawBytes is not long raw || raw <= 0)
            return new VramInfo(0, 0, adapterName, false, UnavailableAdvisory);

        double mb = Math.Round(raw / 1024.0 / 1024.0, 0);
        return new VramInfo(raw, mb, adapterName, true, RequiresBiosAdvisory);
    }
}

/// <summary>Reads the iGPU's reported VRAM/UMA allocation. Abstracted so callers/tests don't need WMI.</summary>
public interface IVramReader
{
    VramInfo Read();
}

/// <summary>
/// Real reader: WMI <c>Win32_VideoController.AdapterRAM</c> — driverless, no elevation, same trust
/// level as <c>DisplayService</c>/<c>BatteryService</c> in this repo. Read-only; never writes.
/// </summary>
public sealed class WmiVramReader(ILogger<WmiVramReader>? logger = null) : IVramReader
{
    public VramInfo Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            VramInfo? fallback = null;
            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    string? name = mo["Name"] as string;
                    long? raw = ReadAdapterRamBytes(mo["AdapterRAM"]);
                    if (raw is null) continue;

                    var info = VramAdvisor.FromAdapterRam(raw, name);
                    // Prefer the AMD/Radeon iGPU when several controllers are enumerated (e.g. a
                    // "Microsoft Basic Display Adapter" alongside the real one); otherwise keep the
                    // first reading as a fallback so we still report something.
                    bool isAmd = name is not null &&
                        (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase));
                    if (isAmd) return info;
                    fallback ??= info;
                }
            }
            if (fallback is VramInfo f) return f;
        }
        catch (Exception ex) { logger?.LogDebug(ex, "VRAM/UMA read unavailable (Win32_VideoController.AdapterRAM)"); }
        return VramAdvisor.FromAdapterRam(null, null);
    }

    /// <summary>
    /// WMI's CIM uint32 AdapterRAM crosses COM/.NET marshalling inconsistently. This is a documented
    /// Windows limitation: AdapterRAM is a 32-bit field, so adapters reporting ≥2GB can arrive as a
    /// boxed negative Int32 once the top bit is set. Reinterpreting the bits (instead of
    /// Convert.ToUInt32, which throws OverflowException on a negative boxed int) reports the same
    /// wrapped number Windows itself shows elsewhere, rather than crashing. Public + static so it's
    /// unit-testable without WMI.
    /// </summary>
    public static long? ReadAdapterRamBytes(object? value) => value switch
    {
        null => null,
        uint u => u,
        int i => unchecked((uint)i),
        long l => l,
        ulong ul => unchecked((long)ul),
        _ => null,
    };
}
