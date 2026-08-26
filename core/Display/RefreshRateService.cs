// GPD Forge — display refresh-rate switching (REAL, via Win32 display-mode APIs). GPL-3.0-or-later.
//
// Enumerates and switches the primary display's refresh rate through EnumDisplaySettingsEx /
// ChangeDisplaySettingsEx — the same OS-level mechanism Windows' own Display Settings panel uses.
// No EC, no BIOS, no driver: a standard, reversible mode change, applied for the current session
// only (dwFlags=0, never written to the registry), so a bad pick never survives a reboot.
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GpdForge.Display;

/// <summary>Current + supported refresh rates (Hz) for the primary display.</summary>
public readonly record struct RefreshRateInfo(int CurrentHz, IReadOnlyList<int> SupportedHz);

/// <summary>One enumerated display mode, reduced to the fields <see cref="DisplayModeParser"/> needs.
/// Kept separate from the P/Invoke struct so the filtering logic is plain data — no Win32 dependency,
/// trivially unit-testable.</summary>
public readonly record struct RawDisplayMode(int Width, int Height, int BitsPerPel, int Hz);

/// <summary>Pure filtering: which Hz values are available without also changing resolution/color
/// depth. No I/O — unit-tested directly.</summary>
public static class DisplayModeParser
{
    /// <summary>Distinct Hz values across <paramref name="modes"/> that share the given resolution and
    /// bit depth (so switching Hz never silently changes anything else), sorted ascending. Modes
    /// reporting 0 or 1 Hz ("use the display hardware's default", per Win32) are excluded — they are
    /// not a real selectable rate.</summary>
    public static IReadOnlyList<int> SupportedHz(IEnumerable<RawDisplayMode> modes, int width, int height, int bitsPerPel) =>
        modes.Where(m => m.Width == width && m.Height == height && m.BitsPerPel == bitsPerPel && m.Hz > 1)
             .Select(m => m.Hz)
             .Distinct()
             .OrderBy(hz => hz)
             .ToArray();
}

/// <summary>Pure validation for a requested refresh rate against the supported list. No I/O —
/// unit-tested directly.</summary>
public static class RefreshRatePicker
{
    public static (bool Ok, string? Error) Validate(int hz, IReadOnlyList<int> supported)
    {
        if (supported.Contains(hz)) return (true, null);
        string list = supported.Count == 0 ? "(none detected)" : string.Join(", ", supported);
        return (false, $"{hz} Hz is not supported on this display (supported: {list})");
    }
}

/// <summary>Win32 display-mode access, abstracted so <see cref="RefreshRateService"/> is unit-testable
/// with a fake — no P/Invoke in tests.</summary>
public interface IDisplayModeSource
{
    /// <summary>The primary display's current mode + every Hz it supports at that resolution/depth.</summary>
    RefreshRateInfo Read();

    /// <summary>Switch the primary display to <paramref name="hz"/> for this session (not persisted to
    /// the registry). Returns true if Windows accepted the mode change.</summary>
    bool Apply(int hz);
}

/// <summary>
/// Real source: P/Invoke EnumDisplaySettingsExW (read) / ChangeDisplaySettingsExW (write). DEVMODE
/// is declared below with fixed-size char buffers (not <c>string</c> + <c>[MarshalAs(ByValTStr)]</c>)
/// so the whole struct is blittable — that lets LibraryImport's source generator marshal it directly,
/// with no hand-written custom marshaller, matching this repo's LibraryImport-only P/Invoke style.
/// </summary>
public sealed partial class Win32DisplayModeSource(ILogger<Win32DisplayModeSource>? logger = null) : IDisplayModeSource
{
    private const uint ENUM_CURRENT_SETTINGS = unchecked((uint)-1);
    private const uint DM_DISPLAYFREQUENCY = 0x00400000;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int EnumDisplaySettingsExW(string? lpszDeviceName, uint iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ChangeDisplaySettingsExW(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    public RefreshRateInfo Read()
    {
        try
        {
            var current = NewDevMode();
            if (EnumDisplaySettingsExW(null, ENUM_CURRENT_SETTINGS, ref current, 0) == 0)
                return new RefreshRateInfo(0, Array.Empty<int>());

            var modes = new List<RawDisplayMode>();
            for (uint i = 0; ; i++)
            {
                var dm = NewDevMode();
                if (EnumDisplaySettingsExW(null, i, ref dm, 0) == 0) break;
                modes.Add(new RawDisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmBitsPerPel, (int)dm.dmDisplayFrequency));
            }

            var supported = DisplayModeParser.SupportedHz(modes, (int)current.dmPelsWidth, (int)current.dmPelsHeight, (int)current.dmBitsPerPel);
            return new RefreshRateInfo((int)current.dmDisplayFrequency, supported);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "refresh-rate read unavailable");
            return new RefreshRateInfo(0, Array.Empty<int>());
        }
    }

