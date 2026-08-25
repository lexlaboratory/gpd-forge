// SPDX-License-Identifier: GPL-3.0-or-later
// Derived from FanControl.GPDPlugin/EcRam.cs (GPL-2.0+, (c) 2025 Chenx Dust).
// GPD Forge adaptation (c) 2026 lexlaboratory.
//
// Real IEcPort backed by the PawnIO LpcIO module that ships INSIDE LibreHardwareMonitorLib
// (no separate install). Reflection is used because PawnIo.LoadModuleFromResource is internal.
// Defensive: any missing type/method/resource throws a descriptive error the probe surfaces.
// Requires elevation (PawnIO driver). READ path only issues address+read; NO control writes.
using System.Reflection;
using LibreHardwareMonitor.Hardware;

namespace GpdForge.Fan;

public sealed class PawnIoEcPort : IEcPort
{
    private const string ModuleResource = "LibreHardwareMonitor.Resources.PawnIO.LpcIO.bin";

    private readonly object _pawn;
    private readonly MethodInfo _execute;
    private readonly MethodInfo? _close;

    public PawnIoEcPort()
    {
        var lhm = typeof(Computer).Assembly;
        var pawnType = lhm.GetType("LibreHardwareMonitor.PawnIo.PawnIo")
            ?? throw new InvalidOperationException("Type LibreHardwareMonitor.PawnIo.PawnIo not found in this LibreHardwareMonitorLib version.");
        var load = pawnType.GetMethod("LoadModuleFromResource", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("PawnIo.LoadModuleFromResource(Assembly, string) not found.");
        _pawn = load.Invoke(null, [pawnType.Assembly, ModuleResource])
            ?? throw new InvalidOperationException($"Failed to load PawnIO module resource '{ModuleResource}'.");
        _execute = pawnType.GetMethod("Execute")
            ?? throw new InvalidOperationException("PawnIo.Execute not found.");
        _close = pawnType.GetMethod("Close");
    }

    private long[] Exec(string name, long[] input, int outCount)
    {
        var elemType = _execute.GetParameters()[1].ParameterType.GetElementType()!;
        var arr = Array.CreateInstance(elemType, input.Length);
        for (int i = 0; i < input.Length; i++) arr.SetValue(Convert.ChangeType(input[i], elemType), i);

        var raw = _execute.Invoke(_pawn, [name, arr, outCount]);
        if (raw is not Array outArr) return [];
        var res = new long[outArr.Length];
        for (int i = 0; i < outArr.Length; i++) res[i] = Convert.ToInt64(outArr.GetValue(i));
        return res;
    }

    public void SelectSlot(int slot) => Exec("ioctl_select_slot", [slot], 0);
    public void Outb(byte register, byte value) => Exec("ioctl_superio_outb", [register, value], 0);
    public byte Inb(byte register) => (byte)Exec("ioctl_superio_inb", [register], 1)[0];
    public ushort Inw(byte register) => (ushort)Exec("ioctl_superio_inw", [register], 1)[0];

    public void Dispose() { try { _close?.Invoke(_pawn, null); } catch { /* ignore */ } }
}
