// GPD Forge — fan controller abstraction.
// Copyright (C) 2026 lexlaboratory. GPL-3.0-or-later. See ../../LICENSE.
// Implementation notes: per-model EC map, re-init on boot/resume, hysteresis.

namespace GpdForge.Fan;

public interface IFanController
{
    /// <summary>Re-initialize the EC (Win 4 leaves it uninitialized on boot AND resume).</summary>
    Task InitializeAsync(CancellationToken ct);
    Task SetDutyAsync(int percent, CancellationToken ct);
    int ReadRpm();
}

/// <summary>Phase-0 stub. Real impl drives the EC via the PawnIO broker per the model register map.</summary>
public sealed class StubFanController : IFanController
{
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task SetDutyAsync(int percent, CancellationToken ct) => Task.CompletedTask;
    public int ReadRpm() => 0;
}
