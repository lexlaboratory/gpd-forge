// GPD Forge — Anti-Modern-Standby during AI/inference jobs. GPL-3.0-or-later.
//
// Modern Standby (S0ix) on the Win 4 will happily suspend the system mid-inference if nothing
// tells Windows otherwise. Unlike a game (which keeps the display awake by presenting frames), a
// headless/background inference job produces no input and no frames, so Windows has no signal
// that work is in flight. This is the single highest-value piece of the AI mode: a long batch job
// left running unattended must not get silently paused by standby.
//
// The real mechanism is the standard Win32 power-request API, SetThreadExecutionState. It is
// unprivileged (no admin needed) and is fully and automatically undone if the process exits. It is
// NOT a hardware/BIOS write — unlike the TDP/fan backends it is NOT gated behind
// GPDFORGE_ENABLE_HARDWARE. It is wired for real, unconditionally, at the same trust level as the
// WMI brightness/battery reads elsewhere in this repo (DisplayService, BatteryService).
//
// THE THREADING BUG THIS FILE EXISTS TO NOT HAVE (fixed 2026-08-29; it shipped before that).
// The API is named SetTHREADExecutionState and it means it. The ES_CONTINUOUS requirement is owned
// by the calling THREAD, not by the process: the kernel records it against that thread, and it is
// released when that thread clears it or when that thread dies. The original version of this file
// claimed "This is a per-process state" and called the import straight from whatever thread the
// caller happened to be on. Both concrete consequences were real and both were silent:
//   * POST /ai/anti-standby {enable:true} lands on pool thread A, which takes ES_SYSTEM_REQUIRED.
//     Toggling it off lands on pool thread B, whose ES_CONTINUOUS clears B's (empty) requirement
//     and leaves A's in force forever. HolderCount reads 0, the panel says released, and the
//     machine never enters Modern Standby again — the exact 14.4 W / 36 %-per-hour overnight drain
//     this project spent 2026-08-29 eliminating, now with a counter insisting all is well.
//   * The inverse: the pool retires the thread holding the request, the kernel drops it with the
//     thread, and the keep-awake evaporates mid-inference. The feature fails open, unannounced.
// The fix is the only one that actually holds: ONE dedicated, long-lived, background owner thread
// per sink, and every Engage/Release marshalled onto it, so the request is taken and dropped by the
// same thread and that thread outlives every hold. See OwnerThreadExecutionStateSink below.
//
// REJECTED ALTERNATIVES.
//   * PowerCreateRequest / PowerSetRequest is genuinely per-process and would need no owner thread.
//     Rejected for now because it is a strictly larger change (handle lifetime, a REASON_CONTEXT
//     string, a second P/Invoke surface) for a feature that already works once the thread affinity
//     is right. Recorded here so the next person knows it was considered, not missed.
//   * "Just always call from the BackgroundService thread." There is no such single thread: the API
//     handler and the hold worker both drive this, and a BackgroundService's ExecuteAsync resumes
//     on arbitrary pool threads after every await. That is the bug, not the fix.
//   * Retrying a failed SetThreadExecutionState in the owner loop. Rejected: a call that fails will
//     keep failing, and spinning on it would burn the battery this feature exists to protect. A
//     failure is recorded and logged once instead, and reported as a failure — never as a hold.
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
/// Runs an inner <see cref="IExecutionStateSink"/> on one dedicated, long-lived background thread.
/// <para>
/// Callers (the API handler, <c>InferenceHoldWorker</c>, the job queue) may hit
/// <see cref="Engage"/>/<see cref="Release"/> from any thread and concurrently; the inner sink's
/// Engage/Release are only ever invoked from the owner thread, which is created in the constructor
/// and lives until <see cref="Dispose"/>. That is what makes a thread-affine request such as
/// <c>SetThreadExecutionState</c> safe here — see the threading note at the top of this file.
/// </para>
/// <para>
/// The queue carries a desired STATE, not commands, so the awkward orderings collapse: a Release
/// before any Engage is a no-op (desired already matches applied), repeated Engages coalesce into
/// the one transition the inner sink sees, and a rapid on/off/on settles on the final value rather
/// than replaying every edge. The owner thread is a background thread, so it never keeps the process
/// alive; <see cref="Dispose"/> asks it to drop the request and WAITS for it, so shutdown drops the
/// hold instead of orphaning it. If the wait times out we say so rather than pretend it released.
/// </para>
/// </summary>
public sealed class OwnerThreadExecutionStateSink : IExecutionStateSink, IDisposable
{
    /// <summary>How long <see cref="Dispose"/> will wait for the owner thread to drop the request.</summary>
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);

    private readonly IExecutionStateSink _inner;
    private readonly ILogger? _logger;
    private readonly Thread _owner;
    private readonly object _gate = new();

    private bool _desired;    // what callers want; written by any thread under _gate
    private bool _attempted;  // the last state the owner thread ASKED the inner sink for
    private bool _applied;    // the last state the inner sink actually accepted
    private bool _disposed;

    public OwnerThreadExecutionStateSink(IExecutionStateSink inner, string threadName, ILogger? logger = null)
    {
        _inner = inner;
        _logger = logger;
        _owner = new Thread(Run) { IsBackground = true, Name = threadName };
        _owner.Start();
    }

    /// <summary>The managed id of the owner thread — the only thread the inner sink is touched from.</summary>
    public int OwnerThreadId => _owner.ManagedThreadId;

    /// <summary>
    /// True once the owner thread has put the request in force. This is the applied truth, not the
    /// requested one: it stays false if the inner sink threw while engaging.
    /// </summary>
    public bool Engaged { get { lock (_gate) return _applied; } }

    public void Engage() => Want(true);

    public void Release() => Want(false);

    private void Want(bool engaged)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                // Nothing to marshal onto any more. Engaging here would take a hold nobody can ever
                // drop; releasing is already what disposal did. Say so rather than fail silently.
                _logger?.LogWarning(
                    "Anti-standby sink is disposed; ignoring a request to {Action} the execution state.",
                    engaged ? "engage" : "release");
                return;
            }

            _desired = engaged;
            Monitor.Pulse(_gate);
        }
    }

    private void Run()
    {
        while (true)
        {
            bool target, stopping;
            lock (_gate)
            {
                // _attempted, not _applied, is the wait predicate. Comparing against _applied would
                // spin forever on a failing Engage: the call would never make _applied true, so the
                // predicate would never settle. A call that just failed will fail again; we record
                // it once and go back to sleep until somebody asks for something different.
                while (!_disposed && _desired == _attempted) Monitor.Wait(_gate);
                stopping = _disposed;
                target = _desired;
                if (target == _attempted)
                {
                    // Woken only to stop, and we never asked for anything. Nothing to drop.
                    if (stopping) return;
                    continue;
                }

                _attempted = target;
            }

            var ok = Apply(target);

            lock (_gate)
            {
                // _applied is the applied truth, so a failed Engage leaves it false — the layer above
                // must not read a hold that the OS refused. A failed Release leaves it false too:
                // whatever the state now is, we are not claiming to hold anything.
                _applied = ok && target;
            }

            // Deliberately no `if (stopping) return` here. Dispose can land WHILE this Apply is in
            // flight — the engage completes, and returning now would walk away from a hold we just
            // took. Falling back to the top re-reads _desired, which disposal has already forced
            // false, so the release happens first and the loop exits on the next pass.
        }
    }

    private bool Apply(bool engage)
    {
        try
        {
            if (engage) _inner.Engage();
            else _inner.Release();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Anti-standby {Action} failed on the owner thread.", engage ? "engage" : "release");
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _desired = false;   // drop the request on the way out, on the thread that took it
            Monitor.Pulse(_gate);
        }

        if (!_owner.Join(ShutdownWait))
        {
            _logger?.LogError(
                "Anti-standby owner thread did not finish within {Seconds}s; the execution-state request " +
                "may still be in force until this process exits.", ShutdownWait.TotalSeconds);
        }
    }
}

