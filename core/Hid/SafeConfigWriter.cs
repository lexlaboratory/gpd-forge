// GPD Forge - HID controller config, written safely. GPL-3.0-or-later.
//
// The GPD gamepad's remap/deadzone config is a 1024-byte blob written into the controller's EC ROM
// via HID feature reports - a bad write can brick the pad. This layer enforces the golden rule:
// read -> BACK UP -> patch -> write -> read back -> verify equal, and RESTORE the backup on any
// mismatch. Protocol facts (VID 0x2F24 / PID 0x0135, 1024-byte blob) from GPD-LinuxControls/pyWinControls.

namespace GpdForge.Hid;

/// <summary>Reads/writes the controller's 1024-byte config blob (HID GET/SET_FEATURE). Injected for tests.</summary>
public interface IHidConfigDevice
{
    byte[] GetConfig();
    void SetConfig(byte[] blob);
}

public sealed class HidVerifyException(string message) : Exception(message);

public sealed class SafeConfigWriter(IHidConfigDevice device)
{
    public const int ConfigSize = 1024;

    /// <summary>The last backup taken (so a caller can offer an undo).</summary>
    public byte[]? LastBackup { get; private set; }

    /// <summary>
    /// Apply a patch to the config with backup + read-back verification. Throws (after restoring the
    /// backup) if the device does not read back exactly what we wrote - never leaving the pad in an
    /// unknown state.
    /// </summary>
    public void Apply(Action<byte[]> patch)
    {
        byte[] current = device.GetConfig();
        if (current.Length != ConfigSize)
            throw new HidVerifyException($"unexpected config size {current.Length} (want {ConfigSize})");

        byte[] backup = (byte[])current.Clone();
        LastBackup = backup;

        byte[] patched = (byte[])current.Clone();
        patch(patched);

        device.SetConfig(patched);
        byte[] readback = device.GetConfig();

        if (!readback.AsSpan().SequenceEqual(patched))
        {
            device.SetConfig(backup);   // restore known-good
            throw new HidVerifyException("config read-back did not match; restored backup");
        }
    }
}

/// <summary>
/// Byte offsets into the 1024-byte config blob. PLACEHOLDERS - must be confirmed against
/// GPD-LinuxControls/pyWinControls and on-device before writing. Reads are safe; writes stay gated.
/// </summary>
public static class GpdButtonMap
{
    public const ushort Vid = 0x2F24;
    public const ushort Pid = 0x0135;   // verify on HX370 Win 4

    // TODO(verify): real offsets for back buttons L4/R4, stick deadzone (-10..10), rumble intensity.
    public const int L4KeyOffset = 0x000; // placeholder
    public const int R4KeyOffset = 0x001; // placeholder
    public const int LeftDeadzoneOffset = 0x010; // placeholder
    public const int RumbleOffset = 0x020; // placeholder
}
