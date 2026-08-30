// GPD Forge — VRAM/UMA advisory (read-only). GPL-3.0-or-later.
//
// On an APU the iGPU's "VRAM" is a slice of system RAM (UMA) whose SIZE is a BIOS setting, applied
// at boot (GOP/_DSM) — not something Windows lets user-mode reassign live. GPD Forge reads the
// current allocation via WMI (driverless, no elevation) and is honest about the rest: there is no
// verified-safe, reversible way to WRITE it from here, so this stays advisory-only. Blindly poking
// a vendor-specific registry/ACPI value to change the UMA split risks a black screen or boot
// failure on a device we can't roll back remotely — exactly the kind of "fake success" this project
// refuses to ship. See docs/ROADMAP.md Phase 3.
//
// Phase 3.3 did NOT add a write path, on purpose. It made the advice CONFIRMABLE instead: the
// observed split is persisted with the boot it was seen under (core/Ai/VramHistory.cs), so after the
// user edits the BIOS and reboots, GPD Forge can say "changed from 2048 MB to 3072 MB across the
// reboot at <when>" — a verdict read back from the machine — instead of "we told you to change it,
// hopefully you did". A setting is not applied here until something provokes it and reads the result.
//
// Two further honesty constraints are encoded below rather than glossed over:
//   1. The BIOS menu path is NAMED for this board because it could be sourced (see BiosPathAdvisory),
//      but it carries its provenance and a stop-if-it-does-not-match instruction. Sending someone
//      hunting through firmware for a menu that does not exist is worse than generic advice.
//   2. Win32_VideoController.AdapterRAM is a CIM uint32. It CANNOT represent a UMA split of 4 GiB or
//      more: such a split either saturates just under 4 GiB or wraps around to a small number (a real
//      6 GB split arrives as 2048 MB). Two consequences are encoded here rather than hidden:
//        * EVERY available reading carries FieldWidthCaveat — a below-ceiling number is what Windows
//          reports, not a proven split size. The earlier version declared sub-4-GiB readings exact,
//          which is how a 4 GB -> 6 GB BIOS edit could be read back as a DECREASE to 2048 MB.
//        * At the ceiling the reading cannot distinguish 4 GB from 8 GB or 16 GB, so neither an
//          "unchanged" verdict nor a delta derived from it is presented as fact (see VramHistory).
//      A reading below 1 MB is not a split at all; it is reported as unavailable rather than as
//      "0 MB", so nothing downstream ever stores or compares a zero baseline.
using System.Management;
using Microsoft.Extensions.Logging;

namespace GpdForge.Ai;

/// <summary>Read-only snapshot of the iGPU's current VRAM/UMA allocation, plus an honest advisory.</summary>
/// <param name="AtReportingCeiling">True when the reading sits at the 32-bit AdapterRAM ceiling, i.e.
/// the real allocation is "this much or more" and changes above it are invisible to us.</param>
public readonly record struct VramInfo(
    long ReportedBytes,
    double ReportedMb,
    string? AdapterName,
    bool Available,
    string Advisory,
    bool AtReportingCeiling = false);

/// <summary>Pure logic: turn a raw AdapterRAM byte count into a <see cref="VramInfo"/>. No I/O —
/// unit-tested directly.</summary>
public static class VramAdvisor
{
    /// <summary>AdapterRAM is CIM uint32. Anything at or above 4095 MiB is at (or wrapped around) the
    /// field's ceiling and must not be read as an exact allocation.</summary>
    public const long ReportingCeilingBytes = 4095L * 1024 * 1024;

    /// <summary>The same ceiling in whole megabytes, so a PERSISTED reading (which keeps only MB, see
    /// <see cref="VramObservation"/>) can be tested for it without re-deriving the byte count.</summary>
    public const double ReportingCeilingMb = 4095;

    /// <summary>Smallest reading that can plausibly be a UMA split. A controller answering with a few
    /// hundred KiB is not reporting a frame buffer; rounding it to "0 MB" and treating that as a
    /// reading produced a baseline the history store would then refuse to read back.</summary>
    public const long MinimumUsableBytes = 1024L * 1024;

    /// <summary>True when a megabyte reading sits at (or above) the uint32 ceiling, i.e. it means
    /// "4 GB or more" rather than exactly this much.</summary>
    public static bool IsAtReportingCeilingMb(double mb) => mb >= ReportingCeilingMb;

