// GPD Forge - resume detection tests. GPL-3.0-or-later.
//
// The detector decides when the machine came back from a suspend. It shares one trick with
// StandbyDrainTracker (wall time minus sleep-excluding time is time spent asleep) and deliberately
// none of its gates: a drain figure is only meaningful unplugged, after a long sleep, when the
// battery actually dropped, whereas a RESTORE is needed after every suspend - short ones, and on
// the charger too. These tests pin that difference, because collapsing the two would silently stop
// restoring on exactly the resumes people notice.
using System;
using GpdForge.Standby;
using Xunit;

namespace GpdForge.Core.Tests;

public class ResumeDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 3, 0, 0, TimeSpan.Zero);

    private static ResumeDetector Detector(int minSleepSeconds = 60) =>
        new(new ResumeDetector.Options(TimeSpan.FromSeconds(minSleepSeconds)));

    [Fact]
    public void First_observation_only_establishes_a_baseline()
    {
        var d = Detector();
        Assert.Null(d.Observe(T0, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Awake_polling_never_reports_a_resume()
    {
        var d = Detector();
        d.Observe(T0, TimeSpan.FromHours(1));

        // Both clocks advance together: the machine never slept.
        for (int i = 1; i <= 20; i++)
        {
            var step = TimeSpan.FromSeconds(5 * i);
            Assert.Null(d.Observe(T0 + step, TimeSpan.FromHours(1) + step));
        }
    }

    [Fact]
    public void Wall_clock_running_ahead_of_the_unbiased_clock_is_a_resume()
    {
        var d = Detector();
        d.Observe(T0, TimeSpan.FromHours(1));

        // Eight hours of wall time, five seconds of it awake.
        var slept = d.Observe(T0 + TimeSpan.FromHours(8), TimeSpan.FromHours(1) + TimeSpan.FromSeconds(5));

        Assert.NotNull(slept);
        Assert.Equal(TimeSpan.FromHours(8) - TimeSpan.FromSeconds(5), slept!.Value);
    }

    [Fact]
    public void A_suspend_shorter_than_the_floor_is_ignored()
    {
        var d = Detector(minSleepSeconds: 60);
        d.Observe(T0, TimeSpan.FromHours(1));

        // A 30 s screen-off blip. Re-initialising the EC on these would mean a write storm.
        var slept = d.Observe(T0 + TimeSpan.FromSeconds(35), TimeSpan.FromHours(1) + TimeSpan.FromSeconds(5));
        Assert.Null(slept);
    }

    [Fact]
    public void The_floor_is_inclusive_at_exactly_the_threshold()
    {
        var d = Detector(minSleepSeconds: 60);
        d.Observe(T0, TimeSpan.FromHours(1));

        var slept = d.Observe(T0 + TimeSpan.FromSeconds(60), TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromSeconds(60), slept);
    }

    [Fact]
    public void Each_suspend_is_reported_exactly_once()
    {
        var d = Detector();
        d.Observe(T0, TimeSpan.FromHours(1));

        var at = T0 + TimeSpan.FromHours(8);
        var unbiased = TimeSpan.FromHours(1) + TimeSpan.FromSeconds(5);
        Assert.NotNull(d.Observe(at, unbiased));

        // The next poll after a resume is an ordinary awake poll, not a second resume.
        Assert.Null(d.Observe(at + TimeSpan.FromSeconds(5), unbiased + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Two_suspends_are_two_reports()
    {
        var d = Detector();
        d.Observe(T0, TimeSpan.FromHours(1));

        var wall = T0 + TimeSpan.FromHours(2);
        var unbiased = TimeSpan.FromHours(1) + TimeSpan.FromSeconds(5);
        Assert.NotNull(d.Observe(wall, unbiased));

        // Awake for a minute, then asleep again.
        wall += TimeSpan.FromMinutes(1);
        unbiased += TimeSpan.FromMinutes(1);
        Assert.Null(d.Observe(wall, unbiased));

        Assert.NotNull(d.Observe(wall + TimeSpan.FromHours(3), unbiased + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_backwards_clock_is_discarded_rather_than_read_as_a_suspend()
    {
        var d = Detector();
        d.Observe(T0, TimeSpan.FromHours(1));

        // Wall clock stepped back (NTP correction, timezone write, VM restore).
        Assert.Null(d.Observe(T0 - TimeSpan.FromHours(4), TimeSpan.FromHours(1) + TimeSpan.FromSeconds(5)));

        // The unbiased clock going backwards is likewise impossible; do not turn it into sleep.
        var d2 = Detector();
        d2.Observe(T0, TimeSpan.FromHours(1));
        Assert.Null(d2.Observe(T0 + TimeSpan.FromHours(8), TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Resuming_on_the_charger_still_counts()
    {
        // The detector takes no battery or AC input at all - that is the point. A resume on mains
        // needs the fan and the power limits back exactly as much as one on battery does, and
        // StandbyDrainTracker's AC gate exists only because a drain PERCENTAGE would be meaningless
        // while charging. Guarded by construction: this must not compile if an AC flag is added.
        var observe = typeof(ResumeDetector).GetMethod(nameof(ResumeDetector.Observe))!;
        Assert.Equal(2, observe.GetParameters().Length);
    }
}
