// GPD Forge — reading the controller's config blob from Windows. GPL-3.0-or-later.
//
// READ ONLY, on purpose and for now. SafeConfigWriter has had a correct backup/patch/verify/restore
// layer since it was written, and no implementation of IHidConfigDevice to run it against — so
// nothing has ever been written to this pad by GPD Forge. That stays true here.
//
// The reason is in SafeConfigWriter's own header: a bad write can brick the pad. And the offsets the
// writer would patch (`ConfigOffsets`) are explicitly PLACEHOLDERS — L4 at 0x000, R4 at 0x001,
// deadzone at 0x010 — numbers that were never confirmed against hardware. Writing a guessed offset
// into a controller's EC ROM is not a bug to discover in the field; it is a pad that stops working.
//
// So this class implements the half that cannot do harm. With it, the offsets can be established
// empirically and safely:
//
//   1. `--probe-hid-dump before.bin`
//   2. change ONE setting in GPD's own WinControls (say, remap L4)
//   3. `--probe-hid-dump after.bin`
//   4. `--probe-hid-diff before.bin after.bin`  -> the bytes that moved ARE the offset
//
// Nothing in that loop writes through GPD Forge. WinControls does the writing, using the vendor's own
// protocol, and we only observe the result — which is the difference between confirming an offset and
// betting on one.
//
// SetConfig deliberately throws. An unimplemented write that silently did nothing would be worse
// than one that fails loudly: SafeConfigWriter's verify step would then compare a blob against
// itself and report success for a write that never happened.
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GpdForge.Hid;

public sealed partial class WindowsHidConfigDevice : IHidConfigDevice, IDisposable
{
    // Confirmed on this device 2026-08-29 (VID_2F24 & PID_0135, seven PnP nodes).
    public const ushort VendorId = 0x2F24;
    public const ushort ProductId = 0x0135;

    /// <summary>The vendor protocol's blob size, from GPD-LinuxControls/pyWinControls.</summary>
    public const int ConfigSize = SafeConfigWriter.ConfigSize;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("hid.dll", EntryPoint = "HidD_GetFeature", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetFeature(SafeFileHandle device, byte[] buffer, int bufferLength);

    // The device states its own feature-report size. Asking for a fixed 1024 is a guess, and a
    // mismatched length is one of the ways HidD_GetFeature fails without saying why.
    [LibraryImport("hid.dll", EntryPoint = "HidD_GetPreparsedData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

    [LibraryImport("hid.dll", EntryPoint = "HidD_FreePreparsedData")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_FreePreparsedData(IntPtr preparsedData);

    // DllImport rather than LibraryImport: HIDP_CAPS contains a fixed-size array, which the
    // source-generated marshaller refuses (SYSLIB1051). The classic marshaller handles it, and this
    // is a metadata read on a struct whose layout is fixed by the OS.
    [DllImport("hid.dll", EntryPoint = "HidP_GetCaps")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps caps);

    /// <summary>HIDP_CAPS. Only the three report lengths are used; the rest is layout padding that
    /// must still be present for the struct to be the size the API expects.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    /// <summary>The device's own feature-report length, or null when it cannot be read.</summary>
    public int? FeatureReportLength()
    {
        if (!HidD_GetPreparsedData(_handle, out var pre) || pre == IntPtr.Zero) return null;
        try
        {
            // HIDP_STATUS_SUCCESS is 0x00110000.
            return HidP_GetCaps(pre, out var caps) == 0x00110000 ? caps.FeatureReportByteLength : null;
        }
        finally { HidD_FreePreparsedData(pre); }
    }

    private readonly SafeFileHandle _handle;

    /// <summary>The device path this instance opened. Useful in a probe's output — "which of the
    /// seven nodes answered" is the first question when a read returns nothing.</summary>
    public string DevicePath { get; }

    private WindowsHidConfigDevice(SafeFileHandle handle, string path)
    {
        _handle = handle;
        DevicePath = path;
    }

    /// <summary>
    /// Opens the controller's HID interface, or returns null with the reason. Never throws for the
    /// ordinary "not connected" case, which is not an error worth a stack trace.
    /// </summary>
    public static WindowsHidConfigDevice? TryOpen(IEnumerable<string> candidatePaths, out string detail)
    {
        var tried = 0;
        foreach (var path in candidatePaths)
        {
            tried++;

            // Two attempts, and the second is the one that usually works. Windows holds the pad's
            // input interfaces open, so CreateFile with GENERIC_READ|GENERIC_WRITE is refused on
            // exactly the nodes most likely to matter. Requesting ZERO access still permits
            // HidD_GetPreparsedData and feature-report exchange — it is the documented way to talk to
            // a HID device someone else already owns. Measured here 2026-08-30: MI_00 and MI_01 both
            // refused read/write access outright.
            foreach (var access in new[] { GENERIC_READ | GENERIC_WRITE, 0u })
            {
                var handle = CreateFile(path, access,
                    FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                if (handle.IsInvalid) { handle.Dispose(); continue; }

                detail = access == 0 ? $"opened {path} (zero-access)" : $"opened {path}";
                return new WindowsHidConfigDevice(handle, path);
            }
        }

        detail = tried == 0
            ? "No candidate HID interface paths were supplied."
            : $"None of the {tried} candidate HID interface(s) could be opened.";
        return null;
    }

    /// <summary>
    /// Reads the 1024-byte config blob. Report id 0 is prepended per the HID feature-report
    /// convention; the returned array is the blob itself, with that byte stripped.
    /// </summary>
    public byte[] GetConfig()
    {
        // Ask the DEVICE how long its feature report is rather than assuming the vendor protocol's
        // 1024. A wrong length is one of the ways HidD_GetFeature fails with nothing useful to say.
        var length = FeatureReportLength();
        if (length is null or 0)
            throw new HidVerifyException(
                $"{DevicePath} reports no feature reports at all — this interface does not carry the config blob.");

        var buffer = new byte[length.Value];
        buffer[0] = 0;   // report id

        if (!HidD_GetFeature(_handle, buffer, buffer.Length))
            throw new HidVerifyException(
                $"HidD_GetFeature failed on {DevicePath} (Win32 {Marshal.GetLastWin32Error()}, "
                + $"feature report length {length}). This interface may not be the one carrying the config blob.");

        // Strip the report id. The remainder is the payload, whatever length the device chose — NOT
        // forced to 1024, because claiming a size the device did not report would be inventing one.
        return buffer.AsSpan(1).ToArray();
    }

    /// <summary>
    /// NOT IMPLEMENTED, deliberately. See the header: the offsets this would patch are unconfirmed
    /// placeholders, and a guessed write into a controller's EC ROM can brick the pad. Throwing keeps
    /// SafeConfigWriter honest — a no-op write would make its read-back compare a blob against itself
    /// and report success for something that never happened.
    /// </summary>
    public void SetConfig(byte[] blob) => throw new NotSupportedException(
        "Writing the controller config is not implemented. The byte offsets in ConfigOffsets are "
        + "unconfirmed placeholders; establish them with --probe-hid-dump / --probe-hid-diff first.");

    public void Dispose() => _handle.Dispose();
}
