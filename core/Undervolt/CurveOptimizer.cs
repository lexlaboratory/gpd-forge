// GPD Forge — Curve Optimizer / undervolt pure validator. GPL-3.0-or-later.
//
// Pure clamp only — no I/O, no RyzenAdj call. See core/Undervolt/CurveOptimizerService.cs for the
// gated service (RyzenAdj, this project's TDP backend, does not expose CO/PBO at all, so there is
// no write path regardless of the gate).
namespace GpdForge.Undervolt;

public static class CurveOptimizerValidator
{
    /// <summary>AMD PBO Curve-Optimizer "count" band (per-core or all-core magnitude). Negative
    /// counts undervolt (lower Vcore at a given frequency); positive counts overvolt. Both
    /// directions are clamped into this band rather than rejected outright, so a slightly-out-of-
    /// range request (e.g. from a saved profile) degrades to the nearest safe value instead of
    /// failing closed.</summary>
    public const int MinCoCount = -30;
    public const int MaxCoCount = 30;

    /// <summary>A millivolt-offset alternative some undervolt tools expose instead of discrete CO
    /// counts. Same clamp philosophy as <see cref="ClampCoCount"/>.</summary>
    public const int MinOffsetMv = -100;
    public const int MaxOffsetMv = 100;

    public static int ClampCoCount(int count) => Math.Clamp(count, MinCoCount, MaxCoCount);

    public static int ClampOffsetMv(int millivolts) => Math.Clamp(millivolts, MinOffsetMv, MaxOffsetMv);

    /// <summary>True for any negative count — the undervolt direction.</summary>
    public static bool IsUndervolt(int coCount) => coCount < 0;
}