    public bool Apply(int hz)
    {
        try
        {
            var dm = NewDevMode();
            if (EnumDisplaySettingsExW(null, ENUM_CURRENT_SETTINGS, ref dm, 0) == 0) return false;
            dm.dmDisplayFrequency = (uint)hz;
            dm.dmFields = DM_DISPLAYFREQUENCY;
            // dwflags=0: applies for this session only (NOT written to the registry), so a bad pick
            // never survives a reboot — unlike a persisted CDS_UPDATEREGISTRY change.
            return ChangeDisplaySettingsExW(null, ref dm, IntPtr.Zero, 0, IntPtr.Zero) == 0 /* DISP_CHANGE_SUCCESSFUL */;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "refresh-rate set failed for {Hz}Hz", hz);
            return false;
        }
    }

    private static DEVMODE NewDevMode()
    {
        var dm = default(DEVMODE);
        dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
        return dm;
    }
}

/// <summary>
/// Blittable projection of Win32's DEVMODEW. Field order/sizes mirror DEVMODEW exactly (verified
/// against the documented 220-byte Unicode size), so the native side reads/writes the same offsets
/// regardless of this C# projection using fixed buffers instead of MarshalAs strings.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct DEVMODE
{
    public fixed char dmDeviceName[32];
    public ushort dmSpecVersion;
    public ushort dmDriverVersion;
    public ushort dmSize;
    public ushort dmDriverExtra;
    public uint dmFields;
    public int dmPositionX;
    public int dmPositionY;
    public uint dmDisplayOrientation;
    public uint dmDisplayFixedOutput;
    public short dmColor;
    public short dmDuplex;
    public short dmYResolution;
    public short dmTTOption;
    public short dmCollate;
    public fixed char dmFormName[32];
    public ushort dmLogPixels;
    public uint dmBitsPerPel;
    public uint dmPelsWidth;
    public uint dmPelsHeight;
    public uint dmDisplayFlags;
    public uint dmDisplayFrequency;
    public uint dmICMMethod;
    public uint dmICMIntent;
    public uint dmMediaType;
    public uint dmDitherType;
    public uint dmReserved1;
    public uint dmReserved2;
    public uint dmPanningWidth;
    public uint dmPanningHeight;
}

/// <summary>Validates + applies a requested refresh rate against what the display actually supports.</summary>
public sealed class RefreshRateService(IDisplayModeSource source, ILogger<RefreshRateService>? logger = null)
{
    public RefreshRateInfo GetInfo() => source.Read();

    /// <summary>Applies <paramref name="hz"/> if it's in the supported list; otherwise leaves the
    /// display untouched and returns the current info plus an error. Never calls into Win32 with an
    /// unvalidated value — <see cref="RefreshRatePicker"/> gates it first.</summary>
    public (RefreshRateInfo Info, string? Error) SetHz(int hz)
    {
        var info = source.Read();
        var (ok, error) = RefreshRatePicker.Validate(hz, info.SupportedHz);
        if (!ok)
        {
            logger?.LogDebug("refresh-rate rejected: {Error}", error);
            return (info, error);
        }
        if (!source.Apply(hz))
            return (info, $"Windows rejected the switch to {hz} Hz.");
        return (source.Read(), null);
    }
}
