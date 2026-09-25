// GPD Forge — the single telemetry sampler. GPL-3.0-or-later.
//
// Until 2026-09-24 six callers each ran a full hardware read (~100–140 ms of WMI + LHM + EC) on their
// own timers: the worker, GET /telemetry for the UI and again for the overlay, FocusProfileWorker
// every 1.5 s for one boolean, POST /jobs, GET /health/check and StandbyService. These pin the
// replacement: one loop reads, everyone else reads the last published sample.
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class TelemetrySamplerTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public void Before_the_first_sample_the_reading_is_honestly_unmeasured()
    {
        var sampler = new TelemetrySampler(new CountingTelemetryReader(), time: new ManualTimeProvider());

        var r = sampler.Latest;

        Assert.Equal(0, r.Sequence);
        Assert.False(r.IsSampled);
        Assert.Null(r.SampledAt);
        Assert.Null(r.AgeMs(DateTimeOffset.UtcNow));
        Assert.Equal(TelemetrySnapshot.Unmeasured, r.Snapshot);
    }

    [Fact]
    public async Task A_sample_is_published_with_its_timestamp_and_a_rising_sequence()
    {
        var time = new ManualTimeProvider();
        var reader = new CountingTelemetryReader();
        var sampler = new TelemetrySampler(reader, time: time);

        await sampler.SampleOnceAsync(None);
        var first = sampler.Latest;
        time.Advance(TimeSpan.FromSeconds(1));
        await sampler.SampleOnceAsync(None);
        var second = sampler.Latest;

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(time.GetUtcNow(), second.SampledAt);
        Assert.Equal(reader.Next, second.Snapshot);
        Assert.Equal(250, second.AgeMs(time.GetUtcNow().AddMilliseconds(250)));
    }

    [Fact]
    public async Task Reading_the_cache_never_touches_the_hardware()
    {
        var reader = new CountingTelemetryReader();
        var sampler = new TelemetrySampler(reader, time: new ManualTimeProvider());
        await sampler.SampleOnceAsync(None);

        for (int i = 0; i < 1000; i++)
        {
            _ = sampler.Latest;
            _ = await sampler.ReadAsync(None);
        }

        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_last_good_sample_and_lets_it_age()
    {
        var time = new ManualTimeProvider();
        var reader = new CountingTelemetryReader();
        var sampler = new TelemetrySampler(reader, time: time);
        await sampler.SampleOnceAsync(None);
        var good = sampler.Latest;

        reader.Throw = new InvalidOperationException("WMI provider hung up");
        time.Advance(TimeSpan.FromSeconds(3));
        await sampler.SampleOnceAsync(None);   // must not throw

        Assert.Equal(good, sampler.Latest);
        Assert.Equal(3000, sampler.Latest.AgeMs(time.GetUtcNow()));
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var reader = new CountingTelemetryReader { Throw = new OperationCanceledException() };
        var sampler = new TelemetrySampler(reader, time: new ManualTimeProvider());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sampler.SampleOnceAsync(None));
    }

    [Fact]
    public async Task A_waiter_is_released_by_the_next_sample()
    {
        var sampler = new TelemetrySampler(new CountingTelemetryReader(), time: new ManualTimeProvider());
        await sampler.SampleOnceAsync(None);

        var wait = sampler.WaitForNewerAsync(1, TimeSpan.FromSeconds(30), None);
        Assert.False(wait.IsCompleted);

        await sampler.SampleOnceAsync(None);
        var r = await wait.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, r.Sequence);
    }

    [Fact]
    public async Task A_wait_that_times_out_returns_the_latest_reading_instead_of_throwing()
    {
        var sampler = new TelemetrySampler(new CountingTelemetryReader(), time: new ManualTimeProvider());
        await sampler.SampleOnceAsync(None);

        var r = await sampler.WaitForNewerAsync(1, TimeSpan.FromMilliseconds(50), None);

        Assert.Equal(1, r.Sequence);
    }

    [Fact]
    public async Task A_sampler_that_never_produced_a_reading_times_out_to_the_unmeasured_one()
    {
        // The startup case where the very first hardware read hangs: callers get honest nulls, not a
        // request that never answers and not an exception.
        var sampler = new TelemetrySampler(new CountingTelemetryReader(), time: new ManualTimeProvider());

        var r = await sampler.WaitForNewerAsync(0, TimeSpan.FromMilliseconds(50), None);

        Assert.Same(TelemetryReading.Unsampled, r);
    }

    [Fact]
    public async Task A_wait_honours_cancellation()
    {
        var sampler = new TelemetrySampler(new CountingTelemetryReader(), time: new ManualTimeProvider());
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sampler.WaitForNewerAsync(0, TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task ReadAsync_waits_for_the_first_sample_rather_than_serving_an_empty_one()
    {
        var sampler = new TelemetrySampler(new CountingTelemetryReader(), time: new ManualTimeProvider());

        var read = sampler.ReadAsync(None);
        Assert.False(read.IsCompleted);
        await sampler.SampleOnceAsync(None);

        Assert.True((await read.WaitAsync(TimeSpan.FromSeconds(5))).IsSampled);
    }

    [Fact]
    public async Task The_background_loop_samples_at_about_one_hertz()
    {
        var reader = new CountingTelemetryReader();
        using var sampler = new TelemetrySampler(reader);
        await sampler.StartAsync(None);
        try
        {
            var first = await sampler.WaitForNewerAsync(0, TimeSpan.FromSeconds(5), None);
            Assert.True(first.IsSampled, "the loop must publish its first sample immediately");

            var second = await sampler.WaitForNewerAsync(first.Sequence, TimeSpan.FromSeconds(5), None);
            var gap = second.SampledAt!.Value - first.SampledAt!.Value;
            Assert.InRange(gap.TotalMilliseconds, 700, 2500);
        }
        finally { await sampler.StopAsync(None); }
    }

    // ---------------------------------------------------------------------------------------------
    // Nobody but the sampler may hold the hardware reader. Checked structurally, because the failure
    // this prevents — a new endpoint quietly doing its own 140 ms read — passes every behavioural test.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Only_the_sampler_takes_the_hardware_reader_as_a_dependency()
    {
        var offenders = typeof(TelemetrySampler).Assembly.GetTypes()
            .Where(t => t != typeof(TelemetrySampler))
            .SelectMany(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(c => c.GetParameters().Any(p => p.ParameterType == typeof(ITelemetryService)))
                .Select(_ => t.FullName))
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0,
            "These types read hardware telemetry directly instead of the sampler's cache: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_worker_and_every_telemetry_consumer_depend_on_the_sampled_source()
    {
        foreach (var type in new[] { typeof(ForgeWorker), typeof(GpdForge.Fan.FanWorker), typeof(GpdForge.Profiles.FocusProfileWorker), typeof(GpdForge.Standby.StandbyService) })
        {
            Assert.Contains(type.GetConstructors().SelectMany(c => c.GetParameters()),
                p => p.ParameterType == typeof(ITelemetrySource));
        }
    }

    // A handler's whole parameter list, however many lines it spans: `app.MapGet("/x", async (` then
    // everything up to the closing parenthesis. The first version of this guard read only up to the
    // first newline, so a handler with its parameters on the next line passed unseen (audit, 2026-09-24).
    private static readonly Regex HandlerParameters = new(
        @"app\.Map(?:Get|Post|Put|Delete|Patch)\(\s*""[^""]*""\s*,\s*(?:async\s*)?\((?<params>[^)]*)\)",
        RegexOptions.Compiled);

    [Fact]
    public void The_handler_parser_sees_parameters_on_later_lines()
    {
        const string multiLine = "app.MapGet(\"/x\", async (\n    ITelemetryService t,\n    CancellationToken ct) =>\n{ });";
        var m = HandlerParameters.Match(multiLine);
        Assert.True(m.Success);
        Assert.Contains("ITelemetryService", m.Groups["params"].Value);
    }

    [Fact]
    public void No_HTTP_handler_takes_the_hardware_reader()
    {
        var source = File.ReadAllText(ProgramRoutes.FindProgramCs());
        var handlers = HandlerParameters.Matches(source);
        Assert.True(handlers.Count > 50, "route parse looks broken; this guard would pass vacuously");

        var offenders = handlers.Where(m => m.Groups["params"].Value.Contains("ITelemetryService"))
                                .Select(m => m.Value.Trim()).ToList();
        Assert.True(offenders.Count == 0, "Handlers reading hardware directly:\n" + string.Join("\n", offenders));
    }

    // ---------------------------------------------------------------------------------------------
    // The wire: the snapshot's fields untouched, plus when it was measured.
    // ---------------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void The_wire_form_keeps_every_snapshot_field_and_adds_the_sample_age()
    {
        var at = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var snap = TelemetrySnapshot.Unmeasured with { CpuTempC = 61.5, AcConnected = true };

        var json = TelemetryWire.ToJson(new TelemetryReading(snap, at, 7), at.AddMilliseconds(420), Web);

        var plain = JsonSerializer.SerializeToNode(snap, Web)!.AsObject();
        foreach (var (key, value) in plain)
            Assert.Equal(value?.ToJsonString(), json[key]?.ToJsonString());
        Assert.Equal(at.ToUnixTimeMilliseconds(), json["sampledAtMs"]!.GetValue<long>());
        Assert.Equal(420, json["sampleAgeMs"]!.GetValue<long>());
    }

    [Fact]
    public void An_unsampled_reading_goes_out_with_null_time_fields()
    {
        var json = TelemetryWire.ToJson(TelemetryReading.Unsampled, DateTimeOffset.UtcNow, Web);

        Assert.True(json.ContainsKey("sampledAtMs"));
        Assert.Null(json["sampledAtMs"]);
        Assert.Null(json["sampleAgeMs"]);
        Assert.Null(json["cpuTempC"]);
    }
}