/// <summary>
/// Real sink: P/Invoke <c>SetThreadExecutionState</c>. <c>ES_CONTINUOUS | ES_SYSTEM_REQUIRED</c> keeps
/// the system out of sleep/Modern-Standby for as long as it's in force; a later call with just
/// <c>ES_CONTINUOUS</c> clears the SYSTEM_REQUIRED flag so normal idle timers resume.
/// <para>
/// The request is owned by the THREAD that made it, not by the process, so every call is marshalled
/// onto one dedicated owner thread via <see cref="OwnerThreadExecutionStateSink"/>. The full argument
/// is at the top of this file. Repeating the same flags on that one thread is harmless/idempotent,
/// which is why the ref-counting lives one layer up in <see cref="AntiStandbyService"/>.
/// </para>
/// <para>
/// <c>SetThreadExecutionState</c> returns 0 on FAILURE. That return used to be discarded, which meant
/// a hold that was never taken still read as taken. It is now checked: a zero throws out of the raw
/// call, the owner thread logs it, and <see cref="Engaged"/> stays false. <see cref="LastCallSucceeded"/>
/// is null until the OS has actually been asked something — an unmeasured value, not a hopeful one.
/// </para>
/// </summary>
public sealed partial class Win32ExecutionStateSink : IExecutionStateSink, IDisposable
{
    private readonly RawSink _raw;
    private readonly OwnerThreadExecutionStateSink _pump;

    public Win32ExecutionStateSink(ILogger<Win32ExecutionStateSink>? logger = null)
    {
        _raw = new RawSink();
        _pump = new OwnerThreadExecutionStateSink(_raw, "gpdforge-execution-state", logger);
    }

    public void Engage() => _pump.Engage();

    public void Release() => _pump.Release();

    /// <summary>True once the OS has actually accepted the keep-awake request.</summary>
    public bool Engaged => _pump.Engaged;

    /// <summary>
    /// Whether the most recent <c>SetThreadExecutionState</c> call returned success. Null means no
    /// call has completed yet — we do not know, so we do not guess.
    /// </summary>
    public bool? LastCallSucceeded => _raw.LastCallSucceeded;

    public void Dispose() => _pump.Dispose();

    /// <summary>
    /// The bare P/Invoke, with no threading opinion of its own. It is only ever driven from the owner
    /// thread; keeping it separate is what lets the marshalling above be tested with no P/Invoke.
    /// <para>
    /// Treating a 0 return as a failure is only safe because the desired-state pump never issues a
    /// Release the owner thread has not first matched with an Engage — the documented return is the
    /// PREVIOUS state, and reading a fresh thread's previous state is not something to bet a thrown
    /// exception on.
    /// </para>
    /// </summary>
    private sealed partial class RawSink : IExecutionStateSink
    {
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        private volatile int _last;   // 0 = never called, 1 = succeeded, 2 = failed

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial uint SetThreadExecutionState(uint esFlags);

        public bool? LastCallSucceeded => _last switch { 1 => true, 2 => false, _ => null };

        public void Engage() => Call(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);

        public void Release() => Call(ES_CONTINUOUS);

        private void Call(uint flags)
        {
            uint previous = SetThreadExecutionState(flags);
            if (previous != 0)
            {
                _last = 1;
                return;
            }

            _last = 2;
            throw new InvalidOperationException(
                $"SetThreadExecutionState(0x{flags:X8}) failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }
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
