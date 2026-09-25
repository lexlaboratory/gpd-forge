// GPD Forge — Forge Advisor rule table (plan F3). GPL-3.0-or-later.
//
// Each row is a scenario: what the game is doing, and the suggestion kinds the advisor must make (in
// order). The numbers come from this device: 130–144 FPS uncapped on the 60 Hz panel with a 1 % low of
// 2–17, ~30 once capped at 60, and the guardian settling at ~22 W.
using GpdForge.Advisor;
using GpdForge.Profiles;
using GpdForge.Sessions;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests.Advisor;

public class AdvisorRulesTests
{
    private static FramePacingMetrics Live(double avg, double low, double span = 10) =>
        new(Frames: (int)(avg * span), SpanSeconds: span, FpsAvg: avg, Fps1PctLow: low, Fps01PctLow: low,
            FrameTimeStdDevMs: 1, Stutters: 0, StuttersPerMin: 0);

    private static GameSession Session(double? avg, double? low, int? cap) =>
        new(Guid.NewGuid(), "eldenring.exe", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(10), 600, 600, 0,
            avg, low, avg, null, null, null, false, null, null, null, [], FrameCapFps: cap);

    private static AdvisorInput Input(
        FramePacingMetrics? live = null, GameSession? last = null, int? hz = 60, int? cap = null,
        bool throttling = false, double? ceiling = null, int? stapm = 28, RuleOverrides? profile = null,
        bool onBattery = false, string? mode = null, IReadOnlyList<ModeStats>? modes = null) =>
        new("eldenring.exe", live, last, hz, cap, throttling, ceiling, stapm, profile, onBattery, mode, modes);

    // How the game ran in each mode (plan F6); 10 min of gaming-battery play unless said otherwise.
    private static ModeStats Mode(string mode, double? avg, double? low, double minutes = 10, double? whPerHour = 12) =>
        new(mode, 1, minutes * 60, avg, low, whPerHour, whPerHour is null ? null : "battery",
            avg is double a && whPerHour is double w ? Math.Round(a / w, 2) : null);

    private static readonly ModeStats[] HoldsOnBattery = [Mode("gaming", 60, 45, whPerHour: 20), Mode("gaming-battery", 45, 35)];

