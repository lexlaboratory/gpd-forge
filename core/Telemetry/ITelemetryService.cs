// GPD Forge — telemetry abstraction.
// Copyright (C) 2026 lexlaboratory. GPL-3.0-or-later. See ../../LICENSE.
// Implementation notes: LibreHardwareMonitor + PresentMon + WMI.

namespace GpdForge.Telemetry;

/// <summary>The single normalized snapshot published over the local API (see Api/).</summary>
/// <summary>
/// One reading. Every sensor is nullable, and that is the point of this type.
///
/// Until 2026-09-01 an unreadable sensor came back as <c>0</c>. With the hardware gate closed the
/// daemon reported <c>cpuTempC: 0, packageW: 0, fanRpm: 0</c> — a CPU at zero degrees, which is a
/// plausible, confident, wrong number. The panel could not tell "cold" from "unmeasured", and
/// neither could anything else reading this struct.
///
/// This repository had already removed exactly that failure from <c>GET /standby</c>, where drain
/// figures became null rather than extrapolations. Telemetry was the last place still doing it.
///
/// ⚠️ Note which fields are NOT nullable, and why the distinction is per-field rather than blanket:
/// <see cref="AcConnected"/> and <see cref="TdpVerified"/> are answers we always have. And a zero in
/// <see cref="Fps"/> or <see cref="DischargeW"/> is meaningful when the source EXISTS — nothing is
/// presenting frames, nothing is discharging on AC — so those are null only when there is no source
/// at all. Collapsing "measured zero" into null would lose as much information as the bug being
/// fixed here.
/// </summary>
public readonly record struct TelemetrySnapshot(
    double? CpuTempC,
    double? GpuTempC,
    double? PackageW,
    int? CpuClockMhz,
    int? FanRpm,
    int? FanDutyPct,
    double? Fps,
    double? Fps1PctLow,
    int? BatteryPct,
    double? DischargeW,
    bool AcConnected,
    bool TdpVerified);

public interface ITelemetryService
{
    Task<TelemetrySnapshot> ReadAsync(CancellationToken ct);
}

/// <summary>Phase-0 stub returning an empty snapshot until the real sources are wired.</summary>
public sealed class StubTelemetryService : ITelemetryService
{
    public Task<TelemetrySnapshot> ReadAsync(CancellationToken ct) =>
        Task.FromResult(new TelemetrySnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, false));
}
