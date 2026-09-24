// GPD Forge — EC RAM access is atomic across callers. GPL-3.0-or-later.
//
// Selecting an EC address takes five port writes. The fan controller and the RPM reader hold
// separate EcRam instances over the same Super I/O ports, and telemetry reads arrive from HTTP
// threads as well as the worker; interleaved sequences would address the wrong cell. These tests
// run both against one shared port that fails loudly if a sequence is interrupted.
using GpdForge.Fan;
using Xunit;

namespace GpdForge.Core.Tests;

public class EcRamConcurrencyTests
{
    /// <summary>A port that knows the address sequence and records any interleaving.</summary>
    private sealed class SequenceCheckingPort : IEcPort
    {
        private int _owner = -1;
        private int _step;
        public int Interleavings;

        public void SelectSlot(int slot) { }

        public void Outb(byte register, byte value)
        {
            int me = Environment.CurrentManagedThreadId;
            if (register == 0x2E && value == 0x11)
            {
                if (_step != 0 && _owner != me) Interleavings++;
                _owner = me; _step = 1;
            }
            else if (_owner != me) Interleavings++;
            else _step++;
            Thread.SpinWait(50);    // widen the window a race needs
            if (register == 0x2F && _step >= 6) _step = 0;   // data write ends a write sequence
        }

        public byte Inb(byte register) { End(); return 0x42; }
        public ushort Inw(byte register) { End(); return 0x1234; }

        private void End()
        {
            if (_owner != Environment.CurrentManagedThreadId) Interleavings++;
            _step = 0;
        }

        public void Dispose() { }
    }

    [Fact]
    public void Concurrent_reads_and_writes_from_separate_instances_never_interleave()
    {
        var port = new SequenceCheckingPort();
        var fan = new EcRam(port);
        var rpm = new EcRam(port);

        var writer = Task.Run(() => { for (int i = 0; i < 400; i++) fan.WriteByte(0x1809, (byte)i); });
        var reader = Task.Run(() => { for (int i = 0; i < 400; i++) rpm.ReadWord(0x0218); });
        var reader2 = Task.Run(() => { for (int i = 0; i < 400; i++) rpm.ReadByte(0x0275); });
        Task.WaitAll(writer, reader, reader2);

        Assert.Equal(0, port.Interleavings);
    }
}
