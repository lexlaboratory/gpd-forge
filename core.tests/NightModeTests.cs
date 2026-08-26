// GPD Forge — night mode tests (pure gamma-ramp math + service with a fake sink). GPL-3.0-or-later.
using GpdForge.Display;
using Xunit;

namespace GpdForge.Core.Tests;

public class GammaRampTests
{
    [Fact]
    public void Warmth_zero_is_the_identity_ramp()
    {
        var ramp = GammaRamp.Build(0);
        for (int i = 0; i < GammaRamp.ChannelSize; i++)
        {
            int expected = i * 257;
            Assert.Equal(expected, ramp.Red[i]);
            Assert.Equal(expected, ramp.Green[i]);
            Assert.Equal(expected, ramp.Blue[i]);
        }
    }

    [Fact]
    public void Every_channel_has_256_entries()
    {
        var ramp = GammaRamp.Build(50);
        Assert.Equal(256, ramp.Red.Length);
        Assert.Equal(256, ramp.Green.Length);
        Assert.Equal(256, ramp.Blue.Length);
    }

    [Fact]
    public void Endpoints_span_the_full_16_bit_range_at_zero_warmth()
    {
        var ramp = GammaRamp.Build(0);
        Assert.Equal(0, ramp.Red[0]);
        Assert.Equal(65535, ramp.Red[255]);
    }

    [Fact]
    public void Positive_warmth_reduces_blue_more_than_green_and_leaves_red_untouched()
    {
        var identity = GammaRamp.Build(0);
        var warm = GammaRamp.Build(100);

        Assert.Equal(identity.Red, warm.Red); // red is never touched by the warm recipe
        for (int i = 1; i < GammaRamp.ChannelSize; i++) // skip i=0, both channels are 0 there
        {
            Assert.True(warm.Green[i] <= identity.Green[i]);
            Assert.True(warm.Blue[i] <= identity.Blue[i]);
            Assert.True(warm.Blue[i] <= warm.Green[i], "blue should be cut at least as hard as green");
        }
        Assert.True(warm.Blue[255] < identity.Blue[255]);
    }

    [Fact]
    public void Each_channel_is_monotonically_increasing()
    {
        var ramp = GammaRamp.Build(75);
        for (int i = 1; i < GammaRamp.ChannelSize; i++)
        {
            Assert.True(ramp.Red[i] >= ramp.Red[i - 1]);
            Assert.True(ramp.Green[i] >= ramp.Green[i - 1]);
            Assert.True(ramp.Blue[i] >= ramp.Blue[i - 1]);
        }
    }

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(500, 100)]
    public void Clamps_warmth_into_0_100(int input, int clamped)
    {
        // GammaRampValues is a record struct over ushort[] fields, whose generated equality compares
        // arrays by reference — compare the channels directly so this is a real content check.
        var a = GammaRamp.Build(input);
        var b = GammaRamp.Build(clamped);
        Assert.Equal(b.Red, a.Red);
        Assert.Equal(b.Green, a.Green);
        Assert.Equal(b.Blue, a.Blue);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-5, true)]
    [InlineData(1, false)]
    [InlineData(100, false)]
    public void IsIdentity_matches_the_zero_warmth_case(int warmth, bool expected)
    {
        Assert.Equal(expected, GammaRamp.IsIdentity(warmth));
    }
}

public class NightModeServiceTests
{
    private sealed class FakeGammaRampSink : IGammaRampSink
    {
        public int ApplyCalls { get; private set; }
        public GammaRampValues? LastRamp { get; private set; }
        public bool ApplyResult { get; set; } = true;

        public bool Apply(GammaRampValues ramp)
        {
            ApplyCalls++;
            LastRamp = ramp;
            return ApplyResult;
        }
    }

    [Fact]
    public void Starts_off_at_zero_warmth()
    {
        var svc = new NightModeService(new FakeGammaRampSink());
        Assert.False(svc.On);
        Assert.Equal(0, svc.Warmth);
    }

    [Fact]
    public void Turning_on_with_a_warmth_applies_a_non_identity_ramp_and_updates_state()
    {
        var sink = new FakeGammaRampSink();
        var svc = new NightModeService(sink);

        var (on, warmth) = svc.Set(true, 60);

        Assert.True(on);
        Assert.Equal(60, warmth);
        Assert.True(svc.On);
        Assert.Equal(60, svc.Warmth);
        Assert.Equal(1, sink.ApplyCalls);
        Assert.False(GammaRamp.IsIdentity(60));
        AssertSameRamp(GammaRamp.Build(60), sink.LastRamp);
    }

    [Fact]
    public void Turning_off_applies_the_identity_ramp_and_reports_warmth_zero()
    {
        var sink = new FakeGammaRampSink();
        var svc = new NightModeService(sink);
        svc.Set(true, 80);

        var (on, warmth) = svc.Set(false, null);

        Assert.False(on);
        Assert.Equal(0, warmth);
        AssertSameRamp(GammaRamp.Build(0), sink.LastRamp);
    }

    /// <summary>GammaRampValues is a record struct over ushort[] fields, whose generated equality
    /// compares arrays by reference — compare channel contents directly instead.</summary>
    private static void AssertSameRamp(GammaRampValues expected, GammaRampValues? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Red, actual!.Value.Red);
        Assert.Equal(expected.Green, actual.Value.Green);
        Assert.Equal(expected.Blue, actual.Value.Blue);
    }

    [Fact]
    public void Turning_on_without_a_warmth_reuses_the_last_requested_value()
    {
        var sink = new FakeGammaRampSink();
        var svc = new NightModeService(sink);
        svc.Set(true, 30);
        svc.Set(false, null);

        var (on, warmth) = svc.Set(true, null);

        Assert.True(on);
        Assert.Equal(30, warmth);
    }

    [Fact]
    public void Warmth_is_clamped_into_0_100()
    {
        var svc = new NightModeService(new FakeGammaRampSink());
        Assert.Equal(100, svc.Set(true, 500).Warmth);
        Assert.Equal(0, svc.Set(true, -20).Warmth); // clamped request of -20 -> 0 -> reported as off-equivalent warmth, still "on"
    }

    [Fact]
    public void A_failed_apply_leaves_the_reported_state_unchanged()
    {
        var sink = new FakeGammaRampSink();
        var svc = new NightModeService(sink);
        svc.Set(true, 40); // baseline: on, warmth 40

        sink.ApplyResult = false;
        var (on, warmth) = svc.Set(false, null); // would turn off, but the native call fails

        Assert.True(on);        // unchanged — never claims success it couldn't deliver
        Assert.Equal(40, warmth);
        Assert.True(svc.On);
        Assert.Equal(40, svc.Warmth);
    }
}
