// GPD Forge — a fixed GPU clock. GPL-3.0-or-later.
//
// Measured on this device (GPD Win 4, Ryzen AI 9 HX 370) on 2026-10-13:
//   - ADLX 1.5 offers no clock tuning for the Radeon 890M: every IsSupported*Tuning answered false, so
//     neither Adrenalin nor ADLX can set a GPU clock here.
//   - PSMU 0x89 (RyzenAdj's --gfx-clk, the mechanism Motion Assistant's GPUClock uses) held the GPU at
//     exactly 1200 MHz. It FIXES the clock — it is not a ceiling — and stays fixed at idle.
//   - No message is known that hands the clock back to the driver. 0 is not "automatic": it pinned the
//     GPU at 0 MHz, a graphics-driver reset did not undo it, and only a restart did. Handheld
//     Companion's source says the same ("you can't restore default frequency on AMD GPUs").
// So a fixed clock is a state that lasts until the handheld restarts. This service records it with the
// boot it was set in, so a restart reads as automatic again rather than as a stale claim.
//
// "Restart" means Restart. With Windows Fast Startup, Shut down hibernates the kernel session: the
// tick count, this service and its state survive the power cycle while the SMU comes back automatic.
// ResumeRestoreWorker sees that as a resume and re-sends the clock, so what is reported stays true —
// the clock is fixed again — and the way back to automatic is still a Restart (review, 2026-10-13).
using System.Text.Json;

namespace GpdForge.Gpu;

/// <summary>Something that can fix the GPU clock. Implemented by the PawnIO Strix backend.</summary>
public interface IGpuClockControl
{
    bool Supported { get; }

    /// <summary>Fixes the GPU clock at <paramref name="mhz"/>; returns the value the SMU handed back.</summary>
    Task<int> ForceAsync(int mhz, CancellationToken ct);
}

/// <summary>No backend that can do it (no hardware gate, or a backend other than pawnio-strix).</summary>
public sealed class NoGpuClockControl : IGpuClockControl
{
    public bool Supported => false;
    public Task<int> ForceAsync(int mhz, CancellationToken ct) =>
        throw new NotSupportedException("Fixing the GPU clock needs the pawnio-strix TDP backend.");
}

public static class GpuClockPolicy
{
    /// <summary>Never 0 (that pinned the 890M at 0 MHz until a restart) and nothing near it: below
    /// 400 MHz the desktop itself crawls.</summary>
    public const int MinMhz = 400;

    /// <summary>The Radeon 890M's rated maximum.</summary>
    public const int MaxMhz = 2900;

    public static bool InRange(int mhz) => mhz is >= MinMhz and <= MaxMhz;
}

/// <summary>A clock Forge fixed, and the boot it was fixed in.</summary>
public sealed record GpuClockForced(int Mhz, DateTimeOffset AtUtc, DateTimeOffset BootUtc);

/// <param name="ForcedMhz">The clock Forge fixed in this boot; null = automatic (the driver decides).</param>
public sealed record GpuClockView(
    bool Supported, int? ForcedMhz, DateTimeOffset? SinceUtc, int MinMhz, int MaxMhz, bool ResetsOnRestart, string Note);

/// <param name="Code">bad_mhz | not_supported | not_confirmed | smu_failed when not Ok.</param>
public sealed record GpuClockOutcome(bool Ok, string? Code, string? Error, GpuClockView View);

/// <summary>gpu-clock.json: the fixed clock and its boot. Atomic replace; unreadable means none.</summary>
public sealed class GpuClockStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public GpuClockStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _path = Path.Combine(directory, "gpu-clock.json");
    }

    public GpuClockForced? Read()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var saved = JsonSerializer.Deserialize<GpuClockForced>(File.ReadAllText(_path), Json);
            return saved is not null && GpuClockPolicy.InRange(saved.Mhz) ? saved : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public void Write(GpuClockForced forced)
    {
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(forced, Json));
        try { if (File.Exists(_path)) File.Replace(temp, _path, null); else File.Move(temp, _path); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
    }

    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class GpuClockService
{
    /// <summary>The computed boot time wobbles with clock adjustments; a restart moves it by far more.</summary>
    private static readonly TimeSpan SameBootTolerance = TimeSpan.FromMinutes(2);

    private const string Note =
        "A fixed clock stays fixed, idle included. AMD firmware has no way back to automatic: it returns " +
        "when the handheld restarts (Restart; sleep and a Fast Startup shut-down keep it, and Forge " +
        "re-applies it). 0 is not automatic — it pins the GPU at 0 MHz — so it is refused.";

    private readonly IGpuClockControl _control;
    private readonly GpuClockStore? _store;
    private readonly Func<DateTimeOffset> _now;
    private readonly DateTimeOffset _boot;
    private readonly Lock _gate = new();
    private GpuClockForced? _forced;

    public GpuClockService(IGpuClockControl control, GpuClockStore? store = null,
        Func<DateTimeOffset>? now = null, Func<DateTimeOffset>? bootUtc = null)
    {
        _control = control;
        _store = store;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _boot = (bootUtc ?? CurrentBootUtc)();

        var saved = store?.Read();
        if (saved is not null && (saved.BootUtc - _boot).Duration() <= SameBootTolerance) _forced = saved;
        else if (saved is not null) store!.Clear();   // set before a restart: the clock is automatic again
    }

    /// <summary>GetTickCount64 counts sleep and hibernation, so this holds still across a suspend.</summary>
    public static DateTimeOffset CurrentBootUtc() =>
        DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public GpuClockForced? Forced { get { lock (_gate) return _forced; } }

    public GpuClockView View()
    {
        var forced = Forced;
        return new GpuClockView(_control.Supported, forced?.Mhz, forced?.AtUtc,
            GpuClockPolicy.MinMhz, GpuClockPolicy.MaxMhz, ResetsOnRestart: true, Note);
    }

    public async Task<GpuClockOutcome> SetAsync(int mhz, CancellationToken ct)
    {
        if (!GpuClockPolicy.InRange(mhz))
            return Fail("bad_mhz", $"The GPU clock must be {GpuClockPolicy.MinMhz}–{GpuClockPolicy.MaxMhz} MHz.");
        if (!_control.Supported)
            return Fail("not_supported", "Fixing the GPU clock needs the pawnio-strix TDP backend on this handheld.");

        int echoed;
        try { echoed = await _control.ForceAsync(mhz, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("smu_failed", $"The SMU did not take the GPU clock: {ex.Message}");
        }
        if (echoed != mhz)
            return Fail("not_confirmed", $"The SMU answered {echoed} for {mhz} MHz, so the clock is not claimed as set.");

        var forced = new GpuClockForced(mhz, _now(), _boot);
        lock (_gate) _forced = forced;
        try { _store?.Write(forced); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* holds for this run */ }
        return new GpuClockOutcome(true, null, null, View());
    }

    /// <summary>Re-sends the fixed clock (after a resume). False when none is fixed or it failed.</summary>
    public async Task<bool> ReapplyAsync(CancellationToken ct)
    {
        var forced = Forced;
        if (forced is null || !_control.Supported) return false;
        try { return await _control.ForceAsync(forced.Mhz, ct) == forced.Mhz; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    private GpuClockOutcome Fail(string code, string error) => new(false, code, error, View());
}
