// GPD Forge — where to look for the controller's config interface. GPL-3.0-or-later.
//
// The pad presents as seven PnP nodes (composite parent plus MI_00/01/02, each also a HID node), and
// only one of them carries the config blob. Rather than guess which, the probe tries them in turn and
// reports which one answered — "which interface" is the first question when a read comes back empty.
//
// Paths are built from the device interface path convention rather than from a name: on this machine
// the controller is called "Dispositivo definido por el proveedor compatible con HID", and anything
// matching English text would find nothing. IDs do not localise.
using System.Management;

namespace GpdForge.Hid;

public static class HidConfigPaths
{
    /// <summary>The HID class device interface GUID. Constant, and not localised.</summary>
    private const string HidInterfaceGuid = "{4d1e55b2-f16f-11cf-88cb-001111000030}";

    /// <summary>
    /// Candidate HID interface paths for the GPD pad, most likely first. Returns an empty sequence
    /// rather than throwing when WMI is unavailable — a probe that cannot enumerate should say so,
    /// not crash.
    /// </summary>
    public static IEnumerable<string> Candidates()
    {
        var found = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'HID%VID_2F24&PID_0135%'");

            foreach (var o in searcher.Get())
            {
                if (o["DeviceID"] is not string id) continue;
                found.Add(ToInterfacePath(id));
            }
        }
        catch (ManagementException) { return []; }

        // MI_02 first: on GPD pads the vendor-defined interface is the one that carries the config,
        // and trying it first keeps the common case to a single open. This is an ordering hint only —
        // every candidate is still tried.
        return found
            .OrderByDescending(p => p.Contains("mi_02", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// Turns a PnP instance id into the device interface path CreateFile expects. Windows' convention
    /// is the instance id with backslashes replaced by '#', prefixed with the device namespace and
    /// suffixed with the interface GUID. Public so it can be unit-tested without any hardware.
    /// </summary>
    public static string ToInterfacePath(string instanceId)
    {
        var escaped = instanceId.Replace('\\', '#');
        return @"\\?\" + escaped + "#" + HidInterfaceGuid;
    }
}