    public static TheoryData<string, AdvisorInput, string[]> Scenarios => new()
    {
        { "nothing live, no history", Input(), [] },
        { "uncapped 140 FPS on 60 Hz, lows well over refresh", Input(live: Live(140, 100)), ["cap_refresh", "fewer_watts"] },
        { "uncapped 140 FPS on 60 Hz, lows near refresh", Input(live: Live(140, 70)), ["cap_refresh"] },
        { "uncapped but only just over refresh", Input(live: Live(62, 50)), [] },
        { "uncapped 90 FPS with poor lows: cap, not fewer watts", Input(live: Live(90, 40)), ["cap_refresh"] },
        { "already capped at refresh", Input(live: Live(140, 70), cap: 60), [] },
        { "cap above refresh does not count", Input(live: Live(140, 50), cap: 120), ["cap_refresh"] },
        { "refresh unknown: no cap advice", Input(live: Live(140, 50), hz: null), [] },
        { "profile already caps it (history run predates it)", Input(last: Session(140, 50, null), profile: new(FrameCapFps: 60)), [] },
        { "from history when not live", Input(last: Session(140, 50, null)), ["cap_refresh"] },
        { "history ran capped", Input(last: Session(140, 50, 60)), [] },
        { "throttled with lows under half the average", Input(live: Live(55, 20), cap: 60, throttling: true), ["cap_30"] },
        { "throttled, uncapped above refresh: cap to refresh first", Input(live: Live(130, 10), throttling: true), ["cap_refresh"] },
        { "throttled but lows are fine", Input(live: Live(55, 40), cap: 60, throttling: true), [] },
        { "poor lows, guardian not throttling", Input(live: Live(55, 20), cap: 60), [] },
        { "throttled at cap 30 already: resolution hint", Input(live: Live(29, 10), cap: 30, throttling: true), ["lower_resolution"] },
        { "throttle rules need live data", Input(last: Session(55, 20, 60), throttling: true), [] },
        { "learned ceiling", Input(live: Live(55, 40), cap: 60, ceiling: 22.4), ["stapm_ceiling"] },
        { "learned ceiling already in the profile", Input(ceiling: 22.4, profile: new(StapmW: 22)), [] },
        { "light game beats ceiling advice", Input(live: Live(140, 100), ceiling: 22), ["cap_refresh", "fewer_watts"] },
        { "light game, sustained limit unknown", Input(live: Live(140, 100), stapm: null), ["cap_refresh"] },
        { "light game, profile already lower", Input(live: Live(140, 100), profile: new(StapmW: 18)), ["cap_refresh"] },
        { "a 2 s loading burst is not evidence", Input(live: Live(300, 200, span: 2)), [] },
        { "unplugged in gaming, gaming-battery held up", Input(onBattery: true, mode: "gaming", modes: HoldsOnBattery), ["battery_mode"] },
        { "plugged in: no battery advice", Input(mode: "gaming", modes: HoldsOnBattery), [] },
        { "already in gaming-battery", Input(onBattery: true, mode: "gaming-battery", modes: HoldsOnBattery), [] },
        { "another mode the user chose", Input(onBattery: true, mode: "windows", modes: HoldsOnBattery), [] },
        { "gaming-battery never played", Input(onBattery: true, mode: "gaming", modes: [Mode("gaming", 60, 45)]), [] },
        { "gaming-battery ran under 30 FPS", Input(onBattery: true, mode: "gaming", modes: [Mode("gaming-battery", 25, 20)]), [] },
        { "gaming-battery stuttered", Input(onBattery: true, mode: "gaming", modes: [Mode("gaming-battery", 45, 15)]), [] },
        { "too little gaming-battery play", Input(onBattery: true, mode: "gaming", modes: [Mode("gaming-battery", 45, 35, minutes: 3)]), [] },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Suggests(string scenario, AdvisorInput input, string[] expected)
    {
        var kinds = AdvisorRules.Advise(input).Select(s => s.Kind).ToArray();
        Assert.True(expected.SequenceEqual(kinds), $"{scenario}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", kinds)}]");
    }

    [Fact]
    public void Cap_to_refresh_carries_the_refresh_rate_and_a_stable_id()
    {
        var s = AdvisorRules.Advise(Input(live: Live(140, 50), hz: 120)).Single(x => x.Kind == "cap_refresh");
        Assert.Equal(120, s.FrameCapFps);
        Assert.Null(s.StapmW);
        Assert.Equal("cap_refresh:eldenring:120", s.Id);
        Assert.True(s.Applicable);
    }

    [Fact]
    public void Cap_30_and_the_hint()
    {
        var cap = AdvisorRules.Advise(Input(live: Live(55, 20), cap: 60, throttling: true)).Single();
        Assert.Equal(30, cap.FrameCapFps);
        var hint = AdvisorRules.Advise(Input(live: Live(29, 10), cap: 30, throttling: true)).Single();
        Assert.False(hint.Applicable);
        Assert.Equal("lower_resolution:eldenring", hint.Id);
    }

    [Fact]
    public void Ceiling_rounds_and_clamps_into_the_manual_band()
    {
        Assert.Equal(22, AdvisorRules.Advise(Input(ceiling: 22.4)).Single().StapmW);
        Assert.Equal(RuleOverridesPolicy.MinStapmW, AdvisorRules.Advise(Input(ceiling: 1)).Single().StapmW);
    }

    [Fact]
    public void Fewer_watts_steps_a_quarter_down()
    {
        var s = AdvisorRules.Advise(Input(live: Live(140, 100), stapm: 28)).Single(x => x.Kind == "fewer_watts");
        Assert.Equal(21, s.StapmW);
        Assert.Null(s.FrameCapFps);
    }

    [Fact]
    public void Battery_mode_is_advice_with_the_evidence_in_it()
    {
        var s = AdvisorRules.Advise(Input(onBattery: true, mode: "gaming", modes: HoldsOnBattery)).Single();
        Assert.Equal("battery_mode:eldenring", s.Id);
        Assert.False(s.Applicable);
        Assert.Contains("45 FPS", s.Detail);
        Assert.Contains("12 W", s.Detail);
        Assert.Contains("20 W", s.Detail);
        Assert.Equal("battery_mode:eldenring", AdvisorRules.Advise(Input(onBattery: true, mode: "gaming",
            modes: [Mode("gaming-battery", 45, null, whPerHour: null)])).Single().Id);
    }

    [Fact]
    public void Id_game_round_trips()
    {
        Assert.Equal("eldenring", AdvisorRules.GameOf("cap_refresh:eldenring:60"));
        Assert.Equal("eldenring", AdvisorRules.GameOf("lower_resolution:eldenring"));
        Assert.Null(AdvisorRules.GameOf("nonsense"));
        Assert.Null(AdvisorRules.GameOf(null));
    }
}
