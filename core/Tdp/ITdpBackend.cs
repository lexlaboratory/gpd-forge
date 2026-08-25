// GPD Forge — TDP backend + delay abstractions. GPL-3.0-or-later.
namespace GpdForge.Tdp;

/// <summary>
/// The low-level side of TDP: apply limits (via RyzenAdj through the PawnIO broker) and read the PM table
/// back. Kept behind an interface so the closed loop is unit-testable without hardware.
/// </summary>
public interface ITdpBackend
{
    Task ApplyAsync(TdpProfile profile, CancellationToken ct);
    Task<TdpReadout> ReadAsync(CancellationToken ct);
}

/// <summary>Injectable delay so the closed loop's backoff is testable without real waiting.</summary>
public interface IDelay
{
    Task WaitAsync(TimeSpan duration, CancellationToken ct);
}

/// <summary>Production delay backed by Task.Delay.</summary>
public sealed class SystemDelay : IDelay
{
    public Task WaitAsync(TimeSpan duration, CancellationToken ct) => Task.Delay(duration, ct);
}

/// <summary>
/// Phase-0 backend with no hardware: echoes the request as observed (so the loop verifies on attempt 1).
/// Replaced in Phase 1 by RyzenAdj-through-broker; that real backend is where the firmware can revert.
/// </summary>
public sealed class StubTdpBackend : ITdpBackend
{
    private TdpProfile _last;
    public Task ApplyAsync(TdpProfile profile, CancellationToken ct) { _last = profile; return Task.CompletedTask; }
    public Task<TdpReadout> ReadAsync(CancellationToken ct) => Task.FromResult(new TdpReadout(_last.StapmW, _last.FastW));
}
