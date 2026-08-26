// SPDX-License-Identifier: GPL-3.0-or-later
// GPD Forge (c) 2026 lexlaboratory. Board matching data derived from FanControl.GPDPlugin (GPL-2.0+).
//
// READ-ONLY fan probe: detect the board, then read the RPM register once. It performs ONLY the
// indexed address+read sequence (no init/enable control writes), so it cannot change fan state or
// fight another controller. If RPM reads 0 it likely needs an enable/init write — which this does
// NOT do; that stays gated.
using System.Management;

namespace GpdForge.Fan;

public sealed record EcProbeResult(
    string Vendor, string Product, string BoardVersion,
    GpdFanDevice? Device, int? RpmPure, string? Error);

public static class GpdFanReader
{
    public static (string vendor, string product, string version) DetectBoard()
    {
        string vendor = "", product = "", version = "";
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Vendor, Name FROM Win32_ComputerSystemProduct");
            foreach (var mo in s.Get()) { using (mo) { vendor = $"{mo["Vendor"]}"; product = $"{mo["Name"]}"; } break; }
        }
        catch { /* leave blank */ }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Version FROM Win32_BaseBoard");
            foreach (var mo in s.Get()) { using (mo) { version = $"{mo["Version"]}"; } break; }
        }
        catch { /* leave blank */ }
        return (vendor.Trim(), product.Trim(), version.Trim());
    }

    /// <summary>Detect the board via WMI, then read RPM read-only.</summary>
    public static EcProbeResult ProbeRpm(Func<IEcPort>? portFactory = null)
    {
        var (vendor, product, version) = DetectBoard();
        return ProbeRpm(vendor, product, version, portFactory);
    }

    /// <summary>Testable core: given identity, match + pure-read RPM through the port.</summary>
    public static EcProbeResult ProbeRpm(string vendor, string product, string version, Func<IEcPort>? portFactory = null)
    {
        var dev = GpdDeviceDb.MatchBoard(vendor, product, version);
        if (dev is null) return new EcProbeResult(vendor, product, version, null, null, "no matching board in DeviceDb");
        try
        {
            using var port = (portFactory ?? (() => new PawnIoEcPort()))();
            var ec = new EcRam(port);
            ec.SelectSlot(dev.Slot);
            int rpm = ec.ReadWord(dev.RpmRead);   // PURE read
            return new EcProbeResult(vendor, product, version, dev, rpm, null);
        }
        catch (Exception ex)
        {
            // Reflection into LHM's PawnIo wraps the real failure in a TargetInvocationException;
            // unwrap to the root so the probe reports the actual cause (e.g. driver not installed).
            var root = ex;
            while (root.InnerException is not null) root = root.InnerException;
            return new EcProbeResult(vendor, product, version, dev, null, $"{root.GetType().Name}: {root.Message}");
        }
    }
}
