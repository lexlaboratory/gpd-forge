// GPD Forge — the body of GET /mode. GPL-3.0-or-later.
//
// 2026-10-10 on this device: the dashboard said "Auto — optimizing for the app in focus" over a gaming
// mode at 25 W, and nothing said that a rule on Orca, the workspace app, had chosen it. The rule that
// switched the mode is named here, so the screen can say who changed the power and why.
using GpdForge.Profiles;

namespace GpdForge.Api;

/// <summary>The app rule that switched to the mode in force.</summary>
public sealed record ModeDecision(Guid RuleId, string? Match, string? Process);

/// <param name="Active">The mode in force.</param>
/// <param name="Auto">Whether the daemon may switch it on its own (<see cref="ModeState.Auto"/>).</param>
/// <param name="DecidedBy">The rule that chose it; null for the user's pick, the power-source default,
/// or with Auto off.</param>
public sealed record ModeView(string Active, bool Auto, ModeDecision? DecidedBy)
{
    public static ModeView From(ModeState mode, AppRuleMatch? lastMatch)
    {
        var (active, auto, chosenByUser) = mode.Snapshot();
        // LastMatch is what the app in front resolves to, recorded every tick even while the user's own
        // pick holds; it chose the mode only when that mode came from an automatic switch and agrees.
        var decidedBy = auto && !chosenByUser
            && lastMatch is { RuleId: Guid ruleId } match
            && string.Equals(match.Mode, active, StringComparison.OrdinalIgnoreCase)
                ? new ModeDecision(ruleId, match.Match, match.Process)
                : null;
        return new ModeView(active, auto, decidedBy);
    }
}
