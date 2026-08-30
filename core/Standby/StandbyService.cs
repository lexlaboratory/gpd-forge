// GPD Forge — the Standby Doctor service behind GET /standby and POST /standby/restore.
// GPL-3.0-or-later.
//
// Every field this exposes is either measured or null. There is no "reasonable default" anywhere in
// here: an unreadable powercfg reports itself unavailable (not "no blockers"), an unobserved night
// reports no drain (not 0 %/h), and a restore step that ran against a stub backend reports that it
// restored nothing (not success). The panel is allowed to say "not measured"; it is not allowed to
// make a number up.
using GpdForge.Fan;
using GpdForge.Hid;
using GpdForge.Tdp;
using GpdForge.Telemetry;
using Microsoft.Extensions.Logging;

namespace GpdForge.Standby;

/// <summary>One thing a resume restore tried to do, and whether it actually happened.</summary>
public sealed record StandbyRestoreStep(string Name, bool Restored, string Detail);

public sealed record StandbyRestoreOutcome(DateTimeOffset At, IReadOnlyList<StandbyRestoreStep> Steps)
{
    public bool AnyRestored => Steps.Any(s => s.Restored);
}

/// <summary>The wire shape of GET /standby. Nulls mean "not measured", never "zero".</summary>
public sealed record StandbyStatus(
    double? LastDrainPctPerHour,
    double? LastDrainSleptHours,
    DateTimeOffset? LastDrainAt,
    string? TopWakeReason,
    IReadOnlyList<string> Blockers,
    bool DiagnosticsAvailable,
    string? DiagnosticsError,
    StandbyRestoreOutcome? LastRestore,
    // Three states, deliberately not two: SleepStudy null with no error means the background sampler
    // has not run yet, which is not the same as powercfg refusing, which is not the same as a clean
    // report with nothing in it.
    SleepStudySummary? SleepStudy = null,
    string? SleepStudyError = null);

public interface IStandbyService
{
    Task<StandbyStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Re-applies what the EC/SMU forget across a suspend. Never throws.</summary>
    Task<StandbyRestoreOutcome> RestoreAsync(TdpProfile? activeProfile, CancellationToken ct);

    /// <summary>Takes one battery sample; the drain measurement falls out of a pair of these.</summary>
    Task SampleAsync(CancellationToken ct);
}

public sealed class StandbyService : IStandbyService
{
    private readonly ITdpController _tdp;
    private readonly IFanController _fan;
    // The REAL write path. StandbyService was built against the phase-0 IFanController, which is
    // registered as a stub and always will be — so the fan step of every resume restore reported
    // "no EC fan backend is wired" while the rest of the daemon was reading 4608 RPM and reporting
    // controllable:true through IGpdFanController. Two interfaces for one fan, and the restore held
    // the dead one. Optional so the type stays constructible in tests that do not care.
    private readonly IGpdFanController? _gpdFan;
    private readonly ITelemetryService _telemetry;
    private readonly ILogger<StandbyService>? _logger;
    private readonly StandbyDoctor _doctor;
    private readonly StandbyDrainTracker _tracker;
    private readonly IUnbiasedClock _clock;
    private readonly Func<DateTimeOffset> _now;
    private readonly SleepStudyCache? _sleepStudy;
    private readonly HidReenumerator? _hid;

    // Whether a write would reach real silicon. The controller interfaces cannot answer this — the
    // no-hardware stubs satisfy them and even report TDP as "verified", because they echo back
    // whatever was asked for. Reporting that as a restore would be exactly the lie this file exists
    // to remove, so the concrete types are inspected once, here.
    private readonly bool _fanBackendReal;
    private readonly bool _tdpBackendReal;

    private StandbyRestoreOutcome? _lastRestore;

    public StandbyService(
        ITdpController tdp,
        ITdpBackend tdpBackend,
        IFanController fan,
        ITelemetryService telemetry,
        ILogger<StandbyService>? logger = null,
        IProcessRunner? runner = null,
        IUnbiasedClock? clock = null,
        Func<DateTimeOffset>? now = null,
        StandbyDrainTracker? tracker = null,
        SleepStudyCache? sleepStudy = null,
        HidReenumerator? hid = null,
        IGpdFanController? gpdFan = null)
    {
        _sleepStudy = sleepStudy;
        _hid = hid;
        _tdp = tdp;
        _fan = fan;
        _telemetry = telemetry;
        _logger = logger;
        // IProcessRunner is only registered when the hardware gate is open, but powercfg is a
        // read-only, unprivileged query that must work either way.
        _doctor = new StandbyDoctor(runner ?? new SystemProcessRunner(), tdp, fan);
        _tracker = tracker ?? new StandbyDrainTracker();
        _clock = clock ?? new Win32UnbiasedClock();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _fanBackendReal = fan is not StubFanController;
        _gpdFan = gpdFan;
        _tdpBackendReal = tdpBackend is not StubTdpBackend;
    }

    public async Task<StandbyStatus> GetStatusAsync(CancellationToken ct)
    {
        var diagnosis = await _doctor.DiagnoseDetailedAsync(ct);
        var drain = _tracker.Last;
        // Never generated here: the report costs tens of seconds. This is whatever SleepStudyWorker
        // last managed to produce, or nothing at all if it has not run yet.
        var (studyRan, study, studyError) = _sleepStudy?.Read() ?? (false, null, null);

        return new StandbyStatus(
            LastDrainPctPerHour: drain?.PctPerHour,
            LastDrainSleptHours: drain?.SleptHours,
            LastDrainAt: drain?.At,
            TopWakeReason: diagnosis.LastWakeReason,
            Blockers: diagnosis.SleepBlockers,
            DiagnosticsAvailable: diagnosis.Available,
            DiagnosticsError: diagnosis.Error,
            LastRestore: _lastRestore,
            SleepStudy: study,
            SleepStudyError: studyRan ? studyError : null);
    }

