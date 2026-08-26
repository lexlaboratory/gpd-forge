// GPD Forge — Anti-Modern-Standby during AI/inference jobs. GPL-3.0-or-later.
//
// Modern Standby (S0ix) on the Win 4 will happily suspend the system mid-inference if nothing
// tells Windows otherwise. Unlike a game (which keeps the display awake by presenting frames), a
// headless/background inference job produces no input and no frames, so Windows has no signal
// that work is in flight. This is the single highest-value piece of the AI mode: a long batch job
// left running unattended must not get silently paused by standby.
//
// The real mechanism is the standard Win32 power-request API, SetThreadExecutionState. It is
// unprivileged (no admin needed), affects only this process's contribution to the system idle/sleep
// timers, and is fully and automatically undone if the process exits. It is NOT a hardware/BIOS
// write — unlike the TDP/fan backends it is NOT gated behind GPDFORGE_ENABLE_HARDWARE. It is wired
// for real, unconditionally, at the same trust level as the WMI brightness/battery reads elsewhere
// in this repo (DisplayService, BatteryService).
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GpdForge.Ai;

/// <summary>
/// The side effect of asking Windows to stay awake. Abstracted so <see cref="AntiStandbyService"/>'s
/// ref-counting is unit-testable with a fake — zero P/Invoke involved in tests.
/// </summary>
public interface IExecutionStateSink
{
    /// <summary>Ask Windows to suppress Modern Standby / display sleep while this holds.</summary>
    void Engage();

    /// <summary>Withdraw the request — Windows may sleep normally again.</summary>
    void Release();
}

/// <summary>
/// Real sink: P/Invoke <c>SetThreadExecutionState</c>. <c>ES_CONTINUOUS | ES_SYSTEM_REQUIRED</c> keeps
/// the system out of sleep/Modern-Standby for as long as it's in force; a later call with just
/// <c>ES_CONTINUOUS</c> clears the SYSTEM_REQUIRED flag so normal idle timers resume. This is a
/// per-process state — calling it repeatedly with the same flags is harmless/idempotent, which is why
/// the ref-counting lives one layer up in <see cref="AntiStandbyService"/> rather than here.
/// </summary>
public sealed partial class Win32ExecutionStateSink : IExecutionStateSink
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint esFlags);

    public void Engage() => SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);

    public void Release() => SetThreadExecutionState(ES_CONTINUOUS);
}

/// <summary>
/// Ref-counted keep-awake holder. Multiple AI jobs (or the manual UI toggle) can each acquire a hold
/// independently; the underlying <see cref="IExecutionStateSink"/> is only engaged on the 0→1
/// transition and released on the 1→0 transition, so concurrent holds compose correctly and a stray
/// extra <see cref="Stop"/> can never release a hold someone else still needs. Thread-safe: the API
/// (manual toggle) and the job queue (per-job holds) can call this concurrently.
/// </summary>
public sealed class AntiStandbyService(IExecutionStateSink sink, ILogger<AntiStandbyService>? logger = null)
{
    private readonly object _gate = new();
    private int _holders;

    /// <summary>How many concurrent holds are currently open.</summary>
    public int HolderCount { get { lock (_gate) return _holders; } }

    /// <summary>True while at least one hold is open (i.e. the sink is engaged).</summary>
    public bool Active => HolderCount > 0;

    /// <summary>Take a hold. Engages the sink only if this is the first (0→1). Returns the new count.</summary>
    public int Start()
    {
        lock (_gate)
        {
            _holders++;
            if (_holders == 1)
            {
                sink.Engage();
                logger?.LogInformation("Anti-standby engaged (AI job/hold active).");
            }
            return _holders;
        }
    }

    /// <summary>
    /// Release a hold. Releases the sink only when the last one drops (1→0). Never goes negative —
    /// an extra Stop() beyond zero is a harmless no-op. Returns the new count.
    /// </summary>
    public int Stop()
    {
        lock (_gate)
        {
            if (_holders == 0) return 0;
            _holders--;
            if (_holders == 0)
            {
                sink.Release();
                logger?.LogInformation("Anti-standby released (no more AI jobs/holds).");
            }
            return _holders;
        }
    }
}
