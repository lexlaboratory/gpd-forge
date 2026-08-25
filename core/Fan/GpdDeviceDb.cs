// SPDX-License-Identifier: GPL-3.0-or-later
// Derived from FanControl.GPDPlugin/DeviceDb.cs (GPL-2.0+, (c) 2025 Chenx Dust),
// itself a port of Cryolitia/gpd-fan-driver (GPL-2.0, (c) 2024 Cryolitia PukNgae).
// GPD Forge adaptation (c) 2026 lexlaboratory. See NOTICE / docs/CREDITS.md.
//
// Per-model EC register map + board matching. Data only.

namespace GpdForge.Fan;

public enum GpdBoard { WinMini, Win4_6800U, WinMax2, Duo }

public sealed record GpdFanDevice(
    string BoardName,
    GpdBoard Board,
    int Slot,
    ushort ManualControlEnable,
    ushort RpmRead,
    ushort PwmWrite,
    byte PwmMax);

public static class GpdDeviceDb
{
    public static readonly GpdFanDevice WinMini = new("GPD Win Mini / Pocket 4", GpdBoard.WinMini, 1, 0x047A, 0x0478, 0x047A, 244);
    public static readonly GpdFanDevice Duo     = new("GPD Duo", GpdBoard.Duo, 1, 0x047A, 0x0478, 0x047A, 244);
    public static readonly GpdFanDevice Win4_6800U = new("GPD Win 4 6800U", GpdBoard.Win4_6800U, 0, 0xC311, 0xC880, 0xC311, 127);
    public static readonly GpdFanDevice WinMax2 = new("GPD Win Max 2 / Win 4 (7840U+)", GpdBoard.WinMax2, 1, 0x0275, 0x0218, 0x1809, 184);

    /// <summary>Match by (vendor, product, board version). Returns null when unknown.</summary>
    public static GpdFanDevice? MatchBoard(string vendor, string product, string boardVersion)
        => (vendor, product, boardVersion) switch
        {
            ("GPD", "G1617-01", _) => WinMini,
            ("GPD", "G1617-02", _) => WinMini,
            ("GPD", "G1617-02-L", _) => WinMini,

            ("GPD", "G1618-04", "Default string") => Win4_6800U,
            ("GPD", "G1618-04", "Ver. 1.0") => WinMax2,   // 7840U and (provisionally) HX370 2025
            ("GPD", "G1618-04", "Ver.1.0") => WinMax2,

            ("GPD", "G1619-04", _) => WinMax2,
            ("GPD", "G1619-05", _) => WinMax2,

            ("GPD", "G1622-01", _) => Duo,
            ("GPD", "G1622-01-L", _) => Duo,

            ("GPD", "G1628-04", _) => WinMini,
            ("GPD", "G1628-04-L", _) => WinMini,

            _ => null,
        };
}