    public async Task SampleAsync(CancellationToken ct)
    {
        try
        {
            var unbiased = _clock.Read();
            if (unbiased is null) return;   // without a sleep-excluding clock a suspend is unprovable

            var snapshot = await _telemetry.ReadAsync(ct);
            var measured = _tracker.Observe(_now(), unbiased.Value, snapshot.BatteryPct, snapshot.AcConnected);
            if (measured is not null)
            {
                _logger?.LogInformation(
                    "Standby drain measured: {Pct}%/h ({From}% -> {To}% over {Hours} h asleep).",
                    measured.PctPerHour, measured.FromPct, measured.ToPct, measured.SleptHours);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger?.LogDebug(ex, "standby battery sample failed"); }
    }

    /// <summary>
    /// Fan first, then TDP: the EC comes back from suspend uninitialized, and re-applying power
    /// limits against an uninitialized EC is how the Win 4 ends up hot and silent.
    /// </summary>
    public async Task<StandbyRestoreOutcome> RestoreAsync(TdpProfile? activeProfile, CancellationToken ct)
    {
        var steps = new List<StandbyRestoreStep>
        {
            await RestoreFanAsync(ct),
            await RestoreTdpAsync(activeProfile, ct),
            await RestoreHidAsync(ct),
        };

        var outcome = new StandbyRestoreOutcome(_now(), steps);
        _lastRestore = outcome;
        return outcome;
    }

    private async Task<StandbyRestoreStep> RestoreFanAsync(CancellationToken ct)
    {
        // Prefer the real EC controller. The old path is kept only for callers constructed without
        // one, and its message no longer blames the hardware gate for a wiring problem: saying
        // "GPDFORGE_ENABLE_HARDWARE is off" while it is demonstrably on sent a whole investigation
        // toward the wrong cause.
        if (_gpdFan is { Available: true })
        {
            try
            {
                // Hand the EC back to firmware control. After a suspend the EC comes back
                // uninitialised, and AUTOMATIC is the safe state to land in: it is what the board
                // does on its own, and it cannot leave the fan pinned at a duty nobody chose.
                // ForgeWorker's curve takes over again on its next tick if a manual mode is selected.
                _gpdFan.SetAuto();

                // Read the duty back rather than trusting a void call. SetAuto cannot report failure
                // by contract, so the only evidence that the EC is listening is that it answers at
                // all — and "the EC answered" is a weaker claim than "the write verified", which is
                // why the wording below says exactly that and no more.
                var duty = _gpdFan.ReadDuty();
                return duty is not null
                    ? new("fan", true, $"EC fan handed back to AUTOMATIC after resume; the EC responded (duty reads {duty}). The mode's curve resumes on the next tick.")
                    : new("fan", false, "AUTOMATIC was commanded but the EC did not answer a read-back, so it cannot be confirmed. The fan is on the firmware curve.");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Standby restore: EC fan re-init failed: {Error}", ex.Message);
                return new("fan", false, $"EC fan re-init failed: {ex.Message}");
            }
        }

        if (_gpdFan is not null)
            return new("fan", false,
                "The EC fan controller is present but not available — the board did not match, or the "
                + "EC port could not be opened. Fan control is left to the firmware.");

        if (!_fanBackendReal)
            return new("fan", false, "No EC fan backend is wired into this daemon build.");

        try
        {
            await _fan.InitializeAsync(ct);
            return new("fan", true, "EC fan controller re-initialised.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Standby restore: fan re-init failed: {Error}", ex.Message);
            return new("fan", false, $"Fan re-init failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-enumerates the controller — but only if Windows reports it faulted. A pad that survived
    /// the suspend is left alone: restarting a working controller mid-game would be a worse bug than
    /// the one this step exists to fix, so "nothing was wrong" is reported as a success with the
    /// reason, not as a repair.
    /// </summary>
    private async Task<StandbyRestoreStep> RestoreHidAsync(CancellationToken ct)
    {
        if (_hid is null)
            return new("hid", false, "No HID backend is wired, so the controller was not checked.");
        try
        {
            var result = await _hid.RestoreAsync(ct);
            return new("hid", result.Healthy, result.Detail);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Standby restore: HID re-enumeration failed: {Error}", ex.Message);
            return new("hid", false, $"Controller re-enumeration failed: {ex.Message}");
        }
    }

    private async Task<StandbyRestoreStep> RestoreTdpAsync(TdpProfile? profile, CancellationToken ct)
    {
        if (profile is null)
            return new("tdp", false, "No TDP profile is defined for the active mode.");
        if (!_tdpBackendReal)
            return new("tdp", false, "The TDP backend is the no-hardware stub; no power limit was written.");
        try
        {
            var result = await _tdp.ApplyAsync(profile.Value, ct);
            return result.Verified
                ? new StandbyRestoreStep("tdp", true, $"STAPM {profile.Value.StapmW} W re-applied and read back.")
                : new StandbyRestoreStep("tdp", false, $"STAPM {profile.Value.StapmW} W was written but did not read back after {result.Attempts} attempts.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Standby restore: TDP re-apply failed: {Error}", ex.Message);
            return new("tdp", false, $"TDP re-apply failed: {ex.Message}");
        }
    }
}
