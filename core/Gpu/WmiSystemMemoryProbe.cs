// GPD Forge — the second opinion the ADLX vtable canary is checked against. GPL-3.0-or-later.
//
// The point of this class is that it reaches the same fact — how much RAM this machine has — by a
// completely different route than ADLX does. That independence is what makes it evidence: if a value
// read through a hand-written vtable offset agrees with a value read over WMI, the offset is right.
// Reading it from ADLX twice would prove nothing.
//
// Returns 0 rather than a guess when it cannot tell, and the caller treats 0 as "no comparison
// available" instead of "0 MB of RAM".
using System.Management;
using Microsoft.Extensions.Logging;

namespace GpdForge.Gpu;

public sealed class WmiSystemMemoryProbe(ILogger<WmiSystemMemoryProbe>? logger = null) : ISystemMemoryProbe
{
    public uint TotalRamMb()
    {
        try
        {
            // TotalPhysicalMemory is what Windows reports as installed, which is the same quantity
            // ADLX's TotalSystemRAM describes. They round differently and firmware reserves a slice,
            // hence the tolerant comparison at the other end rather than an equality check.
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (var o in searcher.Get())
            {
                var raw = o["TotalPhysicalMemory"];
                if (raw is null) continue;
                var bytes = Convert.ToUInt64(raw);
                if (bytes == 0) continue;
                return (uint)(bytes / 1024 / 1024);
            }
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or InvalidCastException)
        {
            logger?.LogDebug(e, "Could not read total physical memory over WMI.");
        }
        return 0;
    }
}
