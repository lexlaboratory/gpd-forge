// GPD Forge — first-run onboarding: incumbent power-controller check (pure logic). GPL-3.0-or-later.
namespace GpdForge.Onboarding;

/// <summary>Whether a competing power controller is running, shaped for the setup wizard's
/// incumbent-check step (see docs/api.md — GET /system/incumbents).</summary>
public readonly record struct IncumbentsStatus(bool MotionAssistant, bool GpdTool);

/// <summary>
/// Pure mapping from the raw process names GpdForge.Profiles.IPowerControllerDetector reports to the
/// two booleans the wizard cares about. Kept separate from ProcessPowerControllerDetector (whose
/// Process.GetProcessesByName call is real OS access and not unit-testable) so this mapping is.
/// </summary>
public static class IncumbentsCheck
{
    public static IncumbentsStatus From(IEnumerable<string> runningControllerNames)
    {
        var set = new HashSet<string>(runningControllerNames, StringComparer.OrdinalIgnoreCase);
        bool motionAssistant = set.Contains("MotionAssistant") || set.Contains("pmgui");
        bool gpdTool = set.Contains("GPDTool") || set.Contains("GPDToolService");
        return new IncumbentsStatus(motionAssistant, gpdTool);
    }
}
