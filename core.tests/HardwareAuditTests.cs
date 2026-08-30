// GPD Forge — the hardware audit log. GPL-3.0-or-later.
using GpdForge.Broker;
using GpdForge.Fan;
using Xunit;

namespace GpdForge.Core.Tests;

public class HardwareAuditLogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Writes_come_back_newest_first()
    {
        // The question is nearly always "what happened last", so oldest-first would make the caller
        // page to the end every time.
        var log = new HardwareAuditLog();
        log.Record("tdp", "Apply", "first", true, T0);
        log.Record("fan", "SetAuto", "second", null, T0.AddSeconds(1));

        Assert.Equal("second", log.Recent()[0].Detail);
    }

    [Fact]
    public void Unconfirmed_and_failed_are_counted_separately()
    {
        // "We could not confirm 12 writes" and "12 writes were rejected" call for different
        // reactions, so collapsing them would make the tally useless for deciding anything.
        var log = new HardwareAuditLog();
        log.Record("fan", "SetManualDuty", "d=200", true, T0);
        log.Record("fan", "SetManualDuty", "d=200", false, T0);
        log.Record("fan", "SetAuto", "auto", null, T0);

        var (total, failed, unconfirmed) = log.Tally();
        Assert.Equal(3, total);
        Assert.Equal(1, failed);
        Assert.Equal(1, unconfirmed);
    }

    [Fact]
    public void The_log_is_bounded_so_it_cannot_fill_a_handhelds_disk()
    {
        var log = new HardwareAuditLog();
        for (var i = 0; i < HardwareAuditLog.Capacity + 50; i++)
            log.Record("tdp", "Apply", $"w{i}", true, T0);

        Assert.Equal(HardwareAuditLog.Capacity, log.Tally().Total);
        // And the newest survived the trim: dropping the write you are recording would be the one
        // unacceptable way to stay under the cap.
        Assert.Equal($"w{HardwareAuditLog.Capacity + 49}", log.Recent(1)[0].Detail);
    }
}

public class AuditingFanControllerTests
{
    private sealed class FakeFan : IGpdFanController
    {
        public bool NextWriteSucceeds = true;
        public int AutoCalls;
        public bool Available => true;
        public bool IsManual => false;
        public bool SetManualDuty(int duty) => NextWriteSucceeds;
        public void SetAuto() => AutoCalls++;
        public int? ReadDuty() => 128;
        public void Dispose() { }
    }

    [Fact]
    public void A_verified_duty_write_is_recorded_as_verified()
    {
        var log = new HardwareAuditLog();
        var fan = new AuditingGpdFanController(new FakeFan(), log);

        Assert.True(fan.SetManualDuty(200));
        Assert.True(log.Recent()[0].Verified);
    }

    [Fact]
    public void A_refused_write_is_recorded_as_failed_rather_than_dropped()
    {
        // A refused write is the single most interesting entry this log can hold; omitting it would
        // leave a record that only ever shows success.
        var log = new HardwareAuditLog();
        var fan = new AuditingGpdFanController(new FakeFan { NextWriteSucceeds = false }, log);

        Assert.False(fan.SetManualDuty(200));
        Assert.False(log.Recent()[0].Verified);
    }

    [Fact]
    public void SetAuto_is_recorded_as_unconfirmed_because_it_cannot_report_failure()
    {
        // It returns void. Recording it as verified would be inventing a confirmation nobody gave us.
        var log = new HardwareAuditLog();
        var fan = new AuditingGpdFanController(new FakeFan(), log);

        fan.SetAuto();
        Assert.Null(log.Recent()[0].Verified);
    }

    [Fact]
    public void Reads_are_not_recorded_because_they_are_not_writes()
    {
        var log = new HardwareAuditLog();
        var fan = new AuditingGpdFanController(new FakeFan(), log);

        _ = fan.ReadDuty();
        Assert.Equal(0, log.Tally().Total);
    }

    [Fact]
    public void The_decorator_passes_the_inner_result_through_unchanged()
    {
        // A decorator that alters behaviour to keep its log tidy has become a liability. Same value
        // out, and the inner call actually happened.
        var inner = new FakeFan();
        var fan = new AuditingGpdFanController(inner, new HardwareAuditLog());

        fan.SetAuto();
        Assert.Equal(1, inner.AutoCalls);
        Assert.True(fan.Available);
        Assert.Equal(128, fan.ReadDuty());
    }
}
