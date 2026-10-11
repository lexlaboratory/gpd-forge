// GPD Forge — desktop apps that present frames but are not games. GPL-3.0-or-later.
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class FrameTargetNonGameTests
{
    // Each of these was learned as a "game" on this device (thermal-ceilings.json, 2026-10-10): Orca, the
    // workspace app, at ~23 W, and Electron, RustDesk and Logi Options+ at ~24–25 W. The advisor then
    // proposed a sustained limit for Orca, and applying it gave Orca a rule of its own.
    [Theory]
    [InlineData("orca")]
    [InlineData("Orca.exe")]
    [InlineData("electron")]
    [InlineData("rustdesk")]
    [InlineData("logioptionsplus_agent")]
    public void Desktop_apps_seen_on_this_device_are_not_games(string app) =>
        Assert.True(FrameTarget.IsNonGame(app));

    [Theory]
    [InlineData("eldenring.exe")]
    [InlineData("gta5_enhanced")]
    [InlineData("league of legends")]
    public void Games_are_still_games(string app) =>
        Assert.False(FrameTarget.IsNonGame(app));
}
