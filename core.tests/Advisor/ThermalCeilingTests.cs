// GPD Forge — learned thermal ceiling tests (plan F3). GPL-3.0-or-later.
using GpdForge.Advisor;
using Xunit;

namespace GpdForge.Core.Tests.Advisor;

public class ThermalCeilingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpdforge-ceiling-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Learns_only_once_the_throttle_has_held()
    {
        var l = new ThermalCeilingLearner();
        l.Observe("EldenRing.exe", 25, At(0));
        l.Observe("EldenRing.exe", 25, At(30));
        Assert.Null(l.CeilingFor("eldenring"));
        l.Observe("EldenRing.exe", 25, At(ThermalCeilingLearner.SettleSeconds));
        Assert.Equal(25, l.CeilingFor("eldenring.exe"));
    }

    [Fact]
    public void A_step_restarts_the_settle_clock()
    {
        var l = new ThermalCeilingLearner();
        l.Observe("game", 28, At(0));
        l.Observe("game", 25, At(50));
        l.Observe("game", 25, At(100));
        Assert.Null(l.CeilingFor("game"));
        l.Observe("game", 25, At(110));
        Assert.Equal(25, l.CeilingFor("game"));
    }

    [Fact]
    public void One_sample_per_settle_then_an_ema()
    {
        var l = new ThermalCeilingLearner();
        l.Observe("game", 25, At(0));
        l.Observe("game", 25, At(60));
        l.Observe("game", 25, At(600)); // same episode: no second sample
        Assert.Equal(25, l.CeilingFor("game"));
        l.Observe("game", null, At(601)); // throttle released
        l.Observe("game", 19, At(700));
        l.Observe("game", 19, At(760));
        Assert.Equal(25 + ThermalCeilingLearner.Alpha * (19 - 25), l.CeilingFor("game")!.Value, 6);
    }

    [Fact]
    public void A_game_switch_or_no_game_resets()
    {
        var l = new ThermalCeilingLearner();
        l.Observe("a", 22, At(0));
        l.Observe("b", 22, At(40));
        l.Observe(null, 22, At(80));
        l.Observe("b", 22, At(90));
        Assert.Null(l.CeilingFor("a"));
        Assert.Null(l.CeilingFor("b"));
    }

    [Fact]
    public void Persists_and_ignores_a_corrupt_file()
    {
        var l = new ThermalCeilingLearner(_dir);
        l.Observe("game", 22, At(0));
        l.Observe("game", 22, At(60));
        Assert.Equal(22, new ThermalCeilingLearner(_dir).CeilingFor("game"));

        File.WriteAllText(Path.Combine(_dir, ThermalCeilingLearner.FileName), "{not json");
        Assert.Null(new ThermalCeilingLearner(_dir).CeilingFor("game"));
    }

    [Fact]
    public void Drops_out_of_band_values_on_load()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, ThermalCeilingLearner.FileName), """{"a": 22, "b": -3, "c": 900}""");
        var l = new ThermalCeilingLearner(_dir);
        Assert.Equal(22, l.CeilingFor("a"));
        Assert.Null(l.CeilingFor("b"));
        Assert.Null(l.CeilingFor("c"));
    }
}
