// GPD Forge — telemetry abstraction.
// Copyright (C) 2026 lexlaboratory. GPL-3.0-or-later. See ../../LICENSE.
// Implementation notes: LibreHardwareMonitor + PresentMon + WMI.

namespace GpdForge.Telemetry;

/// <summary>The single normalized snapshot published over the local API (see Api/).</summary>
public readonly record struct TelemetrySnapshot(
    double CpuTempC,
    double GpuTempC,
    double PackageW,
    int CpuClockMhz,
    int FanRpm,
    int FanDutyPct,
    double Fps,
    double Fps1PctLow,
    int BatteryPct,
    double DischargeW,
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
