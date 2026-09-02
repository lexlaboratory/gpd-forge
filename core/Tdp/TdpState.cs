// GPD Forge — what TDP is in force, and who put it there. GPL-3.0-or-later.
//
// Before this existed the daemon could not answer either question. `GET /telemetry` reported
// `tdpVerified: true` as a hardcoded literal (WmiTelemetryService, "an answer we always have" — it
// was not an answer at all), and eight separate code paths write TDP with nothing recording which
// one acted:
//
//   ForgeWorker  — the guardian throttle, the charge-guard ceiling, the throttle-clear restore,
//                  the tuner sweep, and the auto-FPS governor
//   Program.cs   — POST /mode, POST /tdp, POST /panic
//
// So "the machine is at 12 W" was visible and "the thermal guardian put it there, and it verified"
// was not — which is the difference between a number and a fact. Someone looking at a handheld stuck
// at 12 W had no way to tell a guardian throttle from a charge-guard ceiling from a mode preset.
//
// Owner travels as a REQUIRED parameter on ITdpController.ApplyAsync rather than being set here by
// convention: a caller that forgets is then a compile error, not a silently anonymous write. That is
// the same reasoning as the auditing decorators — "remember to record this" is the class of
// instruction this codebase has watched fail twice.
namespace GpdForge.Tdp;

/// <summary>Well-known owners. Strings rather than an enum because they are wire-visible in
/// <c>GET /tdp</c> and in the audit log; an ordinal would be meaningless to a client.</summary>
public static class TdpOwner
{
    public const string Mode = "mode";                       // POST /mode, and the auto-profile worker
    public const string Manual = "manual";                   // POST /tdp
    public const string Panic = "panic";                     // POST /panic
    public const string ThermalGuardian = "thermal-guardian"; // guardian throttle
    public const string ChargeGuard = "charge-guard";         // cool-while-charging ceiling
    public const string AutoFps = "auto-fps";                 // the FPS→TDP governor
    public const string Tuner = "tuner";                      // auto-tuner sweep
    public const string Restore = "restore";                  // clearing a throttle back to the mode preset
    public const string ResumeRestore = "resume-restore";     // the post-suspend re-apply
}

/// <summary>
/// The last TDP write and its provenance. Written by the auditing decorator, so every caller is
/// covered by construction.
/// </summary>
/// <param name="Verified">
/// NULLABLE on purpose, and three-valued like the audit log: true (read back and matched), false (the
/// firmware refused), and null (no readback was possible — a stub backend, or a reader that returned
/// nothing). Collapsing null into true is what <c>tdpVerified: true</c> was doing.
/// </param>
/// <param name="Backend">
/// Which backend serviced the write. Visible on the wire because it has to be: with the hardware gate
/// closed the stub echoes back whatever it was handed, so it "verifies" every time. An endpoint that
/// reported verified without saying the backend is a stub would be a liar with a timestamp.
/// </param>
public readonly record struct TdpSnapshot(
    TdpProfile Requested,
    TdpReadout Observed,
    bool? Verified,
    int Attempts,
    string Owner,
    string Backend,
    DateTimeOffset AtUtc);

public sealed class TdpState
{
    private readonly Lock _gate = new();
    private TdpSnapshot? _last;

    /// <summary>The last write, or null when nothing has written TDP since the daemon started.
    /// Null rather than a zeroed snapshot: "nothing has happened yet" is a real answer.</summary>
    public TdpSnapshot? Last { get { lock (_gate) return _last; } }

    public void Record(TdpSnapshot snapshot)
    {
        lock (_gate) _last = snapshot;
    }
}
