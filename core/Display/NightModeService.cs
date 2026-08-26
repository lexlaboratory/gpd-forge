// GPD Forge — warm-screen "night mode" via the GDI gamma ramp (REAL). GPL-3.0-or-later.
//
// Deliberately NOT Windows Night Light: that feature's on/off + color-temperature state lives in
// an undocumented, build-fragile registry blob (CloudStore\...\ColorProfileType\...) — poking it
// directly is exactly the kind of blind, easily-breaking write this project refuses to ship.
// Instead this drives the standard GDI gamma ramp (SetDeviceGammaRamp), the same real, fully
// reversible primitive f.lux/Redshift-style tools use: off restores the identity ramp (screen
// completely unaffected), no driver, no elevation.
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GpdForge.Display;

/// <summary>A computed 256-entry-per-channel gamma ramp.</summary>
public readonly record struct GammaRampValues(ushort[] Red, ushort[] Green, ushort[] Blue);

/// <summary>Pure gamma-ramp math: warmth 0..100 → a valid ramp. No I/O — unit-tested directly.
/// Warmth 0 is the identity ramp (screen unaffected); warmth 100 is maximally warm (blue cut
/// hardest, green cut less, red untouched — the standard "reduce blue" warm-white recipe).</summary>
public static class GammaRamp
{
    public const int ChannelSize = 256;
    private const double MaxGreenCut = 0.20;
    private const double MaxBlueCut = 0.55;

    public static GammaRampValues Build(int warmth)
    {
        int w = Math.Clamp(warmth, 0, 100);
        double greenScale = 1.0 - MaxGreenCut * (w / 100.0);
        double blueScale = 1.0 - MaxBlueCut * (w / 100.0);

        var red = new ushort[ChannelSize];
        var green = new ushort[ChannelSize];
        var blue = new ushort[ChannelSize];
        for (int i = 0; i < ChannelSize; i++)
        {
            int identity = i * 257; // 0, 257, ..., 65535 across 256 steps — the standard identity ramp
            red[i] = (ushort)identity;
            green[i] = Scale(identity, greenScale);
            blue[i] = Scale(identity, blueScale);
        }
        return new GammaRampValues(red, green, blue);
    }

    /// <summary>True if <paramref name="warmth"/> (after clamping) produces the identity ramp.</summary>
    public static bool IsIdentity(int warmth) => Math.Clamp(warmth, 0, 100) == 0;

    private static ushort Scale(int identity, double factor) =>
        (ushort)Math.Clamp((int)Math.Round(identity * factor), 0, 65535);
}

/// <summary>Applies a gamma ramp to the display. Abstracted so <see cref="NightModeService"/> is
/// unit-testable with a fake — no P/Invoke in tests.</summary>
public interface IGammaRampSink
{
    bool Apply(GammaRampValues ramp);
}

/// <summary>
/// Real sink: GetDC(NULL) + gdi32 SetDeviceGammaRamp + ReleaseDC. The native ramp struct
/// (<see cref="GammaRampNative"/>) uses fixed-size ushort buffers rather than
/// <c>ushort[]</c> + <c>[MarshalAs(ByValArray)]</c>, so it stays blittable for LibraryImport — same
/// rationale as DEVMODE in RefreshRateService.cs.
/// </summary>
public sealed partial class Win32GammaRampSink(ILogger<Win32GammaRampSink>? logger = null) : IGammaRampSink
{
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("gdi32.dll")]
    private static partial int SetDeviceGammaRamp(IntPtr hdc, ref GammaRampNative lpRamp);

    public bool Apply(GammaRampValues ramp)
    {
        if (ramp.Red.Length != GammaRamp.ChannelSize || ramp.Green.Length != GammaRamp.ChannelSize || ramp.Blue.Length != GammaRamp.ChannelSize)
            return false;

        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try { return TryApply(hdc, ramp); }
        catch (Exception ex) { logger?.LogDebug(ex, "gamma ramp apply failed"); return false; }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }

    private static unsafe bool TryApply(IntPtr hdc, GammaRampValues ramp)
    {
        var native = new GammaRampNative();
        for (int i = 0; i < GammaRamp.ChannelSize; i++)
        {
            native.Red[i] = ramp.Red[i];
            native.Green[i] = ramp.Green[i];
            native.Blue[i] = ramp.Blue[i];
        }
        return SetDeviceGammaRamp(hdc, ref native) != 0;
    }
}

/// <summary>Blittable projection of Win32's gamma-ramp struct (3x WORD[256]) via fixed buffers.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct GammaRampNative
{
    public fixed ushort Red[256];
    public fixed ushort Green[256];
    public fixed ushort Blue[256];
}

/// <summary>
/// Tracks + applies the night-mode toggle. <see cref="Warmth"/> always reflects what is actually on
/// screen right now (0 while off — the identity ramp really is applied, not just remembered), so the
/// API never reports a warmth that isn't real; a failed native apply leaves the reported state
/// unchanged rather than claiming success.
/// </summary>
public sealed class NightModeService(IGammaRampSink sink, ILogger<NightModeService>? logger = null)
{
    private int _lastRequestedWarmth = 50; // used when turning on without specifying a warmth

    public bool On { get; private set; }
    public int Warmth { get; private set; }

    public (bool On, int Warmth) Set(bool on, int? warmth)
    {
        if (warmth is int w) _lastRequestedWarmth = Math.Clamp(w, 0, 100);

        int target = on ? _lastRequestedWarmth : 0;
        if (!sink.Apply(GammaRamp.Build(target)))
        {
            logger?.LogWarning("NightModeService: gamma ramp apply failed (on={On}, warmth={Warmth}); state unchanged.", on, target);
            return (On, Warmth);
        }

        On = on;
        Warmth = target;
        return (On, Warmth);
    }
}
