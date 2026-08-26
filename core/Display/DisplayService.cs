// GPD Forge - display controls (brightness) via WMI. GPL-3.0-or-later.
// No kernel driver. Works on the internal panel; may be limited from a session-0 service.
using System.Management;
using Microsoft.Extensions.Logging;

namespace GpdForge.Display;

public sealed class DisplayService(ILogger<DisplayService>? logger = null)
{
    public int? GetBrightness()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var mo in s.Get())
            {
                using (mo) return Convert.ToInt32(mo["CurrentBrightness"]);
            }
        }
        catch (Exception ex) { logger?.LogDebug(ex, "brightness read unavailable"); }
        return null;
    }

    public bool SetBrightness(int percent)
    {
        int level = Math.Max(0, Math.Min(100, percent));
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject mo in s.Get().Cast<ManagementObject>())
            {
                using (mo)
                {
                    mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)level });
                    return true;
                }
            }
        }
        catch (Exception ex) { logger?.LogDebug(ex, "brightness set unavailable"); }
        return false;
    }
}
