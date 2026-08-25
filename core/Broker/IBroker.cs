// GPD Forge — kernel access broker abstraction.
// Copyright (C) 2026 lexlaboratory. GPL-3.0-or-later. See ../../LICENSE.

namespace GpdForge.Broker;

/// <summary>
/// The ONLY component allowed to touch MSRs / EC / I/O ports, via PawnIO (never WinRing0),
/// with a strict per-model whitelist and an audit log of every write. Fail closed.
/// </summary>
public interface IBroker
{
    bool IsAvailable { get; }
    uint ReadEc(ushort offset);
    void WriteEc(ushort offset, uint value);
    ulong ReadMsr(uint index);
}

/// <summary>Phase-0 no-op broker (no hardware access). Replaced by the PawnIO broker in Phase 1.</summary>
public sealed class NullBroker : IBroker
{
    public bool IsAvailable => false;
    public uint ReadEc(ushort offset) => 0;
    public void WriteEc(ushort offset, uint value) { /* no-op until PawnIO broker lands */ }
    public ulong ReadMsr(uint index) => 0;
}
