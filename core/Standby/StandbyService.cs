// GPD Forge — the Standby Doctor service behind GET /standby and POST /standby/restore.
// GPL-3.0-or-later.
//
// Every field this exposes is either measured or null. There is no "reasonable default" anywhere in
// here: an unreadable powercfg reports itself unavailable (not "no blockers"), an unobserved night
// reports no drain (not 0 %/h), and a restore step that ran against a stub backend reports that it
// restored nothing (not success). The panel is allowed to say "not measured"; it is not allowed to
// make a number up.
using GpdForge.Fan;
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
    StandbyRestoreOutcome? LastRestore);

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
    private readonly ITelemetryService _telemetry;
    private readonly ILogger<StandbyService>? _logger;
    private readonly StandbyDoctor _doctor;
    private readonly StandbyDrainTracker _tracker;
    private readonly IUnbiasedClock _clock;
    private readonly Func<DateTimeOffset> _now;

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
        StandbyDrainTracker? tracker = null)
    {
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
        _tdpBackendReal = tdpBackend is not StubTdpBackend;
    }

    public async Task<StandbyStatus> GetStatusAsync(CancellationToken ct)
    {
        var diagnosis = await _doctor.DiagnoseDetailedAsync(ct);
        var drain = _tracker.Last;

        return new StandbyStatus(
            LastDrainPctPerHour: drain?.PctPerHour,
            LastDrainSleptHours: drain?.SleptHours,
            LastDrainAt: drain?.At,
            TopWakeReason: diagnosis.LastWakeReason,
            Blockers: diagnosis.SleepBlockers,
            DiagnosticsAvailable: diagnosis.Available,
            DiagnosticsError: diagnosis.Error,
            LastRestore: _lastRestore);
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
            // Not implemented, so it is listed as not restored rather than quietly omitted: the panel
            // should show what a resume restore does NOT yet cover.
            new("hid", false, "HID/controller re-enumeration has no backend yet — nothing was restored."),
        };

        var outcome = new StandbyRestoreOutcome(_now(), steps);
        _lastRestore = outcome;
        return outcome;
    }

    private async Task<StandbyRestoreStep> RestoreFanAsync(CancellationToken ct)
    {
        if (!_fanBackendReal)
            return new("fan", false, "No EC fan backend is wired (GPDFORGE_ENABLE_HARDWARE is off or the board is unmatched).");
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
