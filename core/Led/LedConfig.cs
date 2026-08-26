// GPD Forge — LED/RGB pure logic (color parsing + Win-4 mode encoding). GPL-3.0-or-later.
//
// Pure data + math only — no I/O, no HID, fully unit-testable without touching a device. See
// core/Led/LedService.cs for the gated service that wraps this with the (currently non-functional)
// hardware write path.
using System.Globalization;

namespace GpdForge.Led;

/// <summary>An RGB888 color. Immutable; always in-range by construction (each channel is a byte).</summary>
public readonly record struct LedColor(byte R, byte G, byte B)
{
    /// <summary>Packs the color into a single 0xRRGGBB value.</summary>
    public int ToRgb888() => (R << 16) | (G << 8) | B;

    /// <summary>Lowercase "#rrggbb" — the form HTML's &lt;input type="color"&gt; expects.</summary>
    public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";

    /// <summary>Builds a color from raw channel values, clamping each into 0..255 rather than
    /// throwing — same "clamp, never reject a caller's number" convention as
    /// GammaRamp/ProfileShaper elsewhere in this codebase.</summary>
    public static LedColor FromRgb(int r, int g, int b) =>
        new((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));

    /// <summary>Parses "#RRGGBB" or "RRGGBB" (the '#' is optional, hex digits are case-insensitive).
    /// Throws <see cref="FormatException"/> on anything else — use <see cref="TryParse"/> at an API
    /// boundary that wants a 400 instead of an exception.</summary>
    public static LedColor Parse(string text) =>
        TryParse(text, out var color) ? color : throw new FormatException($"'{text}' is not a valid #RRGGBB color");

    public static bool TryParse(string? text, out LedColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string hex = text.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length != 6) return false;

        if (!byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)) return false;
        if (!byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)) return false;
        if (!byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)) return false;

        color = new LedColor(r, g, b);
        return true;
    }
}

/// <summary>LED lighting modes this board's config protocol defines.</summary>
public enum LedMode { Off, Solid, Breathe, Rotate }

/// <summary>The documented Win-4 mode → control-byte encoding. Pure lookup table — unit-tested
/// directly, kept separate from anything that would actually send the byte (see LedService.cs).</summary>
public static class LedModeEncoding
{
    public static byte ToByte(this LedMode mode) => mode switch
    {
        LedMode.Off => 0x00,
        LedMode.Solid => 0x01,
        LedMode.Breathe => 0x11,
        LedMode.Rotate => 0x21,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown LED mode"),
    };
}

/// <summary>The desired LED configuration — pure data GPD Forge remembers so GET /led round-trips
/// even though nothing on this board can be read back live (see LedService.cs).</summary>
public readonly record struct LedConfig(LedMode Mode, LedColor Color)
{
    public static readonly LedConfig Default = new(LedMode.Off, LedColor.FromRgb(0, 200, 255));
}
