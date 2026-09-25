// GPD Forge — per-mode processor power policy: the pure half. GPL-3.0-or-later.
//
// Roadmap H4 / plan F4. TDP says how many watts the SoC may draw; these three Windows settings say how
// it SPENDS them, and with a fixed 22-25 W ceiling that matters. An aggressive boost policy chases
// single-core spikes to the top clock, burns the budget in heat the fan then has to remove, and the
// guardian claws it back a few seconds later — frame-time spikes for nothing. "Efficient enabled"
// boost plus a performance-leaning EPP keeps the clocks where the sustained budget can hold them.
//
// Everything here is pure (no process, no registry) so the table and the parser are unit-tested; the
// side effects live in PowerPolicyService.
//
// GUIDs, never aliases or names: `powercfg /q` is localised (this device answers in Spanish), and the
// only parts of its output that do not translate are the GUIDs and the hex values. The parser keys on
// exactly those, which is the same lesson HibernatePolicy learned.
using System.Globalization;
using System.Text.RegularExpressions;
using GpdForge.Profiles;

namespace GpdForge.Power;

/// <summary>One power source's processor policy. <paramref name="Epp"/> 0 = performance, 100 = energy;
/// <paramref name="BoostMode"/> is the PERFBOOSTMODE index; <paramref name="MaxProcessorState"/> is %.</summary>
public sealed record ProcessorSettings(int Epp, int BoostMode, int MaxProcessorState);

/// <summary>The policy for both power sources. Windows keeps AC and DC separately; so does this.</summary>
public sealed record ProcessorPolicy(ProcessorSettings Ac, ProcessorSettings Dc);

public static partial class ProcessorPowerPolicy
{
    public const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    public const string PerfEpp = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
    public const string PerfBoostMode = "be337238-0d82-4146-a960-4f3749d470c7";
    public const string ProcThrottleMax = "bc5038f7-23e0-4960-96da-33abaf5935ec";

    // PERFBOOSTMODE indices, as `powercfg /q` lists them.
    public const int BoostDisabled = 0;
    public const int BoostEnabled = 1;
    public const int BoostAggressive = 2;
    public const int BoostEfficientEnabled = 3;

    /// <summary>The three settings in write order, paired with how each is read off a policy.</summary>
    public static readonly IReadOnlyList<(string Guid, string Name, Func<ProcessorSettings, int> Pick)> Settings =
    [
        (PerfEpp, "EPP", s => s.Epp),
        (PerfBoostMode, "boost mode", s => s.BoostMode),
        (ProcThrottleMax, "max processor state", s => s.MaxProcessorState),
    ];

    /// <summary>
    /// What a mode wants. Null means "leave Windows alone" — standby is a restore state, not a usage
    /// mode, and an unknown name must not guess.
    /// </summary>
    public static ProcessorPolicy? Plan(string? mode)
    {
        var both = (ProcessorSettings s) => new ProcessorPolicy(s, s);
        return mode switch
        {
            // Efficient boost, not aggressive: with a fixed TDP an aggressive boost spends the budget
            // on single-core spikes that the sustained limit then takes back — heat, fan noise and a
            // frame-time hitch in exchange for nothing. EPP 33 still leans the governor to performance.
            ModeCatalogue.Gaming => both(new(33, BoostEfficientEnabled, 100)),
            // The frame cap is the lever there; EPP 50 stops the idle part of each frame from racing.
            ModeCatalogue.GamingBattery => both(new(50, BoostEfficientEnabled, 100)),
            // Sustained: agents and inference want steady all-core throughput, not bursts — the TDP
            // preset is already flat (25/25/25) for the same reason.
            ModeCatalogue.Ai => both(new(25, BoostEfficientEnabled, 100)),
            ModeCatalogue.Windows => both(new(50, BoostEfficientEnabled, 100)),
            // On battery boost is off entirely: a boost burst costs more energy per unit of work than
            // it saves in time, and nothing in this mode is latency-bound. On AC it stays efficient so
            // plugging in does not make a desktop feel stuck.
            ModeCatalogue.Battery => new ProcessorPolicy(
                Ac: new(80, BoostEfficientEnabled, 100),
                Dc: new(80, BoostDisabled, 100)),
            _ => null,
        };
    }

    /// <summary>The first GUID in `powercfg /getactivescheme` output, or null.</summary>
    public static string? ParseActiveScheme(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = GuidPattern().Match(output);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// The current AC and DC values of one setting from `powercfg /q` output, or null when that
    /// setting's block is absent or incomplete. Locale-free: the block starts at the setting's GUID and
    /// its last two lines are always the current AC then DC index, as hex.
    /// </summary>
    public static (int Ac, int Dc)? ParseSetting(string? output, string settingGuid)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var start = output.IndexOf(settingGuid, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        // The block ends at the start of the LINE holding the next GUID (setting or scheme) — cutting
        // at the GUID itself would leave that line's localised label as the block's last line.
        var rest = output[(start + settingGuid.Length)..];
        var next = GuidPattern().Match(rest);
        var block = next.Success ? rest[..Math.Max(0, rest.LastIndexOf('\n', next.Index))] : rest;

        // The LAST TWO non-empty lines, each ending in a hex value — not merely the last two hex values
        // anywhere: a truncated read that lost the DC line would otherwise pair the range increment
        // with the AC value and report it as a valid (AC, DC) reading.
        var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return null;
        var ac = TrailingHex().Match(lines[^2]);
        var dc = TrailingHex().Match(lines[^1]);
        if (!ac.Success || !dc.Success) return null;
        return (ParseHex(ac.Groups[1].Value), ParseHex(dc.Groups[1].Value));
    }

    /// <summary>
    /// The powercfg argument lines that make <paramref name="scheme"/> hold <paramref name="policy"/>,
    /// ending with /setactive on the SAME scheme: editing a scheme's values does not reach the power
    /// manager until it is (re)activated, and re-activating the active scheme switches nothing.
    /// </summary>
    public static IReadOnlyList<string> BuildWriteArgs(string scheme, ProcessorPolicy policy)
    {
        var args = new List<string>(7);
        foreach (var (guid, _, pick) in Settings)
        {
            args.Add($"/setacvalueindex {scheme} {SubProcessor} {guid} {pick(policy.Ac).ToString(CultureInfo.InvariantCulture)}");
            args.Add($"/setdcvalueindex {scheme} {SubProcessor} {guid} {pick(policy.Dc).ToString(CultureInfo.InvariantCulture)}");
        }
        args.Add($"/setactive {scheme}");
        return args;
    }

    /// <summary>The query that reads one setting of one scheme.</summary>
    public static string QueryArgs(string scheme, string settingGuid) => $"/q {scheme} {SubProcessor} {settingGuid}";

    private static int ParseHex(string digits) => int.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"0x([0-9a-fA-F]{1,8})$")]
    private static partial Regex TrailingHex();
}
