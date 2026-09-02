// GPD Forge — TDP controller abstraction. GPL-3.0-or-later.
// Implementation notes: closed loop, never fire-and-forget.
namespace GpdForge.Tdp;

/// <summary>Applies power limits and VERIFIES them by reading the PM table back.</summary>
public interface ITdpController
{
    /// <param name="owner">
    /// WHO is asking, from <see cref="TdpOwner"/>. Required rather than optional: eight code paths
    /// write TDP, and before this the daemon could not say which one had. A default value here would
    /// mean the next caller is anonymous by omission, which is the failure this parameter exists to
    /// prevent — the same reasoning as the auditing decorators.
    /// </param>
    Task<TdpApplyResult> ApplyAsync(TdpProfile profile, string owner, CancellationToken ct);
}
