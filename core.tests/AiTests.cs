// GPD Forge — Agents/AI mode tests: anti-standby ref-count, sustained profile shaping, VRAM
// advisory, and the job-flow wiring. GPL-3.0-or-later.
using GpdForge.Ai;
using GpdForge.Api;
using Xunit;

namespace GpdForge.Core.Tests;

public class AntiStandbyServiceTests
{
    private sealed class FakeSink : IExecutionStateSink
    {
        public int Engaged { get; private set; }
        public int Released { get; private set; }
        public void Engage() => Engaged++;
        public void Release() => Released++;
    }

    [Fact]
    public void Starts_disengaged_with_zero_holders()
    {
        var svc = new AntiStandbyService(new FakeSink());
        Assert.Equal(0, svc.HolderCount);
        Assert.False(svc.Active);
    }

    [Fact]
    public void First_start_engages_the_sink_0_to_1()
    {
        var sink = new FakeSink();
        var svc = new AntiStandbyService(sink);

        int n = svc.Start();

        Assert.Equal(1, n);
        Assert.Equal(1, svc.HolderCount);
        Assert.True(svc.Active);
        Assert.Equal(1, sink.Engaged);
        Assert.Equal(0, sink.Released);
    }

    [Fact]
    public void Concurrent_holds_only_engage_once()
    {
        var sink = new FakeSink();
        var svc = new AntiStandbyService(sink);

        svc.Start(); svc.Start(); svc.Start();

        Assert.Equal(3, svc.HolderCount);
        Assert.Equal(1, sink.Engaged);   // still just the one 0->1 transition
    }

    [Fact]
    public void Last_stop_releases_the_sink_1_to_0()
    {
        var sink = new FakeSink();
        var svc = new AntiStandbyService(sink);
        svc.Start(); svc.Start();

        svc.Stop();
        Assert.Equal(1, svc.HolderCount);
        Assert.Equal(0, sink.Released);   // one holder remains -> not released yet

        svc.Stop();
        Assert.Equal(0, svc.HolderCount);
        Assert.False(svc.Active);
        Assert.Equal(1, sink.Released);   // released exactly once, on the last release
    }

    [Fact]
    public void Stop_below_zero_is_a_harmless_noop()
    {
        var sink = new FakeSink();
        var svc = new AntiStandbyService(sink);

        int n = svc.Stop();

        Assert.Equal(0, n);
        Assert.Equal(0, sink.Released);
        Assert.Equal(0, sink.Engaged);
    }

    [Fact]
    public void Reengages_after_dropping_to_zero_and_starting_again()
    {
        var sink = new FakeSink();
        var svc = new AntiStandbyService(sink);
        svc.Start();
        svc.Stop();

        svc.Start();

        Assert.Equal(2, sink.Engaged);
        Assert.Equal(1, sink.Released);
    }
}

public class ProfileShaperTests
{
    [Fact]
    public void Produces_a_flat_profile_with_no_boost_above_stapm()
    {
        var p = ProfileShaper.Shape(20, 90);
        Assert.Equal(20, p.StapmW);
        Assert.Equal(20, p.FastW);
        Assert.Equal(20, p.SlowW);
        Assert.Equal(90, p.TctlC);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1000)]
    public void Clamps_watts_into_the_safe_band(int targetW)
    {
        var p = ProfileShaper.Shape(targetW, 90);
        Assert.InRange(p.StapmW, ProfileShaper.MinW, ProfileShaper.MaxW);
        Assert.Equal(p.StapmW, p.FastW);
        Assert.Equal(p.StapmW, p.SlowW);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    public void Clamps_tctl_into_the_safe_band(int tctlC)
    {
        var p = ProfileShaper.Shape(20, tctlC);
        Assert.InRange(p.TctlC, ProfileShaper.MinTctlC, ProfileShaper.MaxTctlC);
    }

    [Fact]
    public void Is_always_flat_regardless_of_input()
    {
        for (int w = -10; w <= 60; w += 7)
        {
            var p = ProfileShaper.Shape(w, 90);
            Assert.Equal(p.StapmW, p.FastW);
            Assert.Equal(p.StapmW, p.SlowW);
        }
    }
}

public class VramAdvisorTests
{
    [Fact]
    public void Reports_unavailable_when_there_is_nothing_to_read()
    {
        var v = VramAdvisor.FromAdapterRam(null, null);
        Assert.False(v.Available);
        Assert.Equal(0, v.ReportedBytes);
        Assert.Contains("BIOS", v.Advisory);
    }

    [Fact]
    public void Reports_unavailable_for_a_non_positive_reading()
    {
        Assert.False(VramAdvisor.FromAdapterRam(0, "AMD Radeon 890M").Available);
        Assert.False(VramAdvisor.FromAdapterRam(-1, "AMD Radeon 890M").Available);
    }

