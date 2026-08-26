// GPD Forge — refresh-rate switching tests (pure parsing/validation + service with a fake source). GPL-3.0-or-later.
using GpdForge.Display;
using Xunit;

namespace GpdForge.Core.Tests;

public class DisplayModeParserTests
{
    [Fact]
    public void Filters_to_the_current_resolution_and_bit_depth()
    {
        var modes = new[]
        {
            new RawDisplayMode(1920, 1080, 32, 60),
            new RawDisplayMode(1920, 1080, 32, 48),
            new RawDisplayMode(1280, 720, 32, 60),   // different resolution — excluded
            new RawDisplayMode(1920, 1080, 16, 60),  // different bit depth — excluded
        };
        var hz = DisplayModeParser.SupportedHz(modes, 1920, 1080, 32);
        Assert.Equal(new[] { 48, 60 }, hz);
    }

    [Fact]
    public void Dedupes_and_sorts_ascending()
    {
        var modes = new[]
        {
            new RawDisplayMode(1920, 1080, 32, 60),
            new RawDisplayMode(1920, 1080, 32, 60), // duplicate
            new RawDisplayMode(1920, 1080, 32, 40),
            new RawDisplayMode(1920, 1080, 32, 48),
        };
        Assert.Equal(new[] { 40, 48, 60 }, DisplayModeParser.SupportedHz(modes, 1920, 1080, 32));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Excludes_the_hardware_default_sentinel_values(int hz)
    {
        var modes = new[] { new RawDisplayMode(1920, 1080, 32, hz), new RawDisplayMode(1920, 1080, 32, 60) };
        Assert.Equal(new[] { 60 }, DisplayModeParser.SupportedHz(modes, 1920, 1080, 32));
    }

    [Fact]
    public void Empty_input_yields_an_empty_list()
    {
        Assert.Empty(DisplayModeParser.SupportedHz(Array.Empty<RawDisplayMode>(), 1920, 1080, 32));
    }
}

public class RefreshRatePickerTests
{
    [Fact]
    public void Accepts_a_supported_rate()
    {
        var (ok, error) = RefreshRatePicker.Validate(60, new[] { 48, 60 });
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void Rejects_an_unsupported_rate_with_a_message_naming_the_supported_list()
    {
        var (ok, error) = RefreshRatePicker.Validate(90, new[] { 48, 60 });
        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("90", error);
        Assert.Contains("48", error);
        Assert.Contains("60", error);
    }

    [Fact]
    public void Reports_none_detected_when_the_supported_list_is_empty()
    {
        var (ok, error) = RefreshRatePicker.Validate(60, Array.Empty<int>());
        Assert.False(ok);
        Assert.Contains("none detected", error);
    }
}

public class RefreshRateServiceTests
{
    private sealed class FakeDisplayModeSource : IDisplayModeSource
    {
        public RefreshRateInfo Info { get; set; } = new(60, new[] { 48, 60 });
        public int ApplyCalls { get; private set; }
        public int? LastAppliedHz { get; private set; }
        public bool ApplyResult { get; set; } = true;

        public RefreshRateInfo Read() => Info;

        public bool Apply(int hz)
        {
            ApplyCalls++;
            LastAppliedHz = hz;
            if (ApplyResult) Info = Info with { CurrentHz = hz };
            return ApplyResult;
        }
    }

    [Fact]
    public void GetInfo_delegates_straight_to_the_source()
    {
        var source = new FakeDisplayModeSource { Info = new RefreshRateInfo(48, new[] { 48, 60 }) };
        var svc = new RefreshRateService(source);
        Assert.Equal(48, svc.GetInfo().CurrentHz);
    }

    [Fact]
    public void SetHz_applies_a_supported_rate_and_reflects_the_new_current()
    {
        var source = new FakeDisplayModeSource();
        var svc = new RefreshRateService(source);

        var (info, error) = svc.SetHz(48);

        Assert.Null(error);
        Assert.Equal(48, info.CurrentHz);
        Assert.Equal(1, source.ApplyCalls);
        Assert.Equal(48, source.LastAppliedHz);
    }

    [Fact]
    public void SetHz_rejects_an_unsupported_rate_without_touching_the_source()
    {
        var source = new FakeDisplayModeSource(); // current 60, supported [48, 60]
        var svc = new RefreshRateService(source);

        var (info, error) = svc.SetHz(144);

        Assert.NotNull(error);
        Assert.Equal(60, info.CurrentHz);   // unchanged
        Assert.Equal(0, source.ApplyCalls); // never called into Win32 with a bad value
    }

    [Fact]
    public void SetHz_surfaces_an_error_when_windows_rejects_the_switch()
    {
        var source = new FakeDisplayModeSource { ApplyResult = false };
        var svc = new RefreshRateService(source);

        var (info, error) = svc.SetHz(48);

        Assert.NotNull(error);
        Assert.Equal(60, info.CurrentHz); // Apply failed — current stays what the source still reports
        Assert.Equal(1, source.ApplyCalls);
    }
}
