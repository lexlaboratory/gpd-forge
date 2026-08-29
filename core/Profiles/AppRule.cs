// GPD Forge — per-app profile rule model. GPL-3.0-or-later.
//
// A rule says "while this process is in the foreground, run in this mode". Rules are ordered:
// the first enabled rule whose Match is a substring of the foreground process name wins.
namespace GpdForge.Profiles;

/// <param name="Match">Normalized (lowercase, no ".exe") process-name fragment.</param>
public sealed record AppRule(Guid Id, string Match, string Mode, bool Enabled);

/// <summary>
/// The last resolution the focus worker performed. <see cref="RuleId"/> is null when no rule
/// matched and the mode came from the AC/battery fallback — the UI has to be able to say
/// "nothing matched, this is just the power default" instead of implying a rule is in charge.
/// </summary>
public sealed record AppRuleMatch(
    Guid? RuleId,
    string? Match,
    string Mode,
    string? Process,
    bool AcConnected,
    DateTimeOffset AtUtc);

/// <summary>Anything that can turn a foreground process name into a mode.</summary>
public interface IModeResolver
{
    /// <summary>The mode this process implies, or null if it has no opinion.</summary>
    string? ModeFor(string? processName);
}
