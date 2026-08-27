// GPD Forge — gated fan PWM-duty controller tests (write sequence + read-back + safety). GPL-3.0-or-later.
using GpdForge.Fan;
using Xunit;

namespace GpdForge.Core.Tests;

public class GpdFanControllerTests
{
    /// <summary>
    /// Models real EC RAM addressing at the IEcPort level (not just recording raw Outb calls): tracks
    /// the two address bytes the 0x2E/0x2F index protocol selects, then reads/writes a backing cell
    /// dictionary keyed by that 16-bit address — so a WriteByte(addr, v) followed by a ReadByte(addr)
    /// really does read back what was written, exactly like real EC RAM. <see cref="ForceReadBack"/>
    /// overrides that for the one test that needs to simulate a verification failure.
    /// </summary>
    private sealed class FakeEcPort : IEcPort
    {
        public readonly List<(byte reg, byte val)> Outbs = [];
        public int SelectedSlot = -1;
        public readonly Dictionary<int, byte> Cells = new();
        public byte? ForceReadBack;

        private int _hi, _lo;
        private int _lastIndexCmd = -1;
        private int Addr => (_hi << 8) | _lo;

        public void SelectSlot(int slot) => SelectedSlot = slot;

        public void Outb(byte register, byte value)
        {
            Outbs.Add((register, value));
            if (register == 0x2E) { _lastIndexCmd = value; return; }
            if (register != 0x2F) return;

            switch (_lastIndexCmd)
            {
                case 0x11: _hi = value; break;               // high address byte
                case 0x10: _lo = value; break;                // low address byte
                case 0x12: Cells[Addr] = value; break;         // data write at the addressed cell
            }
        }

        public byte Inb(byte register)
        {
            if (register == 0x2F && _lastIndexCmd == 0x12)
                return ForceReadBack ?? Cells.GetValueOrDefault(Addr);
            return 0;
        }

        public ushort Inw(byte register) => 0; // unused by GpdFanController
        public void Dispose() { }
    }

    // wm2 board from the task spec: manual_control_enable=0x0275, pwm_write=0x1809, pwm_max=184, slot 1.
    private static readonly GpdFanDevice Device = GpdDeviceDb.WinMax2;

    private static (GpdFanController ctl, FakeEcPort port) Make()
    {
        var port = new FakeEcPort();
        var ctl = new GpdFanController(Device, () => port);
        return (ctl, port);
    }

    [Fact]
    public void SetManualDuty_from_auto_lands_max_flips_enable_then_writes_the_real_target()
    {
        var (ctl, port) = Make();

        bool ok = ctl.SetManualDuty(128);

        Assert.True(ok);
        Assert.True(ctl.IsManual);
        Assert.Equal(Device.Slot, port.SelectedSlot);

        int maxCast = FanMath.CastPwm(255, Device.PwmMax);
        int targetCast = FanMath.CastPwm(128, Device.PwmMax);

        // Extract the sequence of DATA writes (0x2F while pointed at the data register, i.e. right
        // after an 0x2E,0x12) in order, by replaying the same index protocol the fake models.
        var dataWrites = ExtractDataWrites(port.Outbs);

        // The enable flip MUST land between the max write and the target write, not after both: while
        // manual_control_enable is still 0 the firmware's own auto loop owns pwm_write, so a target
        // write ordered before the enable flip would just silently clobber the max write before manual
        // mode ever samples it — defeating the "never engage manual at a low/zero speed" safety step.
        Assert.Equal(3, dataWrites.Count);
        Assert.Equal((Device.PwmWrite, (byte)maxCast), dataWrites[0]);        // 1. duty at MAX first
        Assert.Equal((Device.ManualControlEnable, (byte)1), dataWrites[1]);   // 2. THEN engage manual
        Assert.Equal((Device.PwmWrite, (byte)targetCast), dataWrites[2]);     // 3. now safely ramp to the real target

        // Final read-back is of pwm_write (register address, not the raw 0x2F wire register).
        Assert.Equal((byte)targetCast, port.Cells[Device.PwmWrite]);
    }

    [Fact]
    public void SetManualDuty_when_already_manual_does_not_repeat_the_max_first_step()
    {
        var (ctl, port) = Make();
        ctl.SetManualDuty(100);   // first call: engages manual, writes max-first
        port.Outbs.Clear();

        ctl.SetManualDuty(150);   // second call: already manual

        var dataWrites = ExtractDataWrites(port.Outbs);
        int targetCast = FanMath.CastPwm(150, Device.PwmMax);

        Assert.Equal(2, dataWrites.Count);   // target duty, then re-assert manual_control_enable=1
        Assert.Equal((Device.PwmWrite, (byte)targetCast), dataWrites[0]);
        Assert.Equal((Device.ManualControlEnable, (byte)1), dataWrites[1]);
    }

    [Fact]
    public void SetManualDuty_verifies_the_read_back_and_returns_true_on_match()
    {
        var (ctl, _) = Make();
        Assert.True(ctl.SetManualDuty(200));
    }

