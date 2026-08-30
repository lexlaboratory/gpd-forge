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

public class GpuProfileApplierTests
{
    private sealed class FakeMemory : ISystemMemoryProbe { public uint TotalRamMb() => 28280; }

    private static GpuProfileApplier WithGateClosed()
        => new(new GpuProfileService(new FakeMemory(), null, _ => null), () => null);

    [Fact]
    public void A_mode_without_a_gpu_profile_is_skipped_with_a_reason()
    {
        var outcome = WithGateClosed().ApplyForMode("standby");
        Assert.False(outcome.Attempted);
        Assert.Contains("no GPU profile", outcome.Reason);
    }

    [Fact]
    public void With_the_gate_closed_nothing_is_attempted_and_the_reason_says_so()
    {
        var outcome = WithGateClosed().ApplyForMode("gaming");
        Assert.False(outcome.Attempted);
        Assert.Empty(outcome.Applied);
        Assert.Contains("unavailable", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
