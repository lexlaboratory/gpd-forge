// GPD Forge - detects other power controllers so we never fight them. GPL-3.0-or-later.
// Two controllers applying TDP at once collapse the device (field-confirmed). GPD Forge yields
// while MotionAssistant / GPD Tool are running, and only takes over once it is the sole owner.
using System.Diagnostics;

namespace GpdForge.Profiles;

public interface IPowerControllerDetector
{
    /// <summary>True if a competing power controller is running; `names` lists them.</summary>
    bool OthersRunning(out string[] names);
}

public sealed class ProcessPowerControllerDetector : IPowerControllerDetector
{
    private static readonly string[] Watched = ["MotionAssistant", "pmgui", "GPDTool", "GPDToolService"];

    public bool OthersRunning(out string[] names)
    {
        names = Watched.Where(n => Process.GetProcessesByName(n).Length > 0).ToArray();
        return names.Length > 0;
    }
}
