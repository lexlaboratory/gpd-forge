// GPD Forge - EC fan read (read-only) tests. GPL-3.0-or-later.
using GpdForge.Fan;
using Xunit;

namespace GpdForge.Core.Tests;

public class GpdFanTests
{
    private sealed class FakeEcPort : IEcPort
    {
        public readonly List<(byte reg, byte val)> Outbs = [];
        public int SelectedSlot = -1;
        public ushort NextWord;
        public byte NextByte;
        public void SelectSlot(int slot) => SelectedSlot = slot;
        public void Outb(byte register, byte value) => Outbs.Add((register, value));
        public byte Inb(byte register) => NextByte;
        public ushort Inw(byte register) => NextWord;
        public void Dispose() { }
    }

    [Theory]
    [InlineData("GPD", "G1618-04", "Ver. 1.0", GpdBoard.WinMax2)]
    [InlineData("GPD", "G1618-04", "Ver.1.0", GpdBoard.WinMax2)]
    [InlineData("GPD", "G1618-04", "Default string", GpdBoard.Win4_6800U)]
    [InlineData("GPD", "G1617-01", "whatever", GpdBoard.WinMini)]
    public void MatchBoard_maps_known_devices(string vendor, string product, string version, GpdBoard expected)
    {
        var dev = GpdDeviceDb.MatchBoard(vendor, product, version);
        Assert.NotNull(dev);
        Assert.Equal(expected, dev!.Board);
    }

    [Fact]
    public void MatchBoard_returns_null_for_unknown()
    {
        Assert.Null(GpdDeviceDb.MatchBoard("Acme", "X1", "1.0"));
    }

    [Fact]
    public void ReadWord_issues_the_indexed_address_sequence()
    {
        var port = new FakeEcPort { NextWord = 3210 };
        var ec = new EcRam(port);

        var value = ec.ReadWord(0x0218);

        Assert.Equal(3210, value);
        Assert.Equal(new (byte, byte)[]
        {
            (0x2E, 0x11), (0x2F, 0x02),   // high byte of 0x0218
            (0x2E, 0x10), (0x2F, 0x18),   // low byte
            (0x2E, 0x12),                 // point at data register
        }, port.Outbs);
    }

    [Fact]
    public void ProbeRpm_reads_the_rpm_register_read_only_for_win4_hx370()
    {
        var port = new FakeEcPort { NextWord = 3600 };
        var result = GpdFanReader.ProbeRpm("GPD", "G1618-04", "Ver. 1.0", () => port);

        Assert.Null(result.Error);
        Assert.Equal(GpdBoard.WinMax2, result.Device!.Board);
        Assert.Equal(1, port.SelectedSlot);              // WinMax2 slot
        Assert.Equal(3600, result.RpmPure);              // read the RPM word
        // addressed the RpmRead register 0x0218 (high 0x02, low 0x18); only address bytes hit 0x2F, never a data write.
        Assert.Contains(((byte)0x2F, (byte)0x18), port.Outbs);
        Assert.All(port.Outbs.Where(o => o.reg == 0x2F), o => Assert.True(o.val is 0x02 or 0x18));
    }

    [Fact]
    public void ProbeRpm_reports_no_match_for_unknown_board()
    {
        var result = GpdFanReader.ProbeRpm("Acme", "X1", "1.0", () => new FakeEcPort());
        Assert.Null(result.Device);
        Assert.NotNull(result.Error);
    }
}
