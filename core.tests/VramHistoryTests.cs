// GPD Forge — tests for the VRAM/UMA change-detection history. GPL-3.0-or-later.
//
// The point of these tests is the honesty boundary, not the plumbing: a diff only counts as a
// confirmed BIOS change when a reboot can be PROVEN, an unavailable read must never overwrite the
// baseline, and a reading pinned at WMI's 32-bit ceiling must say so even when it looks unchanged.
// Zero P/Invoke and zero WMI here — the boot clock and the store are both fakes.
//
// Several tests here exist because the verdict text once claimed things the code had not measured.
// They assert on ABSENCE as much as presence — no "CONFIRMED" across a ceiling reading, no "did NOT
// take effect" when the ceiling hides success, no "no reboot since" without a stored boot instant,
// no "will be detected" when the write threw. A regression that restores any of those phrases fails
// here, which is the whole point: this feature's job is confirmation, so a false confirmation is
// worse than no feature at all.
using System;
using System.IO;
using GpdForge.Ai;
using Xunit;

namespace GpdForge.Core.Tests;

internal sealed class FakeVramHistoryStore : IVramHistoryStore
{
    public VramObservation? Row;
    public int Writes;
    public Exception? ReadThrows;
    public Exception? WriteThrows;

    public VramObservation? Read() => ReadThrows is null ? Row : throw ReadThrows;

    public void Write(VramObservation observation)
    {
        if (WriteThrows is not null) throw WriteThrows;
        Row = observation;
        Writes++;
    }
}

internal sealed class FakeVramBootClock(DateTimeOffset now, DateTimeOffset? boot) : IBootClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
    public DateTimeOffset? BootUtc { get; set; } = boot;
}

public class VramHistoryTests
{
    private static readonly DateTimeOffset Boot1 = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Boot2 = new(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T0 = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);

    /// <summary>2 GiB: deliberately below the AdapterRAM ceiling so the caveat does not pollute the
    /// verdict strings under test.</summary>
    private static VramInfo Live(double mb, string? name = "AMD Radeon(TM) 890M Graphics") =>
        VramAdvisor.FromAdapterRam((long)(mb * 1024 * 1024), name);

    private static (VramHistory history, FakeVramHistoryStore store, FakeVramBootClock clock) Build(
        VramObservation? seed = null, DateTimeOffset? now = null, DateTimeOffset? boot = null)
    {
        var store = new FakeVramHistoryStore { Row = seed };
        var clock = new FakeVramBootClock(now ?? T0, boot ?? Boot1);
        return (new VramHistory(store, clock), store, clock);
    }

    [Fact]
    public void First_observation_writes_a_baseline_and_claims_nothing_else()
    {
        var (history, store, _) = Build();

        var report = history.Observe(Live(2048));

        Assert.Equal(VramChangeKind.FirstObservation, report.Kind);
        Assert.False(report.RebootConfirmed);
        Assert.Null(report.PreviousMb);
        Assert.Equal(1, store.Writes);
        Assert.Equal(2048, store.Row!.ReportedMb);
        Assert.Equal(Boot1, store.Row.BootUtc);
        Assert.DoesNotContain("CONFIRMED", report.Summary);
    }

