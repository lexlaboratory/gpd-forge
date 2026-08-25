// GPD Forge — TDP controller abstraction. GPL-3.0-or-later.
// Implementation notes: closed loop, never fire-and-forget.
namespace GpdForge.Tdp;

/// <summary>Applies power limits and VERIFIES them by reading the PM table back.</summary>
public interface ITdpController
{
    Task<TdpApplyResult> ApplyAsync(TdpProfile profile, CancellationToken ct);
}
