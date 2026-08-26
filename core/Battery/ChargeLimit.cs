// GPD Forge — battery charge-limit pure validator. GPL-3.0-or-later.
//
// Pure clamp only — no I/O. See core/Battery/ChargeLimitService.cs for the gated service that
// wraps this with the (currently nonexistent) EC/BIOS read/write path.
namespace GpdForge.Battery;

public static class ChargeLimitValidator
{
    /// <summary>Charge-limit percentages below this floor aren't a meaningful "stop charging at
    /// X%" request on any EC/BIOS implementation GPD Forge knows of, so they're clamped up to it
    /// rather than accepted as-is — same "clamp, never reject a caller's number" convention as
    /// ProfileShaper/GammaRamp elsewhere in this codebase.</summary>
    public const int MinPercent = 50;
    public const int MaxPercent = 100;

    public static int Normalize(int percent) => Math.Clamp(percent, MinPercent, MaxPercent);
}