    [Fact]
    public void SetManualDuty_returns_false_when_the_read_back_does_not_match()
    {
        var (ctl, port) = Make();
        port.ForceReadBack = 0;   // simulate the EC not actually holding the written duty

        bool ok = ctl.SetManualDuty(200);

        Assert.False(ok);
        // Still marks manual (we DID issue the write+enable) — the failure is reported, not hidden,
        // but we don't pretend the write never happened.
        Assert.True(ctl.IsManual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(39)]
    public void SetManualDuty_enforces_the_safety_floor_never_commanding_a_near_stopped_fan(int lowRequest)
    {
        var (ctl, port) = Make();
        ctl.SetManualDuty(lowRequest);

        int floorCast = FanMath.CastPwm(GpdFanController.MinManualDuty, Device.PwmMax);
        Assert.Equal((byte)floorCast, port.Cells[Device.PwmWrite]);
    }

    [Fact]
    public void SetManualDuty_clamps_requests_above_255()
    {
        var (ctl, port) = Make();
        ctl.SetManualDuty(9999);

        int maxCast = FanMath.CastPwm(255, Device.PwmMax);
        Assert.Equal((byte)maxCast, port.Cells[Device.PwmWrite]);
    }

    [Fact]
    public void SetAuto_writes_zero_to_manual_control_enable_and_clears_IsManual()
    {
        var (ctl, port) = Make();
        ctl.SetManualDuty(128);
        Assert.True(ctl.IsManual);

        ctl.SetAuto();

        Assert.False(ctl.IsManual);
        Assert.Equal((byte)0, port.Cells[Device.ManualControlEnable]);
    }

    [Fact]
    public void ReadDuty_uncasts_the_pwm_write_cell()
    {
        var (ctl, port) = Make();
        ctl.SetManualDuty(64);

        int? read = ctl.ReadDuty();

        Assert.NotNull(read);
        Assert.InRange(read!.Value, 63, 65); // uncast round-trip, within ±1
    }

    [Fact]
    public void ReadDuty_returns_null_when_the_ec_port_is_unavailable()
    {
        var ctl = new GpdFanController(Device, () => throw new InvalidOperationException("driver missing"));
        Assert.Null(ctl.ReadDuty());
        Assert.False(ctl.Available);
    }

    [Fact]
    public void Constructor_never_throws_when_the_port_factory_fails()
    {
        var ex = Record.Exception(() => new GpdFanController(Device, () => throw new InvalidOperationException("no PawnIO driver")));
        Assert.Null(ex);
    }

    [Fact]
    public void SetManualDuty_returns_false_when_the_ec_port_is_unavailable()
    {
        var ctl = new GpdFanController(Device, () => throw new InvalidOperationException("driver missing"));
        Assert.False(ctl.SetManualDuty(128));
        Assert.False(ctl.IsManual);
    }

    [Fact]
    public void Dispose_restores_automatic_even_if_SetAuto_was_never_called()
    {
        var (ctl, port) = Make();
        ctl.SetManualDuty(128);
        Assert.True(ctl.IsManual);

        ctl.Dispose();

        Assert.Equal((byte)0, port.Cells[Device.ManualControlEnable]);
        Assert.False(ctl.IsManual);
    }

    [Fact]
    public void Dispose_is_idempotent_and_never_throws()
    {
        var (ctl, _) = Make();
        ctl.SetManualDuty(128);

        var ex = Record.Exception(() => { ctl.Dispose(); ctl.Dispose(); });

        Assert.Null(ex);
    }

    [Fact]
    public void NoOpGpdFanController_never_reports_success_and_never_throws()
    {
        var noop = new NoOpGpdFanController();
        Assert.False(noop.Available);
        Assert.False(noop.SetManualDuty(128));
        Assert.False(noop.IsManual);
        Assert.Null(noop.ReadDuty());
        var ex = Record.Exception(() => { noop.SetAuto(); noop.Dispose(); });
        Assert.Null(ex);
    }

    /// <summary>Replays the same 0x2E/0x2F index protocol the fake models to pull out just the
    /// (address, value) DATA writes, in order — i.e. what EcRam.WriteByte actually wrote, discarding
    /// the index/addressing chatter around each one.</summary>
    private static List<(ushort addr, byte val)> ExtractDataWrites(List<(byte reg, byte val)> outbs)
    {
        var result = new List<(ushort, byte)>();
        int hi = 0, lo = 0, lastCmd = -1;
        foreach (var (reg, val) in outbs)
        {
            if (reg == 0x2E) { lastCmd = val; continue; }
            if (reg != 0x2F) continue;
            switch (lastCmd)
            {
                case 0x11: hi = val; break;
                case 0x10: lo = val; break;
                case 0x12: result.Add(((ushort)((hi << 8) | lo), val)); break;
            }
        }
        return result;
    }
}
