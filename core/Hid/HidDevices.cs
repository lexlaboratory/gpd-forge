// GPD Forge - locating the controller's device nodes. GPL-3.0-or-later.
//
// Everything here keys on IDs and numeric codes, never on device names or status text. On the
// reference Win 4 (Spanish Windows) the same controller is called "Dispositivo definido por el
// proveedor compatible con HID"; a parser that matched English names would find nothing and report
// a missing controller. `PNPDeviceID` and `ConfigManagerErrorCode` do not localise.
using System.Management;

namespace GpdForge.Hid;

/// <summary>
/// One PnP node belonging to the controller. Confirmed on device 2026-08-29: the GPD pad presents as
/// <b>seven</b> nodes, not one — a USB composite parent plus three interfaces (MI_00/01/02), each of
/// which appears again as its own HID node.
/// </summary>
public sealed record HidDeviceNode(string InstanceId, int ConfigManagerErrorCode)
{
    /// <summary>0 means Windows considers the node to be working. Anything else is a fault code.</summary>
    public bool Healthy => ConfigManagerErrorCode == 0;

    /// <summary>
    /// The composite parent — the node with no interface (<c>&amp;MI_</c>) segment. Restarting it
    /// re-enumerates every child, which is one action instead of seven. Structural, so it holds
    /// whatever the device is called in whatever language.
    /// </summary>
    public bool IsCompositeParent => !InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase);
}

public interface IHidDeviceEnumerator
{
    /// <summary>Every PnP node whose instance ID contains <paramref name="idFragment"/>.</summary>
    IReadOnlyList<HidDeviceNode> Find(string idFragment);
}

public sealed class WmiHidDeviceEnumerator : IHidDeviceEnumerator
{
    public IReadOnlyList<HidDeviceNode> Find(string idFragment)
    {
        var nodes = new List<HidDeviceNode>();
        try
        {
            // LIKE against the ID rather than the localised Name. The escaped underscore matters:
            // in WQL '_' is a single-character wildcard, so an unescaped VID_2F24 would also match
            // VIDX2F24 and friends.
            var pattern = idFragment.Replace("_", "[_]");
            using var searcher = new ManagementObjectSearcher(
                $"SELECT PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPDeviceID LIKE '%{pattern}%'");

            foreach (var o in searcher.Get().Cast<ManagementBaseObject>())
            {
                using (o)
                {
                    if (o["PNPDeviceID"] is not string id || id.Length == 0) continue;
                    var code = o["ConfigManagerErrorCode"] is null ? 0 : Convert.ToInt32(o["ConfigManagerErrorCode"]);
                    nodes.Add(new HidDeviceNode(id, code));
                }
            }
        }
        catch (ManagementException)
        {
            // WMI unavailable or the class is missing: report nothing found rather than throwing into
            // a resume restore. The caller distinguishes "no nodes" from "all healthy".
        }
        return nodes;
    }
}
