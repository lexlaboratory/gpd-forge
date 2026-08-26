// GPD Forge — battery charge-limit service: gated write attempt + honest advisory. GPL-3.0-or-later.
//
// "Stop charging at N%" is an EC/BIOS feature on every GPD handheld GPD Forge has looked at — there
// is no documented, driverless way to read OR write it on this board yet (unlike brightness or
// refresh rate, which are plain Win32 APIs). So this stays gated + advisory exactly like
// TabletModeService, except there is no working write path even when the gate is open — the same
// honesty stance as LedService/CurveOptimizerService.
using Microsoft.Extensions.Logging;

namespace GpdForge.Battery;

/// <summary>Reads/writes the EC/BIOS charge-limit threshold. Abstracted so
/// <see cref="ChargeLimitService"/> is unit-testable with a fake — no real EC access in tests.</summary>
public interface IChargeLimitBackend
{
    /// <summary>The live threshold, or null if there's no driverless way to read it.</summary>
    int? Read();

    /// <summary>Attempts to write the threshold. Returns true on success.</summary>
    bool Write(int percent);
}

/// <summary>The real backend. No driverless EC/BIOS read or write path is known for this board, so
/// this honestly reports "unavailable" for both rather than guessing at a register. Kept as a real
/// (if perpetually-unavailable) implementation so only this class needs to change once a verified
/// path exists.</summary>
public sealed class UnavailableChargeLimitBackend(ILogger<UnavailableChargeLimitBackend>? logger = null) : IChargeLimitBackend
{
    public int? Read() => null;

    public bool Write(int percent)
    {
        logger?.LogDebug("Charge-limit write attempted ({Percent}%) — no known EC/BIOS write path on this board; not applied.", percent);
        return false;
    }
}

/// <summary>Response shape both GET and POST /battery/charge-limit share.</summary>
public readonly record struct ChargeLimitStatus(int Percent, bool Available, bool Applied, string Advisory);

public static class ChargeLimitAdvisor
{
    public const string UnavailableReadAdvisory =
        "Charge threshold is an EC/BIOS feature; this board exposes no driverless way to read the " +
        "currently-configured limit, so this is the last-set (or default) value GPD Forge is " +
        "holding, not a live read.";

    public const string GateClosedAdvisory =
        "Charge threshold is an EC/BIOS feature — set GPDFORGE_ENABLE_HARDWARE=1 to attempt a " +
        "write. Stored only until then.";

    public const string WriteFailedAdvisory =
        "Charge threshold is an EC/BIOS feature with no verified write path on this board yet. The " +
        "desired limit is stored so the UI still round-trips, but nothing was written to the device.";
}

/// <summary>Holds the desired charge limit and decides whether/how to try applying it.</summary>
public sealed class ChargeLimitService(IChargeLimitBackend backend, bool hardwareGateOpen, ILogger<ChargeLimitService>? logger = null)
{
    private int _desired = ChargeLimitValidator.MaxPercent;

    public ChargeLimitStatus Get()
    {
        if (backend.Read() is int live)
        {
            _desired = ChargeLimitValidator.Normalize(live);
            return new ChargeLimitStatus(_desired, Available: true, Applied: false, "Live from the EC/BIOS.");
        }
        return new ChargeLimitStatus(_desired, Available: false, Applied: false, ChargeLimitAdvisor.UnavailableReadAdvisory);
    }

    /// <summary>Normalizes and stores the desired percent and, only when the hardware gate is
    /// open, attempts a write through <see cref="IChargeLimitBackend"/>. Never fakes success.</summary>
    public ChargeLimitStatus Set(int percent)
    {
        _desired = ChargeLimitValidator.Normalize(percent);
        bool available = backend.Read() is not null;

        if (!hardwareGateOpen)
            return new ChargeLimitStatus(_desired, available, Applied: false, ChargeLimitAdvisor.GateClosedAdvisory);

        bool wrote = backend.Write(_desired);
        if (!wrote)
        {
            logger?.LogInformation("ChargeLimitService: write not applied (no working EC/BIOS path); desired {Percent}% stored.", _desired);
            return new ChargeLimitStatus(_desired, available, Applied: false, ChargeLimitAdvisor.WriteFailedAdvisory);
        }
        return new ChargeLimitStatus(_desired, Available: true, Applied: true, "Applied.");
    }
}