    [Fact]
    public void Converts_bytes_to_megabytes_and_keeps_the_adapter_name()
    {
        var v = VramAdvisor.FromAdapterRam(4L * 1024 * 1024 * 1024, "AMD Radeon 890M");
        Assert.True(v.Available);
        Assert.Equal(4096, v.ReportedMb);
        Assert.Equal("AMD Radeon 890M", v.AdapterName);
    }

    [Fact]
    public void Advisory_always_mentions_bios_and_reboot_when_available()
    {
        var v = VramAdvisor.FromAdapterRam(1024L * 1024 * 1024, "AMD Radeon 890M");
        Assert.Contains("BIOS", v.Advisory);
        Assert.Contains("reboot", v.Advisory, StringComparison.OrdinalIgnoreCase);
    }

    // --- the bit-reinterpretation helper on the real (WMI-backed) reader ---

    [Fact]
    public void ReadAdapterRamBytes_passes_through_a_boxed_uint()
    {
        object boxed = 2147483648u; // 2 GiB, top bit set
        Assert.Equal(2147483648L, WmiVramReader.ReadAdapterRamBytes(boxed));
    }

    [Fact]
    public void ReadAdapterRamBytes_reinterprets_a_negative_boxed_int_as_unsigned()
    {
        // -1073741824 as Int32 == 0xC0000000 == 3 GiB reinterpreted as uint32.
        object boxed = -1073741824;
        Assert.Equal(3221225472L, WmiVramReader.ReadAdapterRamBytes(boxed));
    }

    [Fact]
    public void ReadAdapterRamBytes_passes_through_a_small_positive_int()
    {
        object boxed = 536870912; // 512 MiB, fits in a positive int32
        Assert.Equal(536870912L, WmiVramReader.ReadAdapterRamBytes(boxed));
    }

    [Fact]
    public void ReadAdapterRamBytes_is_null_for_null_or_unrecognized_types()
    {
        Assert.Null(WmiVramReader.ReadAdapterRamBytes(null));
        Assert.Null(WmiVramReader.ReadAdapterRamBytes("not a number"));
    }
}

/// <summary>Wires anti-standby into the job queue: a running job holds the keep-awake lock; a
/// blocked job does not (nothing to keep the machine awake for); finishing a job releases its
/// hold.</summary>
public class JobsStateAntiStandbyTests
{
    private sealed class FakeSink : IExecutionStateSink
    {
        public int Engaged { get; private set; }
        public int Released { get; private set; }
        public void Engage() => Engaged++;
        public void Release() => Released++;
    }

    [Fact]
    public void Adding_a_running_job_engages_anti_standby()
    {
        var sink = new FakeSink();
        var anti = new AntiStandbyService(sink);
        var jobs = new JobsState(anti);

        jobs.Add("infer batch", null, "running");

        Assert.Equal(1, anti.HolderCount);
        Assert.Equal(1, sink.Engaged);
    }

    [Fact]
    public void Adding_a_blocked_job_does_not_engage_anti_standby()
    {
        var sink = new FakeSink();
        var anti = new AntiStandbyService(sink);
        var jobs = new JobsState(anti);

        jobs.Add("infer batch", null, "blocked");

        Assert.Equal(0, anti.HolderCount);
        Assert.Equal(0, sink.Engaged);
    }

    [Fact]
    public void Two_running_jobs_ref_count_independently()
    {
        var sink = new FakeSink();
        var anti = new AntiStandbyService(sink);
        var jobs = new JobsState(anti);

        var a = jobs.Add("job a", null, "running");
        var b = jobs.Add("job b", null, "running");

        Assert.Equal(2, anti.HolderCount);
        Assert.Equal(1, sink.Engaged);   // only the 0->1 edge engages the sink

        jobs.Finish(a.Id);
        Assert.Equal(1, anti.HolderCount);
        Assert.Equal(0, sink.Released);  // b still running -> not released yet

        jobs.Finish(b.Id);
        Assert.Equal(0, anti.HolderCount);
        Assert.Equal(1, sink.Released);
    }

    [Fact]
    public void Finishing_a_blocked_job_does_not_touch_anti_standby()
    {
        var sink = new FakeSink();
        var anti = new AntiStandbyService(sink);
        var jobs = new JobsState(anti);

        var job = jobs.Add("infer batch", null, "blocked");
        jobs.Finish(job.Id);

        Assert.Equal(0, sink.Engaged);
        Assert.Equal(0, sink.Released);
    }

    [Fact]
    public void Finish_updates_the_job_status_to_done()
    {
        var anti = new AntiStandbyService(new FakeSink());
        var jobs = new JobsState(anti);

        var job = jobs.Add("infer batch", null, "running");
        jobs.Finish(job.Id);

        Assert.Equal("done", jobs.All.Single(j => j.Id == job.Id).Status);
    }

    [Fact]
    public void Finish_of_an_unknown_id_returns_false()
    {
        var jobs = new JobsState(new AntiStandbyService(new FakeSink()));
        Assert.False(jobs.Finish("job-999"));
    }
}
