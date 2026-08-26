// GPD Forge — LED/RGB pure logic + service tests. GPL-3.0-or-later.
using GpdForge.Led;
using Xunit;

namespace GpdForge.Core.Tests;

public class LedColorTests
{
    [Theory]
    [InlineData("#00c8ff", 0x00, 0xc8, 0xff)]
    [InlineData("00C8FF", 0x00, 0xc8, 0xff)]
    [InlineData("#FFFFFF", 255, 255, 255)]
    [InlineData("000000", 0, 0, 0)]
    [InlineData("  #AbCdEf  ", 0xAB, 0xCD, 0xEF)]
    public void Parse_accepts_hash_prefix_and_mixed_case(string text, int r, int g, int b)
    {
        var c = LedColor.Parse(text);
        Assert.Equal((byte)r, c.R);
        Assert.Equal((byte)g, c.G);
        Assert.Equal((byte)b, c.B);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#fff")]
    [InlineData("gggggg")]
    [InlineData("#0000000")]
    [InlineData("#00000")]
    [InlineData(null)]
    public void TryParse_rejects_invalid_input(string? text)
    {
        Assert.False(LedColor.TryParse(text, out _));
    }

    [Fact]
    public void Parse_throws_FormatException_on_invalid_input()
    {
        Assert.Throws<FormatException>(() => LedColor.Parse("not-a-color"));
    }

    [Fact]
    public void ToHex_round_trips_through_Parse_as_lowercase()
    {
        var c = LedColor.Parse("#AABBCC");
        Assert.Equal("#aabbcc", c.ToHex());
    }

    [Fact]
    public void ToRgb888_packs_channels_red_green_blue()
    {
        var c = new LedColor(0x11, 0x22, 0x33);
        Assert.Equal(0x112233, c.ToRgb888());
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(255, 255)]
    [InlineData(300, 255)]
    public void FromRgb_clamps_each_channel(int input, int expected)
    {
        var c = LedColor.FromRgb(input, input, input);
        Assert.Equal((byte)expected, c.R);
        Assert.Equal((byte)expected, c.G);
        Assert.Equal((byte)expected, c.B);
    }
}

public class LedModeEncodingTests
{
    [Theory]
    [InlineData(LedMode.Off, 0x00)]
    [InlineData(LedMode.Solid, 0x01)]
    [InlineData(LedMode.Breathe, 0x11)]
    [InlineData(LedMode.Rotate, 0x21)]
    public void ToByte_matches_the_documented_Win4_encoding(LedMode mode, byte expected)
    {
        Assert.Equal(expected, mode.ToByte());
    }
}

public class HidLedWriterTests
{
    [Fact]
    public void TryWrite_always_reports_failure_on_this_board()
    {
        var writer = new HidLedWriter();
        Assert.False(writer.TryWrite(LedMode.Solid, LedColor.Parse("#ffffff")));
    }
}

public class LedServiceTests
{
    private sealed class FakeLedWriter : ILedHidWriter
    {
        public bool Result = true;
        public int Calls { get; private set; }
        public LedMode? LastMode { get; private set; }
        public LedColor? LastColor { get; private set; }

        public bool TryWrite(LedMode mode, LedColor color)
        {
            Calls++;
            LastMode = mode;
            LastColor = color;
            return Result;
        }
    }

    [Fact]
    public void Get_never_writes_and_reports_the_stored_default()
    {
        var writer = new FakeLedWriter();
        var svc = new LedService(writer, hardwareGateOpen: true);

        var s = svc.Get();

        Assert.Equal(0, writer.Calls);
        Assert.False(s.Applied);
        Assert.Equal("Off", s.Mode);
    }

    [Fact]
    public void Set_with_the_gate_closed_never_calls_the_writer()
    {
        var writer = new FakeLedWriter();
        var svc = new LedService(writer, hardwareGateOpen: false);

        var s = svc.Set(LedMode.Solid, LedColor.Parse("#ff0000"));

        Assert.Equal(0, writer.Calls);
        Assert.False(s.Applied);
        Assert.Equal(LedAdvisor.GateClosedAdvisory, s.Advisory);
        Assert.Equal("Solid", s.Mode);   // still remembered for round-trip
        Assert.Equal("#ff0000", s.Color);
    }

    [Fact]
    public void Set_with_the_gate_open_calls_the_writer_and_stays_honest_when_it_fails()
    {
        var writer = new FakeLedWriter { Result = false };
        var svc = new LedService(writer, hardwareGateOpen: true);

        var s = svc.Set(LedMode.Breathe, LedColor.Parse("#00ff00"));

        Assert.Equal(1, writer.Calls);
        Assert.Equal(LedMode.Breathe, writer.LastMode);
        Assert.False(s.Applied);
        Assert.Equal(LedAdvisor.WriteFailedAdvisory, s.Advisory);
    }

    [Fact]
    public void Set_with_the_gate_open_and_a_successful_write_reports_applied_true()
    {
        var writer = new FakeLedWriter { Result = true };
        var svc = new LedService(writer, hardwareGateOpen: true);

        var s = svc.Set(LedMode.Rotate, null);

        Assert.True(s.Applied);
    }

    [Fact]
    public void Set_without_a_color_keeps_the_previously_stored_color()
    {
        var writer = new FakeLedWriter();
        var svc = new LedService(writer, hardwareGateOpen: false);

        svc.Set(LedMode.Solid, LedColor.Parse("#123456"));
        var s = svc.Set(LedMode.Breathe, null);

        Assert.Equal("#123456", s.Color);
    }
}
