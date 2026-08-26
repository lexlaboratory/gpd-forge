// GPD Forge — undervolt/Curve-Optimizer service: gated, and honest that there is no backend at
// all. GPL-3.0-or-later.
//
// RyzenAdj — the only TDP backend this project drives (see core/Tdp/RyzenAdjBackend.cs) — has no
// Curve Optimizer / PBO flags; it simply doesn't implement that SMU mailbox call. So unlike
// LED/charge-limit (which at least have a real-but-broken write path to point an interface at),
// there is nothing to inject here: this is validated and stored only, always applied:false, gated
// the same way as every other hardware control for consistency (so turning the gate on is a
// legible, single, honest signal — "yes, try" — even where "try" still means "refuse honestly").
using Microsoft.Extensions.Logging;

namespace GpdForge.Undervolt;

/// <summary>Response shape both GET and POST /undervolt share.</summary>
public readonly record struct UndervoltStatus(int CoCount, int OffsetMv, bool Applied, string Advisory);

public static class CurveOptimizerAdvisor
{
    public const string GateClosedAdvisory =
        "Curve Optimizer / PBO undervolt has no implemented write path on this board — RyzenAdj " +
        "(GPD Forge's TDP backend) does not expose it — set GPDFORGE_ENABLE_HARDWARE=1 if you want " +
        "that confirmed directly. Validated and stored only.";

    public const string NoBackendAdvisory =
        "RyzenAdj (GPD Forge's TDP backend) does not expose Curve Optimizer / PBO controls, and no " +
        "other verified write path exists for this board. The requested value is validated and " +
        "stored, but never written.";
}

/// <summary>Holds the desired CO count / mV offset and validates every request, but never attempts
/// a write — see the file header for why there is no backend to inject here at all.</summary>
public sealed class CurveOptimizerService(bool hardwareGateOpen, ILogger<CurveOptimizerService>? logger = null)
{
    private int _coCount;
    private int _offsetMv;

    public UndervoltStatus Get() => new(_coCount, _offsetMv, Applied: false, Advisory);

    public UndervoltStatus Set(int? coCount, int? offsetMv)
    {
        if (coCount is int c) _coCount = CurveOptimizerValidator.ClampCoCount(c);
        if (offsetMv is int m) _offsetMv = CurveOptimizerValidator.ClampOffsetMv(m);

        if (hardwareGateOpen)
            logger?.LogInformation(
                "CurveOptimizerService: gate open but RyzenAdj exposes no CO/PBO path; not attempting a write (coCount={CoCount}, offsetMv={OffsetMv}).",
                _coCount, _offsetMv);

        return new UndervoltStatus(_coCount, _offsetMv, Applied: false, Advisory);
    }

    private string Advisory => hardwareGateOpen ? CurveOptimizerAdvisor.NoBackendAdvisory : CurveOptimizerAdvisor.GateClosedAdvisory;
}
