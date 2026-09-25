// GPD Forge — per-mode processor power policy: the side-effecting half. GPL-3.0-or-later.
//
// Writes the plan from ProcessorPowerPolicy into the ACTIVE scheme with powercfg, reads it back, and
// records every write in the hardware audit log. Three rules shape it:
//
//  - Only the active scheme, resolved to its GUID once per apply. SCHEME_CURRENT is never used for a
//    write: if the user switched schemes between two powercfg calls, an alias would split one apply
//    across two schemes. Other schemes are never read or written.
//  - The originals are captured BEFORE the first write, once per scheme, and never overwritten by a
//    later capture — a second capture would record GPD Forge's own values as "what was there", and the
//    uninstall would then restore Forge's policy onto a machine without Forge.
//  - Verified by readback, not assumed. powercfg can accept a value and leave the scheme unchanged
//    (policy-managed machines, a vendor tool re-writing it); the audit says which.
//
// Runs through IProcessRunner, whose SystemProcessRunner starts powercfg hidden (CreateNoWindow): a
// console flash on every mode switch is exactly the kind of thing that makes a daemon feel broken.
using System.Text.Json;
using GpdForge.Broker;
using GpdForge.Tdp;
using Microsoft.Extensions.Logging;

namespace GpdForge.Power;

public sealed record PowerPolicyApply(DateTimeOffset AtUtc, string Mode, string? Scheme, bool Verified, string Detail);

public sealed record PowerPolicyReadout(string? Scheme, ProcessorPolicy? Current, string? Detail);

