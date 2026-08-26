// GPD Forge — first-run onboarding: incumbent power-controller check tests (pure logic). GPL-3.0-or-later.
using GpdForge.Onboarding;
using Xunit;

namespace GpdForge.Core.Tests;

public class IncumbentsCheckTests
{
    [Fact]
    public void Reports_both_false_when_nothing_is_running()
    {
        var s = IncumbentsCheck.From([]);
        Assert.False(s.MotionAssistant);
        Assert.False(s.GpdTool);
    }

    [Fact]
    public void Detects_MotionAssistant_by_its_own_process_name()
    {
        var s = IncumbentsCheck.From(["MotionAssistant"]);
        Assert.True(s.MotionAssistant);
        Assert.False(s.GpdTool);
    }

    [Fact]
    public void Detects_MotionAssistant_via_its_pmgui_helper_process()
    {
        var s = IncumbentsCheck.From(["pmgui"]);
        Assert.True(s.MotionAssistant);
        Assert.False(s.GpdTool);
    }

    [Fact]
    public void Detects_GpdTool_by_either_of_its_process_names()
    {
        Assert.True(IncumbentsCheck.From(["GPDTool"]).GpdTool);
        Assert.True(IncumbentsCheck.From(["GPDToolService"]).GpdTool);
    }

    [Fact]
    public void Detects_both_at_once()
    {
        var s = IncumbentsCheck.From(["MotionAssistant", "GPDTool"]);
        Assert.True(s.MotionAssistant);
        Assert.True(s.GpdTool);
    }

    [Fact]
    public void Is_case_insensitive()
    {
        Assert.True(IncumbentsCheck.From(["motionassistant"]).MotionAssistant);
        Assert.True(IncumbentsCheck.From(["gpdtoolservice"]).GpdTool);
    }

    [Fact]
    public void Ignores_unrelated_process_names()
    {
        var s = IncumbentsCheck.From(["chrome", "explorer"]);
        Assert.False(s.MotionAssistant);
        Assert.False(s.GpdTool);
    }
}