    [Fact]
    public void Same_value_same_boot_is_unchanged_and_keeps_the_original_since_timestamp()
    {
        var seed = new VramObservation(2048, "AMD Radeon(TM) 890M Graphics", T0, T0, Boot1);
        var (history, store, _) = Build(seed, now: T0.AddMinutes(5));

        var report = history.Observe(Live(2048));

        Assert.Equal(VramChangeKind.Unchanged, report.Kind);
        Assert.Equal(T0, report.SinceUtc);
        Assert.Contains("Unchanged", report.Summary);
        // Throttled: nothing moved and the last-seen stamp is fresh, so no write to the system drive.
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void An_unchanged_reading_is_refreshed_once_the_last_seen_stamp_goes_stale()
    {
        var seed = new VramObservation(2048, "AMD Radeon(TM) 890M Graphics", T0, T0, Boot1);
        var (history, store, _) = Build(seed, now: T0 + VramHistory.LastSeenRefresh + TimeSpan.FromMinutes(1));

        var report = history.Observe(Live(2048));

        Assert.Equal(VramChangeKind.Unchanged, report.Kind);
        Assert.Equal(1, store.Writes);
        Assert.Equal(T0, store.Row!.FirstSeenUtc);
        Assert.True(store.Row.LastSeenUtc > T0);
    }

    [Fact]
    public void A_different_value_across_a_reboot_is_a_confirmed_bios_change()
    {
        // Both endpoints are below the AdapterRAM ceiling, which is the ONLY case where the numbers
        // can be subtracted for meaning. 8192 MB is not usable here: it cannot come out of a uint32.
        var seed = new VramObservation(2048, "AMD", T0, T0, Boot1);
        var (history, store, _) = Build(seed, now: T1, boot: Boot2);

        var report = history.Observe(Live(3072));

        Assert.Equal(VramChangeKind.ChangedAcrossReboot, report.Kind);
        Assert.True(report.RebootConfirmed);
        Assert.Equal(2048, report.PreviousMb);
        Assert.Contains("2048 MB", report.Summary);
        Assert.Contains("3072 MB", report.Summary);
        Assert.Contains("CONFIRMED", report.Summary);
        Assert.Equal(2048, store.Row!.PreviousMb);
        Assert.Equal(Boot1, store.Row.PreviousBootUtc);
    }

    [Fact]
    public void The_same_value_across_a_reboot_says_the_bios_edit_did_not_take()
    {
        // 2048 MB, comfortably below the ceiling: here "unchanged" really is a measurement, so the
        // failure claim is earned. (At the ceiling it is not — see the ceiling test below.)
        var seed = new VramObservation(2048, "AMD", T0, T0, Boot1);
        var (history, _, _) = Build(seed, now: T1, boot: Boot2);

        var report = history.Observe(Live(2048));

        Assert.Equal(VramChangeKind.UnchangedAcrossReboot, report.Kind);
        Assert.True(report.RebootConfirmed);
        Assert.Contains("did NOT take effect", report.Summary);
        Assert.Equal(T0, report.SinceUtc);
    }

    [Fact]
    public void A_ceiling_reading_after_a_reboot_refuses_to_say_the_bios_edit_failed()
    {
        // The exact 4 GB -> 8 GB case: both readings saturate at 4095 MB, so a SUCCESSFUL edit is
        // indistinguishable from a failed one. Telling the user it failed sends them back into
        // firmware to undo work that actually landed.
        var seed = new VramObservation(4095, "AMD", T0, T0, Boot1);
        var (history, _, _) = Build(seed, now: T1, boot: Boot2);

        var report = history.Observe(VramAdvisor.FromAdapterRam(VramAdvisor.ReportingCeilingBytes, "AMD"));

        Assert.Equal(VramChangeKind.UnchangedAcrossReboot, report.Kind);
        Assert.DoesNotContain("did NOT take effect", report.Summary);
        Assert.Contains("CANNOT be determined", report.Summary);
    }

    [Fact]
    public void A_delta_with_a_ceiling_endpoint_is_never_reported_as_a_confirmed_change()
    {
        // A 4 GB -> 6 GB edit: the old reading saturated at 4095, the new one WRAPPED to 2048. The
        // arithmetic says "dropped by 2 GB"; the truth is the field cannot represent either split.
        var seed = new VramObservation(4095, "AMD", T0, T0, Boot1);
        var (history, _, _) = Build(seed, now: T1, boot: Boot2);

        var report = history.Observe(Live(2048));

        Assert.Equal(VramChangeKind.ChangedAcrossReboot, report.Kind);
        Assert.DoesNotContain("CONFIRMED", report.Summary);
        Assert.DoesNotContain("is applied and read back", report.Summary);
        Assert.Contains("NOT a measurement", report.Summary);
        Assert.Contains("neither a confirmed increase nor a confirmed decrease", report.Summary);
    }

    [Fact]
    public void A_delta_that_lands_on_the_ceiling_is_also_not_a_confirmed_change()
    {
        // Symmetric to the case above: the CURRENT reading is the pinned one, so "changed from
        // 2048 MB to 4095 MB" is not a measured 2 GB increase either.
        var seed = new VramObservation(2048, "AMD", T0, T0, Boot1);
        var (history, _, _) = Build(seed, now: T1, boot: Boot2);

        var report = history.Observe(VramAdvisor.FromAdapterRam(VramAdvisor.ReportingCeilingBytes, "AMD"));

        Assert.Equal(VramChangeKind.ChangedAcrossReboot, report.Kind);
        Assert.DoesNotContain("CONFIRMED", report.Summary);
        Assert.Contains("32-bit AdapterRAM ceiling", report.Summary);
    }

    [Fact]
    public void A_change_within_one_boot_is_not_sold_as_a_bios_edit()
    {
        var seed = new VramObservation(2048, "AMD", T0, T0, Boot1);
        var (history, _, _) = Build(seed, now: T0.AddMinutes(30), boot: Boot1);

        var report = history.Observe(Live(512));

        Assert.Equal(VramChangeKind.ChangedSameBoot, report.Kind);
        Assert.False(report.RebootConfirmed);
        Assert.Contains("not a confirmed BIOS edit", report.Summary);
    }

    [Fact]
    public void A_change_with_no_boot_time_refuses_to_attribute_it_to_a_reboot()
    {
        var seed = new VramObservation(2048, "AMD", T0, T0, null);
        var (history, _, _) = Build(seed, now: T1, boot: null);

        var report = history.Observe(Live(3072));

        Assert.Equal(VramChangeKind.ChangedRebootUnknown, report.Kind);
        Assert.False(report.RebootConfirmed);
        Assert.Contains("could not be", report.Summary);
        Assert.DoesNotContain("CONFIRMED", report.Summary);
    }

    [Fact]
    public void A_boot_time_that_moved_less_than_the_tolerance_is_the_same_boot()
    {
        var seed = new VramObservation(2048, "AMD", T0, T0, Boot1);
        var (history, _, _) = Build(seed, now: T0.AddHours(1), boot: Boot1.AddSeconds(30));

        var report = history.Observe(Live(1024));

        Assert.Equal(VramChangeKind.ChangedSameBoot, report.Kind);
        Assert.False(report.RebootConfirmed);
    }

    [Fact]
    public void An_unavailable_reading_never_overwrites_the_baseline()
    {
        var seed = new VramObservation(2048, "AMD", T0, T0, Boot1);
        var (history, store, _) = Build(seed, now: T1, boot: Boot2);

        var report = history.Observe(VramAdvisor.FromAdapterRam(null, null));

        Assert.Equal(VramChangeKind.NotObserved, report.Kind);
        Assert.Equal(0, store.Writes);
        Assert.Equal(2048, store.Row!.ReportedMb);
        Assert.Null(report.PreviousMb);
    }

    [Fact]
    public void An_unreadable_store_degrades_to_no_verdict_instead_of_throwing()
    {
        var store = new FakeVramHistoryStore { ReadThrows = new IOException("locked") };
        var history = new VramHistory(store, new FakeVramBootClock(T1, Boot2));

        var report = history.Observe(Live(2048));

        Assert.Equal(VramChangeKind.NotObserved, report.Kind);
        Assert.Equal(0, store.Writes);
        Assert.Contains("could not be read", report.Summary);
    }

    [Fact]
    public void A_reading_at_the_adapter_ram_ceiling_carries_the_caveat_into_the_verdict()
    {
        var seed = new VramObservation(4095, "AMD", T0, T0, Boot1);
        var (history, _, _) = Build(seed, now: T1, boot: Boot2);

        var live = VramAdvisor.FromAdapterRam(VramAdvisor.ReportingCeilingBytes, "AMD Radeon(TM) 890M Graphics");
        Assert.True(live.AtReportingCeiling);

        var report = history.Observe(live);

        Assert.Equal(VramChangeKind.UnchangedAcrossReboot, report.Kind);
        Assert.Contains("32-bit ceiling", report.Summary);
        Assert.Contains("Task Manager", report.Summary);
    }

    [Fact]
    public void A_reading_below_the_ceiling_is_still_not_presented_as_a_proven_split()
    {
        // It is not at the ceiling — but AdapterRAM is uint32, so a 6 GiB split arrives here as
        // 2048 MB. Declaring sub-4-GiB readings exact is what allowed an INCREASE to be read back as
        // a decrease, so every available reading carries the field-width caveat.
        var v = VramAdvisor.FromAdapterRam(2048L * 1024 * 1024, "AMD Radeon(TM) 890M Graphics");
        Assert.False(v.AtReportingCeiling);
        Assert.DoesNotContain("32-bit ceiling", v.Advisory);   // the pinned-reading caveat, correctly absent
        Assert.Contains("32-bit field", v.Advisory);
        Assert.Contains("wraps around", v.Advisory);
        Assert.Contains("not as a proven split size", v.Advisory);
    }

    [Fact]
    public void Unchanged_with_no_stored_boot_time_claims_neither_a_reboot_nor_the_absence_of_one()
    {
        // WMI was down when the baseline was written, so nothing can be said about reboots since.
        var seed = new VramObservation(2048, "AMD", T0, T0, null);
        var (history, store, _) = Build(seed, now: T1, boot: Boot2);

        var report = history.Observe(Live(2048));

        Assert.Equal(VramChangeKind.Unchanged, report.Kind);
        Assert.False(report.RebootConfirmed);
        Assert.DoesNotContain("no reboot since", report.Summary);
        Assert.Contains("could not be established", report.Summary);
        // And the current boot must NOT be stamped onto a row that was never seen under it: doing so
        // would let the NEXT call assert "no reboot since that observation" on invented evidence.
        Assert.Null(store.Row!.BootUtc);
    }

    [Fact]
    public void A_failed_write_reports_the_failure_instead_of_promising_a_detection()
    {
        // %ProgramData%\GPD Forge\ not writable by the service account: nothing was recorded, so
        // there is nothing to detect a later change against.
        var store = new FakeVramHistoryStore { WriteThrows = new UnauthorizedAccessException("Access to the path is denied.") };
        var history = new VramHistory(store, new FakeVramBootClock(T0, Boot1));

        var report = history.Observe(Live(2048));

        Assert.Equal(VramChangeKind.FirstObservation, report.Kind);
        Assert.DoesNotContain("Baseline recorded", report.Summary);
        Assert.DoesNotContain("will be detected", report.Summary);
        Assert.Contains("could NOT be recorded", report.Summary);
        Assert.Contains("Access to the path is denied.", report.Summary);
        Assert.Null(store.Row);
    }

    [Fact]
    public void A_backwards_moving_boot_instant_is_not_sold_as_the_same_boot()
    {
        // NTP corrected the RTC downwards after the reboot, so the new boot instant is EARLIER than
        // the stored one. That is not a proven reboot — but it is certainly not proof of one boot,
        // and calling it same-boot dismissed a real BIOS change as a driver reporting quirk.
        var seed = new VramObservation(2048, "AMD", T0, T0, Boot2);
        var (history, _, _) = Build(seed, now: T1, boot: Boot2.AddHours(-3));

        var report = history.Observe(Live(3072));

        Assert.Equal(VramChangeKind.ChangedRebootUnknown, report.Kind);
        Assert.DoesNotContain("SAME boot", report.Summary);
        Assert.False(VramHistory.IsSameBoot(Boot2, Boot2.AddHours(-3)));
        Assert.False(VramHistory.IsReboot(Boot2, Boot2.AddHours(-3)));
    }

    [Fact]
    public void A_sub_megabyte_controller_reading_is_unavailable_rather_than_a_zero_baseline()
    {
        // 256 KiB is not a UMA split. Reported as "0 MB, available" it produced a row the store
        // wrote and then refused to read back — a File.Replace on every single request, forever.
        var v = VramAdvisor.FromAdapterRam(256L * 1024, "AMD");
        Assert.False(v.Available);

        var (history, store, _) = Build();
        var report = history.Observe(v);

        Assert.Equal(VramChangeKind.NotObserved, report.Kind);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void The_file_store_refuses_to_write_exactly_what_it_refuses_to_read()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gpdforge-vram-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileVramHistoryStore(dir);
            var unusable = new VramObservation(0, "AMD", T0, T0, Boot1);

            Assert.False(VramHistory.IsUsableBaseline(unusable));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Write(unusable));
            // The point of the assertion: no file was produced, so there is no write/read loop.
            Assert.Empty(Directory.GetFiles(dir));
            Assert.Null(store.Read());
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void The_named_bios_path_is_carried_with_its_caveat_not_as_bare_instruction()
    {
        var advisory = VramAdvisor.FromAdapterRam(2048L * 1024 * 1024, "AMD").Advisory;
        Assert.Contains("UMA Frame buffer Size", advisory);
        Assert.Contains("UMA_SPECIFIED", advisory);
        // Never present firmware navigation as certain: the version caveat must travel with the path.
        Assert.Contains("do not guess", advisory);
    }

    [Fact]
    public void Classify_and_IsReboot_are_pure_and_agree_with_Observe()
    {
        Assert.Equal(VramChangeKind.FirstObservation, VramHistory.Classify(null, 2048, Boot1));
        Assert.False(VramHistory.IsReboot(null, Boot2));
        Assert.False(VramHistory.IsReboot(Boot1, null));
        Assert.True(VramHistory.IsReboot(Boot1, Boot2));
        // Same-boot is positive evidence, not "not a reboot": unknown on either side is unknown.
        Assert.False(VramHistory.IsSameBoot(null, Boot1));
        Assert.False(VramHistory.IsSameBoot(Boot1, null));
        Assert.False(VramHistory.IsSameBoot(Boot1, Boot2));
        Assert.True(VramHistory.IsSameBoot(Boot1, Boot1.AddSeconds(-30)));
        Assert.True(VramHistory.SameSize(2048.4, 2048));
        Assert.False(VramHistory.SameSize(2048, 4096));
    }

    [Fact]
    public void The_file_store_round_trips_and_survives_a_corrupt_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gpdforge-vram-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileVramHistoryStore(dir);
            Assert.Null(store.Read());

            store.Write(new VramObservation(4095, "AMD Radeon(TM) 890M Graphics", T0, T1, Boot1, 2048, T0, Boot1));
            var row = store.Read();
            Assert.NotNull(row);
            Assert.Equal(4095, row!.ReportedMb);
            Assert.Equal(2048, row.PreviousMb);
            Assert.Equal(Boot1, row.BootUtc);

            File.WriteAllText(Path.Combine(dir, "vram-history.json"), "{ this is not json");
            Assert.Null(store.Read());                       // quarantined, not fatal
            Assert.NotEmpty(Directory.GetFiles(dir, "vram-history.json.corrupt-*"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
