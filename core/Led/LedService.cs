// GPD Forge — LED/RGB service: gated write attempt + honest advisory. GPL-3.0-or-later.
//
// The Win 4's LED sits behind the same HID feature-report config interface as the controller's
// button/deadzone blob (see core/Hid/SafeConfigWriter.cs) — and on THIS unit that interface is
// already known broken: gpdconfig/pyWinControls fails the very first HidD_SetFeature call with
// "(0x1) Incorrect function" (see docs/overlay-home-button.md). So even with the hardware gate
// open, GPD Forge does not pretend a write can succeed: HidLedWriter is real plumbing (it is what a
// working write would go through, and does compute the real mode/color encoding) but honestly
// reports failure rather than guessing at a protocol nobody has verified on this firmware. The
// desired mode/color is still remembered so GET /led round-trips what the user asked for.
using Microsoft.Extensions.Logging;

namespace GpdForge.Led;

/// <summary>Attempts to push a mode/color to the LED controller. Abstracted so
/// <see cref="LedService"/> is unit-testable with a fake — no real HID access in tests.</summary>
public interface ILedHidWriter
{
    bool TryWrite(LedMode mode, LedColor color);
}

/// <summary>The real attempt. Always reports failure on this board — see the file header. Kept as
/// an honest "yes, we tried" implementation rather than a silent no-op, so the day this board (or a
/// different GPD model) accepts the write, only this method needs to change.</summary>
public sealed class HidLedWriter(ILogger<HidLedWriter>? logger = null) : ILedHidWriter
{
    public bool TryWrite(LedMode mode, LedColor color)
    {
        logger?.LogDebug(
            "LED HID write attempted (mode={Mode} byte=0x{ModeByte:X2}, color={Color} rgb888=0x{Rgb:X6}) — " +
            "no verified write path on this board's firmware (HidD_SetFeature fails); not applied.",
            mode, mode.ToByte(), color.ToHex(), color.ToRgb888());
        return false;
    }
}

/// <summary>Response shape both GET and POST /led share.</summary>
public readonly record struct LedStatus(string Mode, string Color, bool Controllable, bool Applied, string Advisory);

public static class LedAdvisor
{
    public const string GateClosedAdvisory =
        "LED is on the EC/HID config interface, which this HX370's firmware does not accept writes " +
        "on yet — set GPDFORGE_ENABLE_HARDWARE=1 to attempt a write. This is the stored desired " +
        "config, not a live read, until then.";

    public const string WriteFailedAdvisory =
        "LED is on the EC/HID config interface, which this HX370's firmware does not accept writes " +
        "on yet (the same HidD_SetFeature path the controller's own config write already fails on). " +
        "The desired config is stored so the UI still round-trips, but nothing was sent to the device.";
}

/// <summary>Holds the desired LED config and decides whether/how to try applying it. There is no
/// known readable live state, so GET always reflects the last POST (or the default) rather than a
/// real read-back.</summary>
public sealed class LedService(ILedHidWriter writer, bool hardwareGateOpen, ILogger<LedService>? logger = null)
{
    private LedConfig _desired = LedConfig.Default;

    public LedStatus Get() => Status(applied: false,
        hardwareGateOpen ? LedAdvisor.WriteFailedAdvisory : LedAdvisor.GateClosedAdvisory);

    /// <summary>Stores the desired mode/color and, only when the hardware gate is open, attempts a
    /// write through <see cref="ILedHidWriter"/>. Never fakes success: applied is only ever true if
    /// the writer itself reports success.</summary>
    public LedStatus Set(LedMode mode, LedColor? color)
    {
        _desired = new LedConfig(mode, color ?? _desired.Color);

        if (!hardwareGateOpen)
            return Status(applied: false, LedAdvisor.GateClosedAdvisory);

        bool wrote = writer.TryWrite(_desired.Mode, _desired.Color);
        if (!wrote)
        {
            logger?.LogInformation("LedService: write not applied (no working HID path on this board); desired config stored.");
            return Status(applied: false, LedAdvisor.WriteFailedAdvisory);
        }
        return Status(applied: true, "Applied.");
    }

    private LedStatus Status(bool applied, string advisory) =>
        new(_desired.Mode.ToString(), _desired.Color.ToHex(), Controllable: false, applied, advisory);
}
