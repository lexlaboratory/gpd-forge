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
    /// <summary>
    /// Whether the last TDP write was read back and matched — null when nothing has written TDP yet,
    /// or when the backend cannot report a readback.
    ///
    /// 🔴 This was a HARDCODED `true` until 2026-09-02, passed as a literal at the one construction
    /// site, and this very file described AcConnected and TdpVerified together as "answers we always
    /// have". It was not an answer at all: it said "verified" on a machine with the hardware gate
    /// closed, where the stub backend echoes back whatever it was handed. The G4 pass made every
    /// sensor honest and left the one field that was not a sensor still lying.
    /// </summary>
    bool? TdpVerified);

public interface ITelemetryService
{
    Task<TelemetrySnapshot> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Phase-0 stub returning an empty snapshot until the real sources are wired.
///
/// "Empty" now means what the word says. This returned all zeros until 2026-09-02 — a CPU at 0°C,
/// 0 W package, 0 rpm fan — the exact confident-wrong reading the type above was made nullable to
/// eliminate, sitting in the file that documents the fix. It is currently reachable only from one
/// unit test that asserts call ordering, so nothing shipped ever read these values; that is luck,
/// not a design, and the next caller to wire it in would have inherited the bug.
/// </summary>
public sealed class StubTelemetryService : ITelemetryService
{
    public Task<TelemetrySnapshot> ReadAsync(CancellationToken ct) =>
        Task.FromResult(new TelemetrySnapshot(
            null, null, null, null, null, null, null, null, null, null,
            AcConnected: false, TdpVerified: null));
}
