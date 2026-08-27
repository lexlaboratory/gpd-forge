// GPD Forge — gated fan PWM-duty WRITE controller. GPL-3.0-or-later.
//
// This is the WRITE path, distinct from the read-only IFanController/StubFanController phase-0 stub
// (see IFanController.cs, still used by StandbyDoctor) and from the read-only PawnIoFanRpm (RPM
// telemetry). Only ever constructed for real when BOTH GPDFORGE_ENABLE_HARDWARE=1 AND
// GPDFORGE_ENABLE_FAN_CONTROL=1 (see Program.cs) — fan writes are gated separately from, and on top
// of, the general hardware gate because commanding the wrong duty is a more immediate physical risk
// than a read.
//
// Write sequence mirrors Cryolitia/gpd-fan-driver (GPL-2.0, (c) 2024 Cryolitia PukNgae):
//   set MANUAL duty D:      ec.WriteByte(pwm_write, cast(D)); ec.WriteByte(manual_control_enable, 1);
//   restore AUTOMATIC:      ec.WriteByte(manual_control_enable, 0);
// with one added safety rule from the same driver: the FIRST time we switch INTO manual, write the
// duty at MAX before the real target — never engage manual control sitting at a low/zero speed.
using Microsoft.Extensions.Logging;

namespace GpdForge.Fan;

public interface IGpdFanController : IDisposable
{
    /// <summary>True only when a real EC port is open and commands can reach matched hardware.</summary>
    bool Available { get; }

    /// <summary>True while the EC is under our manual control (false = automatic/firmware-driven).</summary>
    bool IsManual { get; }

    /// <summary>
    /// Requests a manual PWM duty (0..255 user scale; clamped to <see cref="GpdFanController.MinManualDuty"/>..255
    /// — never commands a near-stopped fan). Returns true only if the write was read back verified.
    /// </summary>
    bool SetManualDuty(int duty0to255);

    /// <summary>Restores AUTOMATIC (firmware-driven) fan control. Never throws.</summary>
    void SetAuto();

    /// <summary>Reads the EC's current PWM duty back in the 0..255 user scale, or null if unavailable.</summary>
    int? ReadDuty();
}

/// <summary>Gate-closed / unmatched-board stand-in: touches no hardware, and is honest about it —
/// every write reports failure so a caller can never mistake "gate closed" for "write succeeded".</summary>
public sealed class NoOpGpdFanController : IGpdFanController
{
    public bool Available => false;
    public bool IsManual => false;
    public bool SetManualDuty(int duty0to255) => false;
    public void SetAuto() { }
    public int? ReadDuty() => null;
    public void Dispose() { }
}

/// <summary>
/// Real controller: EC RAM writes through <see cref="IEcPort"/> per the matched board's register map
/// (<see cref="GpdDeviceDb"/>). Safety-critical — see <see cref="SetManualDuty"/> for the write
/// sequence and <see cref="Dispose"/> for the shutdown restore. Injectable <see cref="IEcPort"/>
/// factory (mirrors <c>PawnIoFanRpm</c>'s constructor shape) so this is unit-testable with a fake —
/// the default factory is the real, elevation-requiring <see cref="PawnIoEcPort"/>.
/// </summary>
public sealed class GpdFanController : IGpdFanController
{
    /// <summary>
    /// Never command less than this (~16%, 40/255) in manual mode. A near-stopped PWM duty can stall
    /// the fan instead of spinning it slowly, which is worse than a slightly-louder floor.
    /// </summary>
    public const int MinManualDuty = 40;

    private readonly IEcPort? _port;
    private readonly EcRam? _ec;
    private readonly GpdFanDevice _device;
    private readonly ILogger? _logger;
    private bool _disposed;

    /// <summary>True once the EC port opened successfully (driver present, elevated). False means
    /// every method below safely no-ops rather than throwing.</summary>
    public bool Available => _ec is not null;

    public bool IsManual { get; private set; }

