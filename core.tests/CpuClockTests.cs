// GPD Forge — CPU clock source selection. GPL-3.0-or-later.
//
// cpuClockMhz came from Win32_Processor.CurrentClockSpeed until 2026-09-24, and on this HX 370 that
// is the fixed 2000 MHz base clock: a number that never moved under any load, reported every second
// as if it were a measurement. These pin the replacement — LHM's effective core clock — and the rule
// that a fallback which never changes is reported as null rather than as a reading.
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class CpuClockTests
{
    private static ClockSensorReading C(string name, double? mhz) => new(name, mhz);

    [Fact]
    public void The_aggregate_effective_clock_wins_when_LHM_publishes_it()
    {
        var mhz = CpuClock.EffectiveCoreMhz([
            C("Bus Speed", 100), C("Core #1", 5100), C("Core #1 (Effective)", 900),
            C("Cores (Average)", 5000), C("Cores (Average Effective)", 1234.6),
        ]);
        Assert.Equal(1235, mhz);
    }

    [Fact]
    public void Per_core_effective_clocks_are_averaged_when_there_is_no_aggregate()
    {
        var mhz = CpuClock.EffectiveCoreMhz([
            C("Bus Speed", 100), C("Core #1", 5100), C("Core #2", 5100),
            C("Core #1 (Effective)", 1000), C("Core #2 (Effective)", 3000),
        ]);
        Assert.Equal(2000, mhz);
    }

    [Fact]
    public void Without_effective_sensors_the_average_core_clock_is_used()
    {
        Assert.Equal(4800, CpuClock.EffectiveCoreMhz([C("Cores (Average)", 4800), C("Core #1", 5100)]));
        // ...and without even that, the plain per-core clocks — never the bus.
        Assert.Equal(4000, CpuClock.EffectiveCoreMhz([C("Bus Speed", 100), C("Core #1", 3000), C("Core #2", 5000)]));
    }

    [Fact]
    public void P_and_E_core_names_count_as_cores()
    {
        Assert.Equal(3000, CpuClock.EffectiveCoreMhz([C("P-Core #1", 4000), C("E-Core #1", 2000)]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(20_000.0)]   // not a clock any x86 core has run at: a garbage register, not a reading
    public void Unusable_values_are_ignored_rather_than_averaged_in(double? bad)
    {
        Assert.Equal(3000, CpuClock.EffectiveCoreMhz([C("Core #1 (Effective)", 3000), C("Core #2 (Effective)", bad)]));
        Assert.Null(CpuClock.EffectiveCoreMhz([C("Cores (Average Effective)", bad)]));
    }

    [Fact]
    public void No_core_clock_at_all_is_null_not_the_bus_speed()
    {
        Assert.Null(CpuClock.EffectiveCoreMhz([]));
        Assert.Null(CpuClock.EffectiveCoreMhz([C("Bus Speed", 100)]));
    }

    [Fact]
    public void A_Win32_clock_that_never_moves_is_reported_as_null()
    {
        var filter = new Win32ClockFilter();
        for (int i = 0; i < 30; i++) Assert.Null(filter.Observe(2000));
    }

    [Fact]
    public void A_Win32_clock_that_does_move_is_believed_from_then_on()
    {
        var filter = new Win32ClockFilter();
        Assert.Null(filter.Observe(2000));
        Assert.Equal(2800, filter.Observe(2800));
        // Once it has proven itself live, returning to the first value is a reading, not a hint of staleness.
        Assert.Equal(2000, filter.Observe(2000));
    }

    [Fact]
    public void A_missing_Win32_reading_is_null_and_does_not_count_as_movement()
    {
        var filter = new Win32ClockFilter();
        Assert.Null(filter.Observe(null));
        Assert.Null(filter.Observe(2000));
        Assert.Null(filter.Observe(0));
        Assert.Null(filter.Observe(2000));
    }
}
