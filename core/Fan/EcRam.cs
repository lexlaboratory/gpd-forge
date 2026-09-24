// SPDX-License-Identifier: GPL-3.0-or-later
// Derived from FanControl.GPDPlugin/EcRam.cs (GPL-2.0+, (c) 2025 Chenx Dust).
// GPD Forge adaptation (c) 2026 lexlaboratory.
//
// EC RAM access through the Super I/O index/data ports (0x2E/0x2F). The IEcPort primitives
// are executed by a driver (PawnIO's LpcIO module in production); abstracted here so the
// addressing sequence is unit-testable without hardware.

namespace GpdForge.Fan;

/// <summary>Super I/O port primitives (index 0x2E / data 0x2F), backed by a driver.</summary>
public interface IEcPort : IDisposable
{
    void SelectSlot(int slot);
    void Outb(byte register, byte value);
    byte Inb(byte register);
    ushort Inw(byte register);
}

/// <summary>Reads/writes 16-bit EC RAM addresses via the indexed Super I/O sequence.</summary>
/// <remarks>
/// Every access is atomic across the whole machine, not just this instance. Addressing a cell is
/// five port writes, and more than one caller drives the same ports: the fan controller and the RPM
/// reader each hold their own EcRam, telemetry reads come from HTTP threads as well as the worker,
/// and LibreHardwareMonitor probes the Super I/O chip itself. An interleaved sequence addresses the
/// wrong cell — for a write, a stray byte in EC RAM. So each access takes an in-process lock AND the
/// machine-wide ISA-bus mutex that LibreHardwareMonitor, HWiNFO and similar tools already honour.
/// </remarks>
public sealed class EcRam(IEcPort port)
{
    // The name every Super I/O tool on Windows agrees on; see LibreHardwareMonitor's Mutexes.cs.
    private const string IsaBusMutexName = @"Global\Access_ISABUS.HTP.Method";
    private static readonly TimeSpan IsaBusTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Lock InProcess = new();
    private static readonly Mutex? IsaBus = TryOpenIsaBusMutex();

    public void SelectSlot(int slot) => port.SelectSlot(slot);

    // Select the 16-bit EC-RAM address, then read/write the data register (0x2F).
    private void Address(ushort ecAddress)
    {
        port.Outb(0x2E, 0x11);
        port.Outb(0x2F, (byte)((ecAddress >> 8) & 0xFF));
        port.Outb(0x2E, 0x10);
        port.Outb(0x2F, (byte)(ecAddress & 0xFF));
        port.Outb(0x2E, 0x12);
    }

    /// <summary>PURE READ: addresses the register and reads a word. No control-register writes.</summary>
    public ushort ReadWord(ushort ecAddress) => Exclusive(() => { Address(ecAddress); return port.Inw(0x2F); });

    public byte ReadByte(ushort ecAddress) => Exclusive(() => { Address(ecAddress); return port.Inb(0x2F); });

    /// <summary>WRITE: only used by init/enable paths — NOT part of the read-only probe.</summary>
    public void WriteByte(ushort ecAddress, byte value) =>
        Exclusive(() => { Address(ecAddress); port.Outb(0x2F, value); return 0; });

    private static T Exclusive<T>(Func<T> access)
    {
        lock (InProcess)
        {
            bool held = false;
            try
            {
                if (IsaBus is not null)
                {
                    try { held = IsaBus.WaitOne(IsaBusTimeout); }
                    catch (AbandonedMutexException) { held = true; }   // previous owner died; the bus is ours
                    if (!held)
                        throw new TimeoutException("Another tool held the ISA bus for over 250 ms; EC access skipped.");
                }
                return access();
            }
            finally
            {
                if (held) IsaBus!.ReleaseMutex();
            }
        }
    }

    private static Mutex? TryOpenIsaBusMutex()
    {
        try { return new Mutex(false, IsaBusMutexName); }
        catch (Exception) { return null; }   // no rights to the Global namespace: in-process lock only
    }
}