    public GpdFanController(GpdFanDevice device, Func<IEcPort>? portFactory = null, ILogger? logger = null)
    {
        _device = device;
        _logger = logger;
        try
        {
            _port = (portFactory ?? (() => new PawnIoEcPort()))();
            _ec = new EcRam(_port);
        }
        catch (Exception ex)
        {
            // PawnIO driver missing / not elevated / module load failed — degrade to unavailable,
            // never throw (same stance as PawnIoFanRpm).
            var root = ex; while (root.InnerException is not null) root = root.InnerException;
            _logger?.LogWarning("GpdFanController: EC port unavailable ({Error}); fan writes will no-op.", $"{root.GetType().Name}: {root.Message}");
            _port = null;
            _ec = null;
        }
    }

    /// <summary>
    /// Sequence: select the EC slot, then —
    ///   <list type="bullet">
    ///   <item>if NOT already manual: write pwm_write = cast(MAX), THEN write manual_control_enable = 1
    ///   (so the instant manual control takes over, the last-commanded duty is unambiguously MAX —
    ///   never a low/zero speed), THEN write pwm_write = cast(duty) now that manual is safely active.
    ///   The max write must land BEFORE the enable flip, not after: while manual_control_enable is 0
    ///   the firmware's own auto loop owns pwm_write, so a write ordered [[max, duty, enable]] would
    ///   have duty silently clobber max before manual ever samples it — defeating the safety step.</item>
    ///   <item>if already manual: write pwm_write = cast(duty), THEN (re-)write manual_control_enable = 1
    ///   — the plain steady-state recipe, no MAX pre-step needed.</item>
    ///   </list>
    /// Finally, read pwm_write back and verify it equals cast(duty) within ±1. Returns true only if
    /// that read-back verified; false on any failure (never throws).
    /// </summary>
    public bool SetManualDuty(int duty0to255)
    {
        if (_ec is null) return false;

        int clamped = Math.Clamp(duty0to255, MinManualDuty, 255);
        int target = FanMath.CastPwm(clamped, _device.PwmMax);

        try
        {
            _ec.SelectSlot(_device.Slot);

            if (!IsManual)
            {
                // Transitioning INTO manual: land MAX in pwm_write, THEN flip the enable bit — so the
                // last value the firmware saw the instant it stopped auto-controlling was MAX, never a
                // low/zero speed. Only once manual is confirmed active (below) do we write the real
                // target — writing it before the enable flip would just silently clobber this max
                // write while auto still owns pwm_write, defeating the safety step entirely.
                byte max = (byte)FanMath.CastPwm(255, _device.PwmMax);
                _ec.WriteByte(_device.PwmWrite, max);
                _ec.WriteByte(_device.ManualControlEnable, 1);
                IsManual = true;
                _ec.WriteByte(_device.PwmWrite, (byte)target);
            }
            else
            {
                _ec.WriteByte(_device.PwmWrite, (byte)target);
                _ec.WriteByte(_device.ManualControlEnable, 1);
            }

            int readBack = _ec.ReadByte(_device.PwmWrite);
            bool ok = Math.Abs(readBack - target) <= 1;
            if (!ok)
                _logger?.LogWarning("GpdFanController: read-back mismatch after manual duty write (wanted {Target}, got {ReadBack}).", target, readBack);
            return ok;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GpdFanController: manual duty write failed.");
            return false;
        }
    }

    public void SetAuto()
    {
        if (_ec is null) return;
        try
        {
            _ec.SelectSlot(_device.Slot);
            _ec.WriteByte(_device.ManualControlEnable, 0);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "GpdFanController: restore-automatic write failed."); }
        finally { IsManual = false; }
    }

    public int? ReadDuty()
    {
        if (_ec is null) return null;
        try
        {
            _ec.SelectSlot(_device.Slot);
            return FanMath.UncastPwm(_ec.ReadByte(_device.PwmWrite), _device.PwmMax);
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "GpdFanController: duty read failed."); return null; }
    }

    /// <summary>CRITICAL SAFETY: always restores AUTOMATIC on shutdown/dispose, even if the caller
    /// already did so (idempotent — SetAuto is safe to call twice). Never leave the EC pinned in
    /// manual because the process exited without cleaning up.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { SetAuto(); } catch { /* best effort — never throw from Dispose */ }
        try { _port?.Dispose(); } catch { /* best effort */ }
    }
}
