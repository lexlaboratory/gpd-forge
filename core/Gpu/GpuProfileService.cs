// GPD Forge — AMD GPU profile control: availability first, settings second. GPL-3.0-or-later.
//
// The feature this serves is "one switch that sets the Radeon settings, and does it automatically per
// app". The automatic half already exists and is not rebuilt here: FocusProfileEngine watches the
// foreground process with anti-flapping hysteresis and AppRuleStore holds the per-app rules. What was
// missing is a GPU backend for those rules to drive.
//
// This layer answers exactly one question for now — CAN we drive the Radeon settings on this machine,
// and how do we know — because that answer gates everything else and is the part that can be verified
// on real hardware today. The 3D settings themselves (Anti-Lag, Chill, Boost, Image Sharpening) are
// built on top of a foundation that has been proven, not alongside one that has been assumed.
//
// When ADLX is unavailable the UI hides the section entirely rather than showing a disabled control.
// That is a deliberate choice: this project has spent real time removing switches that looked live and
// did nothing, and a greyed-out row still implies "nearly working" when the honest answer is "this
// machine cannot do it".
using Microsoft.Extensions.Logging;

namespace GpdForge.Gpu;

/// <summary>
/// A named set of Radeon settings. Not applied yet — defined here so the shape is settled and the
/// mutual-exclusion rule below has somewhere to live.
/// </summary>
/// <remarks>
/// AMD documents that Radeon Chill cannot be enabled at the same time as Radeon Boost, Radeon
/// Anti-Lag, or Anti-Lag Next. A profile that sets both is not a preference the driver will merge —
/// it is a request it will partly refuse, silently. Modelling the constraint here means the conflict
/// is reported to the user instead of discovered as "the switch did not take".
/// </remarks>
public sealed record GpuProfile(string Name, bool AntiLag = false, bool Chill = false, bool Boost = false)
{
    /// <summary>Null when the combination is legal; otherwise why it is not.</summary>
    public string? Conflict =>
        Chill && (Boost || AntiLag)
            ? "Radeon Chill cannot be on at the same time as Boost or Anti-Lag — AMD's driver refuses "
              + "the combination, so this profile would only partly apply."
            : null;
}

/// <summary>What the daemon can say about GPU profile support right now.</summary>
public sealed record GpuProfileStatus(
    bool Available,
    string Status,
    string? AdlxVersion,
    string Detail,
    string? AdapterName);

/// <summary>Reads the machine's RAM so the ADLX vtable canary has something to be checked against.
/// Injected so the check is testable without WMI.</summary>
public interface ISystemMemoryProbe
{
    /// <summary>Installed RAM in MB, or 0 when it could not be read (never a guess).</summary>
    uint TotalRamMb();
}

/// <summary>
/// Decides whether GPU profile control is available, and says why when it is not.
/// </summary>
public sealed class GpuProfileService
{
    /// <summary>Own gate, separate from GPDFORGE_ENABLE_HARDWARE on purpose: ADLX is a user-mode
    /// driver API with nothing to do with the MSR/EC paths, and a fault here must not be able to take
    /// down power control that has been validated on the metal.</summary>
    public const string GateVariable = "GPDFORGE_ENABLE_GPU_PROFILES";

    private readonly ISystemMemoryProbe _memory;
    private readonly ILogger<GpuProfileService>? _logger;
    private readonly Func<string, string?> _readEnv;
    private readonly Func<ILogger?, AdlxInterop> _makeInterop;

    private GpuProfileStatus? _cached;

    public GpuProfileService(
        ISystemMemoryProbe memory,
        ILogger<GpuProfileService>? logger = null,
        Func<string, string?>? readEnv = null,
        Func<ILogger?, AdlxInterop>? makeInterop = null)
    {
        _memory = memory;
        _logger = logger;
        _readEnv = readEnv ?? Environment.GetEnvironmentVariable;
        _makeInterop = makeInterop ?? (l => new AdlxInterop(l));
    }

    public bool GateOpen => _readEnv(GateVariable) == "1";

    /// <summary>
    /// Probe once and remember. Cached because initialising a driver library on every request is both
    /// wasteful and a good way to find its reentrancy bugs; the answer cannot change without a driver
    /// change, which means a restart.
    /// </summary>
    public GpuProfileStatus Status(string? adapterName = null)
    {
        if (_cached is not null) return _cached;

        if (!GateOpen)
        {
            _cached = new GpuProfileStatus(false, "Disabled", null,
                $"GPU profile control is off. Set {GateVariable}=1 on the service to enable it.", adapterName);
            return _cached;
        }

        using var adlx = _makeInterop(_logger);
        var probe = adlx.Initialise(_memory.TotalRamMb());

        var available = probe.Status == AdlxStatus.Ready;
        if (!available)
            _logger?.LogInformation("GPU profile control unavailable: {Status} — {Detail}", probe.Status, probe.Detail);

        _cached = new GpuProfileStatus(available, probe.Status.ToString(), probe.Version, probe.Detail, adapterName);
        return _cached;
    }

    /// <summary>Drop the cached answer. For the probe CLI, which wants to measure rather than recall.</summary>
    public void Forget() => _cached = null;
}
