// GPD Forge — keyboard backlight advisory tests. GPL-3.0-or-later.
using GpdForge.Display;
using Xunit;

namespace GpdForge.Core.Tests;

public class KeyboardBacklightTests
{
    [Fact]
    public void Get_is_never_controllable_and_never_applied()
    {
        var s = new KeyboardBacklightService().Get();
        Assert.False(s.Controllable);
        Assert.False(s.Applied);
        Assert.Equal(KeyboardBacklightAdvisor.Advisory, s.Advisory);
    }

    [Fact]
    public void Set_is_never_controllable_and_never_applied()
    {
        var s = new KeyboardBacklightService().Set();
        Assert.False(s.Controllable);
        Assert.False(s.Applied);
        Assert.Equal(KeyboardBacklightAdvisor.Advisory, s.Advisory);
    }

    [Fact]
    public void Advisory_is_honest_about_the_EC_being_the_real_owner()
    {
        Assert.Contains("embedded controller", KeyboardBacklightAdvisor.Advisory);
        Assert.DoesNotContain("success", KeyboardBacklightAdvisor.Advisory, StringComparison.OrdinalIgnoreCase);
    }
}
