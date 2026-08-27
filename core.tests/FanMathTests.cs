// GPD Forge — PWM duty conversion (pure math) tests. GPL-3.0-or-later.
using GpdForge.Fan;
using Xunit;

namespace GpdForge.Core.Tests;

public class FanMathTests
{
    // pwmMax for every board in GpdDeviceDb, so the exhaustive checks below cover every real unit.
    public static readonly TheoryData<int> AllPwmMax = new()
    {
        GpdDeviceDb.WinMax2.PwmMax,
        GpdDeviceDb.Win4_6800U.PwmMax,
        GpdDeviceDb.WinMini.PwmMax,
    };

    [Theory]
    [MemberData(nameof(AllPwmMax))]
    public void CastPwm_zero_maps_to_one(int pwmMax)
    {
        Assert.Equal(1, FanMath.CastPwm(0, pwmMax));
    }

    [Theory]
    [MemberData(nameof(AllPwmMax))]
    public void CastPwm_255_maps_to_pwmMax(int pwmMax)
    {
        Assert.Equal(pwmMax, FanMath.CastPwm(255, pwmMax));
    }

    [Theory]
    [MemberData(nameof(AllPwmMax))]
    public void UncastPwm_one_maps_to_zero(int pwmMax)
    {
        Assert.Equal(0, FanMath.UncastPwm(1, pwmMax));
    }

    [Theory]
    [MemberData(nameof(AllPwmMax))]
    public void UncastPwm_pwmMax_maps_to_255(int pwmMax)
    {
        Assert.Equal(255, FanMath.UncastPwm(pwmMax, pwmMax));
    }

    [Theory]
    [MemberData(nameof(AllPwmMax))]
    public void CastPwm_is_monotonic_non_decreasing(int pwmMax)
    {
        int prev = FanMath.CastPwm(0, pwmMax);
        for (int u = 1; u <= 255; u++)
        {
            int cur = FanMath.CastPwm(u, pwmMax);
            Assert.True(cur >= prev, $"CastPwm({u},{pwmMax})={cur} regressed below CastPwm({u - 1},{pwmMax})={prev}");
            prev = cur;
        }
    }

    [Theory]
    [MemberData(nameof(AllPwmMax))]
    public void CastPwm_output_always_within_ec_range(int pwmMax)
    {
        for (int u = 0; u <= 255; u++)
        {
            int ec = FanMath.CastPwm(u, pwmMax);
            Assert.InRange(ec, 1, pwmMax);
        }
    }

    [Theory]
    [MemberData(nameof(AllPwmMax))]
    public void Round_trip_cast_then_uncast_stays_within_plus_minus_one(int pwmMax)
    {
        for (int u = 0; u <= 255; u++)
        {
            int ec = FanMath.CastPwm(u, pwmMax);
            int back = FanMath.UncastPwm(ec, pwmMax);
            Assert.InRange(back, u - 1, u + 1);
        }
    }

    [Theory]
    [MemberData(nameof(AllPwmMax))]
    public void Round_trip_uncast_then_cast_stays_within_plus_minus_one(int pwmMax)
    {
        for (int ec = 1; ec <= pwmMax; ec++)
        {
            int user = FanMath.UncastPwm(ec, pwmMax);
            int back = FanMath.CastPwm(user, pwmMax);
            Assert.InRange(back, ec - 1, ec + 1);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-1000)]
    [InlineData(int.MinValue)]
    public void CastPwm_clamps_negative_input_to_zero(int belowZero)
    {
        Assert.Equal(FanMath.CastPwm(0, 184), FanMath.CastPwm(belowZero, 184));
    }

    [Theory]
    [InlineData(256)]
    [InlineData(9999)]
    [InlineData(int.MaxValue)]
    public void CastPwm_clamps_above_255_input_to_255(int above255)
    {
        Assert.Equal(FanMath.CastPwm(255, 184), FanMath.CastPwm(above255, 184));
    }

    [Fact]
    public void UncastPwm_clamps_output_to_zero_for_ec_below_one()
    {
        // EC byte 0 (or any stray negative) must never uncast to a negative duty.
        Assert.Equal(0, FanMath.UncastPwm(0, 184));
        Assert.Equal(0, FanMath.UncastPwm(-5, 184));
    }

    [Fact]
    public void UncastPwm_clamps_output_to_255_for_ec_above_pwmMax()
    {
        Assert.Equal(255, FanMath.UncastPwm(9999, 184));
    }

    [Fact]
    public void CastPwm_matches_the_documented_wm2_examples()
    {
        // From the spec: wm2 board, pwm_max=184.
        Assert.Equal(1, FanMath.CastPwm(0, 184));
        Assert.Equal(184, FanMath.CastPwm(255, 184));
        // Midpoint sanity check: ~50% user duty casts to ~50% of the EC range.
        int mid = FanMath.CastPwm(128, 184);
        Assert.InRange(mid, 90, 96);
    }
}
