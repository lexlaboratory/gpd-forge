// GPD Forge — live fan RPM via the PawnIO EC read. GPL-3.0-or-later.
//
// Reads the model's RPM register through the PawnIO LpcIO module (no WinRing0). Read-only:
// it never touches a control register, so it cannot change fan state. Only wired when
// GPDFORGE_ENABLE_HARDWARE=1 + elevation (the service runs as SYSTEM). Keeps ONE EC port open
// for the lifetime of the service and re-reads per telemetry tick — cheap, no per-read reload.
using Microsoft.Extensions.Logging;

namespace GpdForge.Fan;

/// <summary>Live fan RPM source. Returns null when unavailable (no driver, unmatched board, read error).</summary>
public interface IFanRpm : IDisposable
{
    int? ReadRpm();
    GpdFanDevice? Device { get; }
}

public sealed class PawnIoFanRpm : IFanRpm
{
    private readonly IEcPort? _port;
    private readonly EcRam? _ec;
    private readonly ILogger? _logger;
    public GpdFanDevice? Device { get; }

    public PawnIoFanRpm(ILogger<PawnIoFanRpm>? logger = null, Func<IEcPort>? portFactory = null)
    {
        _logger = logger;
        try
        {
            var (vendor, product, version) = GpdFanReader.DetectBoard();
            Device = GpdDeviceDb.MatchBoard(vendor, product, version);
            if (Device is not null)
            {
                _port = (portFactory ?? (() => new PawnIoEcPort()))();
                _ec = new EcRam(_port);
                _logger?.LogInformation("PawnIO fan RPM ready for {Board} (RpmRead 0x{Rpm:X4}).", Device.BoardName, Device.RpmRead);
            }
            else
            {
                _logger?.LogInformation("Fan RPM: no matching GPD board for '{Vendor}/{Product}/{Version}'.", vendor, product, version);
            }
        }
        catch (Exception ex)
        {
            // PawnIO driver missing / not elevated / module load failed — degrade to no RPM, never throw.
            var root = ex; while (root.InnerException is not null) root = root.InnerException;
            _logger?.LogWarning("PawnIO fan RPM unavailable: {Error}", $"{root.GetType().Name}: {root.Message}");
            _port = null; _ec = null;
        }
    }

    public int? ReadRpm()
    {
        if (_ec is null || Device is null) return null;
        try
        {
            _ec.SelectSlot(Device.Slot);
            int rpm = _ec.ReadWord(Device.RpmRead);
            return rpm >= 0 ? rpm : null;
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "fan RPM read failed"); return null; }
    }

    public void Dispose() { try { _port?.Dispose(); } catch { /* best effort */ } }
}
