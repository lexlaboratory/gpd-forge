// GPD Forge — GET /profiles/active as the wire carries it, populated. GPL-3.0-or-later.
//
// F1 audit round 1 (2026-09-25). Both contract suites only ever saw the inactive answer — the daemon
// fixture runs with auto-profiles off and the mock's seeded rules carry no overrides — and a null
// `applied` skips its field checks. So renaming `stapmW` in the projection would have kept every test
// green while the notice rendered nothing on the device. These validate a POPULATED answer against
// tests/contract/api-contract.json, and pin how each field follows its owner.
using System.Text.Json;
using GpdForge.Gpu;
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class ActiveGameProfileWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static ActiveGameProfile Profile(TdpHold? hold = null) =>
        new("eldenring.exe", Guid.NewGuid(), "eldenring", "gaming",
            new AppliedOverrides(22, 60, "Aggressive", true, null),
            [new SkippedOverride("gpu", "Radeon control is off.")],
            ["discord"], DateTimeOffset.Parse("2026-09-25T10:00:00Z"))
        { CapVersion = 7, TdpHold = hold };

    private static readonly DateTimeOffset Since = DateTimeOffset.Parse("2026-09-25T10:00:00Z");

    /// <summary>What the agent reports once it has carried the profile out: Anti-Lag on, FRTC at 60.
    /// <paramref name="after"/> is how long after the profile went on it read the driver.</summary>
    private static GpuAgentReport Driver(bool antiLag = true, bool antiLagSupported = true, bool? chill = null,
        bool capOn = true, int cap = 60, bool available = true, double after = 20) =>
        new(available, available ? "Ready" : "Unavailable", "1.0", available ? "ok" : "ADLX is not installed",
            new GpuSettingsSnapshot(
                new GpuFeatureState(antiLagSupported, antiLag && antiLagSupported),
                chill is bool c ? new GpuFeatureState(true, c) : null,
                null, null,
                new GpuFeatureState(Supported: true, Enabled: capOn, Value: cap, Min: 15, Max: 1000)),
            Since.AddSeconds(after));

    private static ProfileLiveState Live(int? manual = null, bool fanHeld = true, long capVersion = 7,
        int? guardianW = null, string[]? rivals = null, GpuAgentReport? agent = null, bool silent = false,
        double nowAfter = 22) =>
        new(manual, fanHeld, capVersion, guardianW, () => rivals ?? [],
            silent ? null : agent ?? Driver(), Since.AddSeconds(nowAfter));

    [Fact]
    public void A_populated_profile_matches_the_contract()
    {
        var wire = ActiveGameProfileWire.From(Profile(), Live(manual: 18, guardianW: null));
        var json = JsonSerializer.SerializeToElement(wire, Web);

        var route = ApiContract.Load().Routes.Single(r => r.Method == "GET" && r.Path == "/profiles/active");
        Assert.Empty(ApiContract.Validate(json, route.Shape!.Value, "/profiles/active"));

        // The field names the UI's notice reads, spelled out: the contract checks types, this checks
        // the values landed where noticeParts looks for them.
        var applied = json.GetProperty("applied");
        Assert.Equal(60, applied.GetProperty("frameCapFps").GetInt32());
        Assert.Equal("Aggressive", applied.GetProperty("fanMode").GetString());
        Assert.True(applied.GetProperty("gpu").GetProperty("antiLag").GetBoolean());
        Assert.Equal("gpu", json.GetProperty("skipped")[0].GetProperty("field").GetString());
        Assert.Equal("stapmW", json.GetProperty("superseded")[0].GetString());
    }

    [Fact]
    public void Nothing_in_force_is_the_inactive_answer_and_matches_the_contract()
    {
        var json = JsonSerializer.SerializeToElement(ActiveGameProfileWire.From(null, Live()), Web);
        var route = ApiContract.Load().Routes.Single(r => r.Method == "GET" && r.Path == "/profiles/active");
        Assert.Empty(ApiContract.Validate(json, route.Shape!.Value, "/profiles/active"));
        Assert.False(json.GetProperty("active").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("applied").ValueKind);
    }

    [Fact]
    public void Untouched_the_profile_reports_what_it_applied()
    {
        var w = ActiveGameProfileWire.From(Profile(), Live());
        Assert.Equal(new AppliedWire(22, 60, "Aggressive", new GpuFeaturesWire(true, null)), w.Applied);
        Assert.Empty(w.Superseded);
    }

    [Fact]
    public void What_the_user_changed_mid_game_is_superseded_not_applied()
    {
        var w = ActiveGameProfileWire.From(Profile(), Live(manual: 18, fanHeld: false, capVersion: 8));

        Assert.Equal(new AppliedWire(null, null, null, new GpuFeaturesWire(true, null)), w.Applied);
        Assert.Equal(["stapmW", "fanMode", "frameCapFps"], w.Superseded);
    }

    [Fact]
    public void A_manual_value_equal_to_the_games_supersedes_nothing()
    {
        // Set 22 W in the overlay, then "save as profile": the game's 22 W is exactly what is in force.
        Assert.Equal(22, ActiveGameProfileWire.From(Profile(), Live(manual: 22)).Applied!.StapmW);
    }

    [Fact]
    public void A_guardian_throttle_below_the_games_watts_moves_them_to_skipped_while_it_lasts()
    {
        var held = ActiveGameProfileWire.From(Profile(), Live(guardianW: 12));
        Assert.Null(held.Applied!.StapmW);
        Assert.Contains(held.Skipped, s => s.Field == "stapmW" && s.Reason.Contains("12 W"));

        // A ceiling above the game's value is not in its way; nor is a throttle that has cleared.
        Assert.Equal(22, ActiveGameProfileWire.From(Profile(), Live(guardianW: 25)).Applied!.StapmW);
        Assert.Equal(22, ActiveGameProfileWire.From(Profile(), Live(guardianW: null)).Applied!.StapmW);
    }

    [Fact]
    public void A_yield_to_a_rival_names_it_while_it_is_still_running()
    {
        var hold = new TdpHold(ApplyOutcome.SkippedConflict, ["MotionAssistant"]);

        var yielded = ActiveGameProfileWire.From(Profile(hold), Live(rivals: ["MotionAssistant"]));
        Assert.Null(yielded.Applied!.StapmW);
        Assert.Contains(yielded.Skipped, s => s.Field == "stapmW" && s.Reason.Contains("MotionAssistant"));

        // Gone: the reassert puts the intent (the game's watts) back.
        Assert.Equal(22, ActiveGameProfileWire.From(Profile(hold), Live(rivals: [])).Applied!.StapmW);
    }

    [Fact]
    public void The_rivals_are_only_asked_for_after_a_yield()
    {
        var asked = 0;
        var live = new ProfileLiveState(null, true, 7, null, () => { asked++; return []; }, Driver(), Since.AddSeconds(22));
        ActiveGameProfileWire.From(Profile(), live);
        Assert.Equal(0, asked);   // a process enumeration per poll would be paid for nothing
    }

    // --- F1 audit round 2: the Radeon side is checked against what the agent reports -------------

    [Fact]
    public void Features_and_cap_the_driver_reports_as_asked_stay_applied()
    {
        var w = ActiveGameProfileWire.From(Profile(), Live(agent: Driver()));
        Assert.Equal(new AppliedWire(22, 60, "Aggressive", new GpuFeaturesWire(true, null)), w.Applied);
        Assert.Single(w.Skipped);   // only the fixture's own gpu entry
    }

    [Fact]
    public void A_feature_the_driver_did_not_take_moves_to_skipped()
    {
        var w = ActiveGameProfileWire.From(Profile(), Live(agent: Driver(antiLag: false)));
        Assert.Null(w.Applied!.Gpu.AntiLag);
        Assert.Contains(w.Skipped, s => s.Field == "antiLag" && s.Reason.Contains("did not take it"));
    }

    [Fact]
    public void A_feature_the_driver_does_not_support_moves_to_skipped_and_says_so()
    {
        var w = ActiveGameProfileWire.From(Profile(), Live(agent: Driver(antiLagSupported: false)));
        Assert.Null(w.Applied!.Gpu.AntiLag);
        Assert.Contains(w.Skipped, s => s.Field == "antiLag" && s.Reason.Contains("does not offer"));
    }

    [Fact]
    public void A_cap_the_driver_did_not_take_moves_to_skipped_with_what_it_holds()
    {
        var off = ActiveGameProfileWire.From(Profile(), Live(agent: Driver(capOn: false)));
        Assert.Null(off.Applied!.FrameCapFps);
        Assert.Contains(off.Skipped, s => s.Field == "frameCapFps" && s.Reason.Contains("no cap"));

        var other = ActiveGameProfileWire.From(Profile(), Live(agent: Driver(cap: 45)));
        Assert.Contains(other.Skipped, s => s.Field == "frameCapFps" && s.Reason.Contains("45 FPS"));
    }

    [Fact]
    public void Before_the_agent_has_had_time_to_carry_it_out_nothing_is_contradicted()
    {
        // The agent posts what the driver holds and THEN reconciles, every 3 s: its first report after
        // the profile went on still shows the driver from before.
        var early = ActiveGameProfileWire.From(Profile(), Live(agent: Driver(antiLag: false, capOn: false, after: 2), nowAfter: 3));
        Assert.Equal(new AppliedWire(22, 60, "Aggressive", new GpuFeaturesWire(true, null)), early.Applied);
    }

    [Fact]
    public void A_silent_or_stale_agent_leaves_the_radeon_side_unconfirmed_not_applied()
    {
        foreach (var live in new[] { Live(silent: true), Live(agent: Driver(after: 20), nowAfter: 20 + 31) })
        {
            var w = ActiveGameProfileWire.From(Profile(), live);
            Assert.Null(w.Applied!.FrameCapFps);
            Assert.Null(w.Applied.Gpu.AntiLag);
            Assert.Equal(22, w.Applied.StapmW);   // TDP and fan are not the agent's
            Assert.Contains(w.Skipped, s => s.Field == "frameCapFps" && s.Reason.Contains("not reporting"));
            Assert.Contains(w.Skipped, s => s.Field == "antiLag" && s.Reason.Contains("not reporting"));
        }
    }

    [Fact]
    public void An_agent_reporting_radeon_control_unavailable_mid_game_is_said()
    {
        var w = ActiveGameProfileWire.From(Profile(), Live(agent: Driver(available: false)));
        Assert.Contains(w.Skipped, s => s.Field == "antiLag" && s.Reason.Contains("ADLX is not installed"));
        Assert.Null(w.Applied!.FrameCapFps);
    }

    [Fact]
    public void A_cap_the_user_changed_since_is_superseded_not_checked_against_the_driver()
    {
        var w = ActiveGameProfileWire.From(Profile(), Live(capVersion: 8, agent: Driver(cap: 45)));
        Assert.Contains("frameCapFps", w.Superseded);
        Assert.DoesNotContain(w.Skipped, s => s.Field == "frameCapFps");
    }
}