    public const string RequiresBiosAdvisory =
        "UMA/VRAM size is set by the BIOS at boot (GOP/_DSM) and only changes after a reboot. " +
        "GPD Forge reads the current allocation but will not write it blindly — change it in BIOS " +
        "setup, or wait for a verified, reversible write path for this board.";

    /// <summary>
    /// Named for the GPD Win 4 (G1618-04) because it could actually be sourced: the community Win 4
    /// documentation (github.com/psilli/win4_info) and GPD's own "the way for GPD WIN4 graphic
    /// RAM/VRAM changing" post both give the same path. The provenance and the version caveat travel
    /// WITH the path on purpose — it is documented against the 6800U/8840U-era Win 4 BIOS, and the
    /// user is told to stop rather than improvise if their firmware does not match, because an
    /// invented BIOS menu is worse than no menu at all.
    /// </summary>
    public const string BiosPathAdvisory =
        "On the GPD Win 4 (G1618-04) the setting is: hold DEL during boot, then Advanced > CBS " +
        "(\"AMD CBS\") > NBIO (\"NBIO Common Options\") > GFX Configuration > \"UMA Frame buffer Size\". " +
        "If the size is greyed out, set \"iGPU Configuration\" to UMA_SPECIFIED first, then save and " +
        "exit. Community-documented for the 6800U/8840U-era Win 4 BIOS; newer BIOS builds may label " +
        "these menus differently. If what you see does not match this path, stop and check your BIOS " +
        "version — do not guess at a similar-looking option.";

    /// <summary>
    /// The reason "unchanged" can be a lie near 4 GB. Kept as its own constant so
    /// <see cref="VramHistory"/> can attach it to a change verdict too, not just the live reading.
    /// </summary>
    public const string CeilingCaveat =
        "This reading is at the 32-bit ceiling of WMI's AdapterRAM field (~4 GiB), so it means " +
        "\"4 GB or more\", not exactly this much: a change between a 4 GB and an 8 GB/16 GB split " +
        "may not be visible here at all, and a larger split can even wrap around to a SMALLER " +
        "number. Confirm the split in Task Manager > Performance > GPU (\"Dedicated GPU memory\") " +
        "or dxdiag.";

    /// <summary>
    /// Why even a comfortably-below-4-GiB reading is not a proven split. AdapterRAM is uint32, so a
    /// larger split does not fail loudly — it wraps, and 6 GiB comes back as 2048 MB. Reporting such a
    /// number as exact is what let a successful BIOS INCREASE be read back as a decrease.
    /// </summary>
    public const string FieldWidthCaveat =
        "AdapterRAM is a 32-bit field, so it cannot represent a UMA split of 4 GiB or more: a larger " +
        "split either saturates near 4095 MB or wraps around to a small number. Treat this figure as " +
        "what Windows reports, not as a proven split size — confirm it in Task Manager > Performance " +
        "> GPU (\"Dedicated GPU memory\") or dxdiag.";

    public const string UnavailableAdvisory =
        "Could not read a usable AdapterRAM value from this device (needs a video controller WMI can " +
        "enumerate and report at least 1 MB for). " +
        RequiresBiosAdvisory + " " + BiosPathAdvisory;

    public static VramInfo FromAdapterRam(long? rawBytes, string? adapterName)
    {
        // Below a megabyte there is no split to report. Returning "0 MB, available" here fabricated a
        // reading AND produced a baseline row the history store refuses to read back, so every call
        // rewrote the same unreadable file forever. Unavailable is the honest answer.
        if (rawBytes is not long raw || raw < MinimumUsableBytes)
            return new VramInfo(0, 0, adapterName, false, UnavailableAdvisory);

        double mb = Math.Round(raw / 1024.0 / 1024.0, 0);
        bool ceiling = raw >= ReportingCeilingBytes;
        // The ceiling caveat supersedes the field-width one: it says everything the latter says and
        // adds that THIS reading is already pinned. Never both — repeating it reads as boilerplate.
        string advisory = RequiresBiosAdvisory + " " + BiosPathAdvisory + " " +
            (ceiling ? CeilingCaveat : FieldWidthCaveat);
        return new VramInfo(raw, mb, adapterName, true, advisory, ceiling);
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
