// GPD Forge — tablet-mode advisory + gated registry toggle. GPL-3.0-or-later.
//
// The Win 4's SMBIOS chassis type reports "Convertible", which makes Windows apply 2-in-1 UI
// behavior (e.g. auto-maximizing every window) even though this board has no hinge/rotation
// sensor. Windows 11 22H2+ ships a documented override for exactly this: the DWORD
// HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl\ConvertibilityEnabled — 0 forces "not
// convertible" (the known community fix for the maximized-windows bug), any other value (or the
// value being absent) leaves Windows' normal chassis-type/DeviceForm detection in charge.
//
// This is a real, documented Windows setting rather than an EC/BIOS write, but it still changes
// system-wide window behavior — not just something local to GPD Forge — so a WRITE only ever
// happens when GPDFORGE_ENABLE_HARDWARE=1. Reads are always free.
using Microsoft.Win32;
using Microsoft.Extensions.Logging;

namespace GpdForge.Display;

/// <summary>Reads/writes the ConvertibilityEnabled registry value. Abstracted so
/// <see cref="TabletModeService"/> is unit-testable with a fake — no real registry access in tests.</summary>
public interface ITabletModeRegistry
{
    /// <summary>The raw DWORD value, or null if it isn't set (or can't be read).</summary>
    int? Read();

    /// <summary>Writes the DWORD value. Returns true on success.</summary>
    bool Write(int value);
}

/// <summary>Real registry access via the documented Win11 22H2+ override (see file header).</summary>
public sealed class WindowsTabletModeRegistry(ILogger<WindowsTabletModeRegistry>? logger = null) : ITabletModeRegistry
{
    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const string ValueName = "ConvertibilityEnabled";

    public int? Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is int i ? i : null;
        }
        catch (Exception ex) { logger?.LogDebug(ex, "ConvertibilityEnabled read unavailable"); return null; }
    }

    public bool Write(int value)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
            if (key is null) return false;
            key.SetValue(ValueName, value, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex) { logger?.LogDebug(ex, "ConvertibilityEnabled write failed"); return false; }
    }
}

/// <summary>Pure advisory text, keyed off the raw registry state — testable without touching the
/// registry.</summary>
public static class TabletModeAdvisor
{
    public const string GateClosedAdvisory =
        "Tablet-mode detection is a system-wide registry value (ConvertibilityEnabled) that changes " +
        "how Windows treats every window on this PC, not just GPD Forge — set " +
        "GPDFORGE_ENABLE_HARDWARE=1 to allow a write. Read-only until then.";

    public const string WriteFailedAdvisory =
        "The registry write did not go through (needs an elevated/SYSTEM process). Nothing changed.";

    /// <summary>true = Windows treats this device as convertible/tablet-capable, false = the fix is
    /// applied, null = the value isn't set (default OS chassis-type detection applies).</summary>
    public static bool? ToConvertible(int? raw) => raw is null ? null : raw != 0;

    public static string Describe(int? raw) => raw switch
    {
        null => "ConvertibilityEnabled is not set — Windows falls back to chassis-type/DeviceForm " +
                "detection (the source of the Win 4's known 'everything opens maximized' behavior).",
        0 => "ConvertibilityEnabled = 0 — Windows is told this is NOT convertible (the known fix).",
        _ => $"ConvertibilityEnabled = {raw} — Windows treats this as convertible/tablet-capable.",
    };
}

/// <summary>Response shape both GET and POST /display/tablet share.</summary>
public readonly record struct TabletModeStatus(bool? Convertible, int? Raw, bool Applied, string Advisory);

public sealed class TabletModeService(ITabletModeRegistry registry, bool hardwareGateOpen, ILogger<TabletModeService>? logger = null)
{
    public TabletModeStatus Get()
    {
        int? raw = registry.Read();
        return new TabletModeStatus(TabletModeAdvisor.ToConvertible(raw), raw, Applied: false, TabletModeAdvisor.Describe(raw));
    }

    /// <summary>enable=true writes 1 (convertible/tablet-capable); enable=false writes 0 (the known
    /// fix). Never touches the registry unless the hardware gate is open.</summary>
    public TabletModeStatus Set(bool enable)
    {
        if (!hardwareGateOpen)
        {
            int? raw = registry.Read();
            return new TabletModeStatus(TabletModeAdvisor.ToConvertible(raw), raw, Applied: false, TabletModeAdvisor.GateClosedAdvisory);
        }

        bool wrote = registry.Write(enable ? 1 : 0);
        int? after = registry.Read();
        if (!wrote)
        {
            logger?.LogWarning("TabletModeService: ConvertibilityEnabled write failed.");
            return new TabletModeStatus(TabletModeAdvisor.ToConvertible(after), after, Applied: false, TabletModeAdvisor.WriteFailedAdvisory);
        }
        return new TabletModeStatus(TabletModeAdvisor.ToConvertible(after), after, Applied: true, TabletModeAdvisor.Describe(after));
    }
}