public sealed class PowerPolicyService(
    IProcessRunner runner,
    string originalsPath,
    HardwareAuditLog? audit = null,
    ILogger<PowerPolicyService>? logger = null,
    TimeProvider? time = null)
{
    private const string Powercfg = "powercfg.exe";
    private const string Subsystem = "power-policy";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The last apply attempt, for GET /power-policy. Null until one ran.</summary>
    public PowerPolicyApply? LastApply { get; private set; }

    public bool OriginalsCaptured => File.Exists(originalsPath);

    /// <summary>The active scheme and its current processor policy, read fresh.</summary>
    public async Task<PowerPolicyReadout> ReadAsync(CancellationToken ct)
    {
        var scheme = await ActiveSchemeAsync(ct);
        if (scheme is null) return new PowerPolicyReadout(null, null, "The active power scheme could not be read.");
        var current = await ReadPolicyAsync(scheme, ct);
        return new PowerPolicyReadout(scheme, current,
            current is null ? "powercfg /q did not report the processor settings of the active scheme." : null);
    }

    /// <summary>
    /// Makes the active scheme hold <paramref name="mode"/>'s plan. A mode with no plan (standby, an
    /// unknown name) writes nothing and returns null.
    /// </summary>
    public async Task<PowerPolicyApply?> ApplyAsync(string mode, CancellationToken ct)
    {
        var desired = ProcessorPowerPolicy.Plan(mode);
        if (desired is null) return null;

        await _gate.WaitAsync(ct);
        try
        {
            var scheme = await ActiveSchemeAsync(ct);
            if (scheme is null)
                return Finish(mode, null, false, "The active power scheme could not be read; nothing was written.");

            // Capture first. Without a record of what was there, a write is not reversible — so no
            // record, no write.
            var before = await ReadPolicyAsync(scheme, ct);
            if (before is null)
                return Finish(mode, scheme, false, "The processor settings of the active scheme could not be read; nothing was written.");
            if (!TryCaptureOriginals(scheme, before))
                return Finish(mode, scheme, false, "The original policy could not be saved; nothing was written.");

            if (before == desired)
                return Finish(mode, scheme, true, $"Already in force: {Describe(desired)}.");

            foreach (var line in ProcessorPowerPolicy.BuildWriteArgs(scheme, desired))
                await runner.RunAsync(Powercfg, line, ct);

            var after = await ReadPolicyAsync(scheme, ct);
            var verified = after == desired;
            return Finish(mode, scheme, verified, verified
                ? $"Applied {Describe(desired)}."
                : $"Wrote {Describe(desired)} but read back {(after is null ? "nothing" : Describe(after))}.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger?.LogWarning(e, "Processor power policy for '{Mode}' failed.", mode);
            return Finish(mode, null, false, $"powercfg failed: {e.Message}");
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Puts back every scheme's captured originals and deletes the record. Returns false when there was
    /// no record (nothing to restore) or any readback disagreed; the record is kept in that case so a
    /// second attempt still knows what to restore.
    /// </summary>
    public async Task<(bool Restored, string Detail)> RestoreAsync(CancellationToken ct)
    {
        var originals = LoadOriginals();
        if (originals is null) return (false, $"The original processor power policy record at {originalsPath} is unreadable; nothing was written.");
        if (originals.Count == 0) return (false, "No original processor power policy was recorded; nothing to restore.");

        await _gate.WaitAsync(ct);
        try
        {
            var active = await ActiveSchemeAsync(ct);
            var allVerified = true;
            foreach (var (scheme, policy) in originals)
            {
                foreach (var line in ProcessorPowerPolicy.BuildWriteArgs(scheme, policy))
                {
                    // Re-activate only the scheme that is active now: /setactive on another scheme
                    // would SWITCH the user's plan, which a restore must never do.
                    if (line.StartsWith("/setactive", StringComparison.Ordinal)
                        && !string.Equals(scheme, active, StringComparison.OrdinalIgnoreCase)) continue;
                    await runner.RunAsync(Powercfg, line, ct);
                }
                var back = await ReadPolicyAsync(scheme, ct);
                var ok = back == policy;
                allVerified &= ok;
                audit?.Record(Subsystem, "restore", $"{scheme}: {Describe(policy)}", ok, _time.GetUtcNow());
            }

            if (!allVerified) return (false, "The original policy was written but did not read back on every scheme; the record is kept.");
            try { File.Delete(originalsPath); } catch (IOException) { /* a stale record restores the same values again */ }
            return (true, $"Restored the original processor power policy on {originals.Count} scheme(s).");
        }
        finally { _gate.Release(); }
    }

    private PowerPolicyApply Finish(string mode, string? scheme, bool verified, string detail)
    {
        var result = new PowerPolicyApply(_time.GetUtcNow(), mode, scheme, verified, detail);
        LastApply = result;
        audit?.Record(Subsystem, $"apply {mode}", detail, verified, result.AtUtc);
        logger?.LogInformation("Processor power policy '{Mode}': {Detail}", mode, detail);
        return result;
    }

    private async Task<string?> ActiveSchemeAsync(CancellationToken ct) =>
        ProcessorPowerPolicy.ParseActiveScheme(await runner.RunAsync(Powercfg, "/getactivescheme", ct));

    private async Task<ProcessorPolicy?> ReadPolicyAsync(string scheme, CancellationToken ct)
    {
        var values = new List<(int Ac, int Dc)>(3);
        foreach (var (guid, _, _) in ProcessorPowerPolicy.Settings)
        {
            var output = await runner.RunAsync(Powercfg, ProcessorPowerPolicy.QueryArgs(scheme, guid), ct);
            if (ProcessorPowerPolicy.ParseSetting(output, guid) is not { } v) return null;
            values.Add(v);
        }
        return new ProcessorPolicy(
            new ProcessorSettings(values[0].Ac, values[1].Ac, values[2].Ac),
            new ProcessorSettings(values[0].Dc, values[1].Dc, values[2].Dc));
    }

    private bool TryCaptureOriginals(string scheme, ProcessorPolicy current)
    {
        // An unreadable record is NOT an empty one: overwriting it would replace the real originals
        // with whatever is in force now, which may already be GPD Forge's own policy.
        if (LoadOriginals() is not { } originals) return false;
        if (originals.ContainsKey(scheme)) return true;
        try
        {
            var updated = new Dictionary<string, ProcessorPolicy>(originals, StringComparer.OrdinalIgnoreCase) { [scheme] = current };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(originalsPath))!);
            var tmp = originalsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(updated, Json));
            File.Move(tmp, originalsPath, overwrite: true);
            audit?.Record(Subsystem, "capture originals", $"{scheme}: {Describe(current)}", true, _time.GetUtcNow());
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(e, "Could not save the original processor power policy to {Path}.", originalsPath);
            return false;
        }
    }

    /// <summary>The captured originals by scheme; empty when none were captured, null when unreadable.</summary>
    private Dictionary<string, ProcessorPolicy>? LoadOriginals()
    {
        try
        {
            if (!File.Exists(originalsPath)) return new(StringComparer.OrdinalIgnoreCase);
            var read = JsonSerializer.Deserialize<Dictionary<string, ProcessorPolicy>>(File.ReadAllText(originalsPath));
            return new(read ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogWarning(e, "The original processor power policy record at {Path} is unreadable.", originalsPath);
            return null;
        }
    }

    public static string Describe(ProcessorPolicy p) => p.Ac == p.Dc
        ? Describe(p.Ac)
        : $"AC {Describe(p.Ac)}; DC {Describe(p.Dc)}";

    private static string Describe(ProcessorSettings s) => $"EPP {s.Epp}, boost {s.BoostMode}, max {s.MaxProcessorState}%";
}

public static class PowerPolicyPaths
{
    /// <summary>Under the data root (%ProgramData%\GPD Forge), not Program Files: -Uninstall deletes
    /// Program Files, and the record must outlive that long enough for the restore to read it.</summary>
    public static string Originals(string dataRoot) => Path.Combine(dataRoot, "power-policy-originals.json");
}
