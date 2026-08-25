// GPD Forge - HID safe-writer tests. GPL-3.0-or-later.
using GpdForge.Hid;
using Xunit;

namespace GpdForge.Core.Tests;

public class SafeConfigWriterTests
{
    private sealed class FakeHidDevice(int size = 1024, bool ignoreWrites = false) : IHidConfigDevice
    {
        public byte[] Store = new byte[size];
        public int Writes { get; private set; }
        public byte[]? LastWritten { get; private set; }

        public byte[] GetConfig() => (byte[])Store.Clone();

        public void SetConfig(byte[] blob)
        {
            Writes++;
            LastWritten = (byte[])blob.Clone();
            if (!ignoreWrites) Store = (byte[])blob.Clone();
        }
    }

    [Fact]
    public void Apply_writes_and_verifies_and_keeps_a_backup()
    {
        var device = new FakeHidDevice();
        var writer = new SafeConfigWriter(device);

        writer.Apply(blob => blob[5] = 0x42);

        Assert.Equal(0x42, device.Store[5]);      // change took
        Assert.NotNull(writer.LastBackup);
        Assert.Equal(0x00, writer.LastBackup![5]); // backup is the pre-change state
    }

    [Fact]
    public void Apply_restores_backup_and_throws_when_readback_mismatches()
    {
        var device = new FakeHidDevice(ignoreWrites: true); // writes never "stick" -> read-back mismatch
        var writer = new SafeConfigWriter(device);

        Assert.Throws<HidVerifyException>(() => writer.Apply(blob => blob[5] = 0x42));

        Assert.True(device.Writes >= 2);            // the patch write + the restore write
        Assert.Equal(0x00, device.LastWritten![5]); // last write was the (clean) backup
    }

    [Fact]
    public void Apply_rejects_unexpected_config_size()
    {
        var device = new FakeHidDevice(size: 512);
        var writer = new SafeConfigWriter(device);

        Assert.Throws<HidVerifyException>(() => writer.Apply(_ => { }));
    }
}
