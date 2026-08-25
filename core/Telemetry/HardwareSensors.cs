// GPD Forge — optional read-only hardware sensors via LibreHardwareMonitor. GPL-3.0-or-later.
//
// READ-ONLY. Fills the sensors WMI can't give (package watts, CPU/GPU temp, fan RPM). LHM loads its
// own read-only kernel driver, so this is only wired when GPDFORGE_ENABLE_HARDWARE=1 + elevation
// (see Program.cs). Everything is defensive: if a sensor/driver is unavailable, values stay 0 and
// TryRead reports false — the caller falls back to WMI. Never writes anything.
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;

namespace GpdForge.Telemetry;

public readonly record struct HwSample(double PackageW, double CpuTempC, double GpuTempC, int FanRpm);

public interface IHardwareSensors : IDisposable
{
    bool TryRead(out HwSample sample);
}

public sealed class LhmHardwareSensors(ILogger<LhmHardwareSensors>? logger = null) : IHardwareSensors
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
    };
    private bool _opened;
    private readonly Lock _gate = new();

    public bool TryRead(out HwSample sample)
    {
        sample = default;
        try
        {
            lock (_gate)
            {
                if (!_opened) { _computer.Open(); _opened = true; }

                double packageW = 0, cpuTempC = 0, gpuTempC = 0;
                int fanRpm = 0;

                foreach (var hw in _computer.Hardware)
                {
                    hw.Update();
                    foreach (var sub in hw.SubHardware) sub.Update();

                    var sensors = hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors));
                    foreach (var s in sensors)
                    {
                        if (s.Value is not float v) continue;
                        switch (s.SensorType)
                        {
                            case SensorType.Power when Named(s, "Package") && hw.HardwareType is HardwareType.Cpu:
                                packageW = v; break;
                            case SensorType.Temperature when hw.HardwareType is HardwareType.Cpu && cpuTempC == 0 && (Named(s, "Package") || Named(s, "Tctl") || Named(s, "Core (Tctl")):
                                cpuTempC = v; break;
                            case SensorType.Temperature when hw.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel && gpuTempC == 0:
                                gpuTempC = v; break;
                            case SensorType.Fan when fanRpm == 0 && v > 0:
                                fanRpm = (int)v; break;
                        }
                    }
                }

                sample = new HwSample(Math.Round(packageW, 1), Math.Round(cpuTempC, 1), Math.Round(gpuTempC, 1), fanRpm);
                return packageW > 0 || cpuTempC > 0 || fanRpm > 0;
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "LHM sensors unavailable (needs elevation/driver)");
            return false;
        }
    }

    private static bool Named(ISensor s, string needle) =>
        s.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        try { if (_opened) _computer.Close(); } catch { /* ignore */ }
    }
}
