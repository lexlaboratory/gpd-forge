// GPD Forge — GPU profile availability and the ADLX vtable canary. GPL-3.0-or-later.
//
// No ADLX here: the interop itself is proven on real hardware by `--probe-gpu`, which is the only
// honest way to test a hand-written vtable layout. What IS unit-testable is the judgement around it —
// when the canary's answer should be believed, and what the service reports when it should not.
using GpdForge.Gpu;
using Xunit;

namespace GpdForge.Core.Tests;

public class AdlxCanaryTests
{
    [Fact]
    public void A_matching_ram_reading_is_accepted()
    {
        // The real case, measured on the device 2026-08-29: ADLX and WMI both said 28280 MB.
        Assert.True(AdlxInterop.IsPlausibleRam(28280, 28280, out _));
    }

    [Fact]
    public void Rounding_differences_are_tolerated_because_they_are_expected()
    {
        // ADLX and WMI round differently and firmware reserves a slice, so equality would reject
        // healthy systems. A misaligned vtable is wrong by orders of magnitude, not by 3%.
        Assert.True(AdlxInterop.IsPlausibleRam(28280, 29000, out _));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(12u)]
    [InlineData(9_000_000u)]
    public void An_impossible_reading_is_rejected_even_with_nothing_to_compare_against(uint reported)
    {
        // Zero expected = no second opinion available. The reading must still be sanity-checked:
        // 0 MB or 9 TB of RAM means we read something that is not a RAM figure.
        Assert.False(AdlxInterop.IsPlausibleRam(reported, 0, out var why));
        Assert.Contains("vtable", why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_reading_that_describes_a_different_machine_is_rejected()
    {
        // Plausible as a number, wrong for THIS machine — which is exactly what a wrong slot index
        // would produce if it happened to land on another integer field.
        Assert.False(AdlxInterop.IsPlausibleRam(2048, 28280, out var why));
        Assert.Contains("same machine", why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Without_a_second_opinion_a_plausible_reading_passes_and_that_is_the_weaker_check()
    {
        // Documents the deliberate weakening: with expected=0 we can only reject nonsense, not
        // detect a slot that returned some other believable integer. Better than nothing, and the
        // caller knows the difference.
        Assert.True(AdlxInterop.IsPlausibleRam(2048, 0, out _));
    }
}

public class GpuProfileConflictTests
{
    [Fact]
    public void Chill_with_boost_is_reported_as_a_conflict()
    {
        // AMD's driver refuses this combination rather than merging it. Catching it here is the
        // difference between telling the user why, and them watching a switch silently not take.
        var p = new GpuProfile("Efficiency", Chill: true, Boost: true);
        Assert.NotNull(p.Conflict);
        Assert.Contains("Chill", p.Conflict);
    }

    [Fact]
    public void Chill_with_anti_lag_is_reported_as_a_conflict()
        => Assert.NotNull(new GpuProfile("Mixed", Chill: true, AntiLag: true).Conflict);

    [Fact]
    public void Chill_alone_is_fine()
        => Assert.Null(new GpuProfile("Quiet", Chill: true).Conflict);

    [Fact]
    public void Boost_and_anti_lag_together_are_fine()
        => Assert.Null(new GpuProfile("Performance", AntiLag: true, Boost: true).Conflict);
}

public class GpuProfileServiceTests
{
    private sealed class FakeMemory(uint mb) : ISystemMemoryProbe
    {
        public uint TotalRamMb() => mb;
    }

    private static GpuProfileService WithGate(string? gate, uint ram = 28280)
        => new(new FakeMemory(ram), null, name => name == GpuProfileService.GateVariable ? gate : null);

    [Fact]
    public void The_gate_is_closed_by_default_and_says_how_to_open_it()
    {
        var status = WithGate(null).Status();

        Assert.False(status.Available);
        Assert.Equal("Disabled", status.Status);
        // A user told "unavailable" with no next step will conclude it is broken.
        Assert.Contains(GpuProfileService.GateVariable, status.Detail);
    }

    [Fact]
    public void A_closed_gate_never_reports_a_version_it_did_not_read()
    {
        // With the gate shut, ADLX is never initialised, so there is no version to report. Filling
        // that in from anywhere would be inventing a measurement.
        Assert.Null(WithGate(null).Status().AdlxVersion);
    }

    [Fact]
    public void The_answer_is_cached_so_a_driver_library_is_not_initialised_per_request()
    {
        var svc = WithGate(null);
        Assert.Same(svc.Status(), svc.Status());
    }

    [Fact]
    public void Forgetting_the_cached_answer_forces_a_fresh_probe()
    {
        var svc = WithGate(null);
        var first = svc.Status();
        svc.Forget();
        Assert.NotSame(first, svc.Status());
    }
}

public class GpuModeProfileTests
{
    [Fact]
    public void Every_shipped_mode_profile_is_a_combination_the_driver_will_accept()
    {
        // The guard that matters. AMD refuses Chill alongside Boost or Anti-Lag rather than merging
        // them, so a default that trips the rule would half-apply on every mode switch, forever, and
        // look like a flaky driver rather than a bad default.
        foreach (var mode in GpuModeProfiles.Modes)
            Assert.Null(GpuModeProfiles.For(mode)!.Conflict);
    }

    [Fact]
    public void Battery_uses_chill_because_it_is_the_one_feature_that_trades_frames_for_power()
    {
        var p = GpuModeProfiles.For("battery")!;
        Assert.True(p.Chill);
        // And therefore cannot use these two — not a preference, a driver rule.
        Assert.False(p.AntiLag);
        Assert.False(p.Boost);
    }

    [Fact]
    public void Gaming_uses_anti_lag_and_leaves_the_image_alone()
    {
        var p = GpuModeProfiles.For("gaming")!;
        Assert.True(p.AntiLag);
        // Boost lowers resolution during motion. Turning that on for someone silently changes how
        // their games look, which is their call and not a power tool's.
        Assert.False(p.Boost);
        Assert.False(p.Chill);
    }

    [Fact]
    public void Ai_mode_leaves_the_frame_pipeline_alone()
    {
        // Inference is compute. Anti-Lag and Chill act on presentation and would only add a variable.
        var p = GpuModeProfiles.For("ai")!;
        Assert.False(p.AntiLag);
        Assert.False(p.Chill);
        Assert.False(p.Boost);
    }

    [Fact]
    public void An_unknown_mode_has_no_opinion_rather_than_a_default_one()
    {
        // null means "leave the GPU as the user configured it". Returning an all-off profile here
        // would silently undo someone's Adrenalin settings the first time an unmapped mode was used.
        Assert.Null(GpuModeProfiles.For("standby"));
        Assert.Null(GpuModeProfiles.For("nonsense"));
    }
}

public class GpuAgentStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 23, 0, 0, TimeSpan.Zero);

    private static GpuAgentReport ReportAt(DateTimeOffset at, bool available = true)
        => new(available, available ? "Ready" : "InitFailed", "1.5.0.124", "detail", null, at);

    [Fact]
    public void Never_having_reported_is_not_the_same_as_unavailable()
    {
        // The distinction this type exists for. "No agent has looked yet" told to a user as "your GPU
        // cannot be controlled" is a lie that sends them hunting for a hardware problem.
        var (report, usable, why) = new GpuAgentState().Current(Now);

        Assert.Null(report);
        Assert.False(usable);
        Assert.Contains("has not reported yet", why);
    }

    [Fact]
    public void A_recent_report_is_usable()
    {
        var state = new GpuAgentState();
        state.Report(ReportAt(Now.AddSeconds(-3)));

        var (report, usable, _) = state.Current(Now);
        Assert.NotNull(report);
        Assert.True(usable);
    }

    [Fact]
    public void A_stale_report_is_returned_but_marked_unusable_with_its_age()
    {
        // Returned rather than discarded: a client that wants to say "last seen 4 minutes ago" needs
        // the data. Unusable so it can never be rendered as the current state of the machine.
        var state = new GpuAgentState();
        state.Report(ReportAt(Now.AddMinutes(-4)));

        var (report, usable, why) = state.Current(Now);
        Assert.NotNull(report);
        Assert.False(usable);
        Assert.Contains("gone quiet", why);
        Assert.Contains("240s", why);
    }

    [Fact]
    public void An_agent_reporting_unavailable_is_believed_rather_than_second_guessed()
    {
        // The agent is the only thing that can actually talk to ADLX. If it says no, that IS the
        // answer, and the reason it gives is the one worth showing.
        var state = new GpuAgentState();
        state.Report(ReportAt(Now, available: false));

        var (_, usable, _) = state.Current(Now);
        Assert.False(usable);
    }

    [Fact]
    public void The_newest_report_wins()
    {
        var state = new GpuAgentState();
        state.Report(ReportAt(Now.AddMinutes(-5)));
        state.Report(ReportAt(Now));

        Assert.True(state.IsFresh(Now));
    }

    [Fact]
    public void Freshness_is_measured_from_the_report_not_from_process_start()
    {
        // An agent that started an hour ago but reported a second ago is healthy; one that started a
        // second ago and has not reported is not. Only the report time answers that.
        var state = new GpuAgentState();
        Assert.False(state.IsFresh(Now));
        state.Report(ReportAt(Now.AddSeconds(-1)));
        Assert.True(state.IsFresh(Now));
    }
}

public class GpuDesiredStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_is_requested_until_someone_asks()
    {
        // The agent must leave the GPU alone until then. Starting the daemon is not a reason to
        // change someone's Adrenalin settings.
        var s = new GpuDesiredState();
        Assert.False(s.Requested);
        Assert.Null(s.FrameCapFps);
    }

    [Fact]
    public void Disabling_the_cap_is_an_intent_not_an_absence()
    {
        // null FrameCapFps with Requested true means "turn it off", which is a different thing from
        // "nobody has said anything". Collapsing them would make the off switch unreachable.
        var s = new GpuDesiredState();
        s.RequestFrameCap(null, Now);
        Assert.True(s.Requested);
        Assert.Null(s.FrameCapFps);
    }

    [Fact]
    public void A_cap_below_the_drivers_range_is_rejected_with_the_actual_limit()
    {
        // 15..1000 is what this device's driver reported on 2026-08-30. The message quotes the real
        // limit, so the user learns what WOULD work instead of just being refused.
        var why = GpuDesiredState.Reject(5, 15, 1000);
        Assert.NotNull(why);
        Assert.Contains("15", why);
    }

    [Fact]
    public void A_cap_above_the_drivers_range_is_rejected_with_the_actual_limit()
        => Assert.Contains("1000", GpuDesiredState.Reject(2000, 15, 1000)!);

    [Fact]
    public void A_cap_inside_the_range_is_accepted()
        => Assert.Null(GpuDesiredState.Reject(45, 15, 1000));

    [Fact]
    public void Disabling_is_always_legal_even_outside_any_range()
        => Assert.Null(GpuDesiredState.Reject(null, 15, 1000));

    [Fact]
    public void Zero_and_negative_are_rejected_as_not_being_frame_rates()
    {
        Assert.NotNull(GpuDesiredState.Reject(0, 15, 1000));
        Assert.NotNull(GpuDesiredState.Reject(-30, 15, 1000));
    }

    [Fact]
    public void Without_a_reported_range_only_implausible_values_are_refused()
    {
        // A limit we did not read is not a limit we can enforce, so an unknown range must not become
        // an invented one. Only what cannot be a frame rate at all is refused.
        Assert.Null(GpuDesiredState.Reject(45, null, null));
        Assert.Null(GpuDesiredState.Reject(240, null, null));
        Assert.NotNull(GpuDesiredState.Reject(50_000, null, null));
    }
}
