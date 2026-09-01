// GPD Forge — the battery-health arithmetic, and the honesty rules inside it. GPL-3.0-or-later.
using GpdForge.Battery;
using Xunit;

namespace GpdForge.Core.Tests;

public class BatteryHealthMathTests
{
    [Fact]
    public void Health_is_full_charge_over_designed()
    {
        // The reference device's real numbers, 2026-09-01.
        Assert.Equal(91.2, BatteryHealthMath.HealthPercent(43890, 40009));
    }

    [Theory]
    [InlineData(null, 40009)]
    [InlineData(43890, null)]
    [InlineData(0, 40009)]
    [InlineData(43890, 0)]
    [InlineData(-1, 40009)]
    public void Health_is_null_rather_than_a_number_when_an_input_is_missing_or_absurd(int? designed, int? full)
    {
        // Null, never 0 and never 100. A designed capacity of zero is a failed read, and dividing by
        // it to announce "0 % health" tells someone their battery is dead when what actually
        // happened is that a WMI query came back empty.
        Assert.Null(BatteryHealthMath.HealthPercent(designed, full));
    }

    [Fact]
    public void Health_above_100_is_reported_rather_than_clamped()
    {
        // A new pack often measures slightly above its design capacity. Clamping would hide that the
        // two figures come from different sources, which is worth knowing.
        Assert.Equal(104.0, BatteryHealthMath.HealthPercent(40000, 41600));
    }

    [Fact]
    public void A_cycle_count_of_zero_is_not_reported_as_zero()
    {
        // THE test in this file. Measured on device: powercfg and the BatteryCycleCount WMI class
        // both return 0 for a pack that has lost 8.8 % of its capacity. A battery cannot lose that
        // having been charged zero times, so 0 means "the EC does not keep this number".
        //
        // Reporting it as 0 would put "0 cycles" on screen beside "91 % health" and leave the user to
        // resolve the contradiction.
        Assert.Null(BatteryHealthMath.NormaliseCycleCount(0));
        Assert.Null(BatteryHealthMath.NormaliseCycleCount(null));
        Assert.Null(BatteryHealthMath.NormaliseCycleCount(-3));
    }

    [Fact]
    public void A_real_cycle_count_passes_through()
    {
        Assert.Equal(212, BatteryHealthMath.NormaliseCycleCount(212));
    }

    [Fact]
    public void One_sample_is_a_reading_not_a_trend()
    {
        var samples = new List<BatteryHealthSample>
        {
            new(DateTimeOffset.UtcNow, 40009, 91.2),
        };
        Assert.Null(BatteryHealthMath.DegradationPoints(samples));
    }

    [Fact]
    public void Two_samples_on_the_same_day_are_not_a_trend_either()
    {
        // This pack loses single-digit percent over YEARS. Anything sub-daily is measurement jitter,
        // and reporting it as degradation would show alarming decline that reverses by tea time.
        var morning = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var samples = new List<BatteryHealthSample>
        {
            new(morning, 40009, 91.2),
            new(morning.AddHours(6), 39950, 91.0),
        };
        Assert.Null(BatteryHealthMath.DegradationPoints(samples));
    }

    [Fact]
    public void Degradation_across_days_is_reported_in_percentage_points()
    {
        var samples = new List<BatteryHealthSample>
        {
            new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 41000, 93.4),
            new(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), 40009, 91.2),
        };
        Assert.Equal(2.2, BatteryHealthMath.DegradationPoints(samples));
    }

    [Fact]
    public void Degradation_is_computed_from_the_oldest_and_newest_regardless_of_list_order()
    {
        // The store appends, but a corrupted or hand-edited file can arrive out of order, and sorting
        // at the point of use costs nothing.
        var samples = new List<BatteryHealthSample>
        {
            new(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), 40009, 91.2),
            new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 41000, 93.4),
        };
        Assert.Equal(2.2, BatteryHealthMath.DegradationPoints(samples));
    }

    [Fact]
    public void A_sample_with_no_health_figure_cannot_anchor_a_trend()
    {
        var samples = new List<BatteryHealthSample>
        {
            new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null),
            new(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), 40009, 91.2),
        };
        Assert.Null(BatteryHealthMath.DegradationPoints(samples));
    }

    [Fact]
    public void Improving_health_reads_as_negative_degradation_rather_than_being_hidden()
    {
        // Packs do read higher after a calibration cycle. Suppressing it would mean the only way the
        // number ever moves is downward, which quietly turns the trend into a ratchet.
        var samples = new List<BatteryHealthSample>
        {
            new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 39500, 90.0),
            new(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), 40009, 91.2),
        };
        Assert.Equal(-1.2, BatteryHealthMath.DegradationPoints(samples));
    }
}
