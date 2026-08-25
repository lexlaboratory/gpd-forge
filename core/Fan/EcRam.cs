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
public sealed class EcRam(IEcPort port)
{
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
    public ushort ReadWord(ushort ecAddress) { Address(ecAddress); return port.Inw(0x2F); }

    public byte ReadByte(ushort ecAddress) { Address(ecAddress); return port.Inb(0x2F); }

    /// <summary>WRITE: only used by init/enable paths — NOT part of the read-only probe.</summary>
    public void WriteByte(ushort ecAddress, byte value) { Address(ecAddress); port.Outb(0x2F, value); }
}
