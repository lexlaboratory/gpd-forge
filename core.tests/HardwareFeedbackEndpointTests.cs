using System.Net.Http.Json;
using System.Text.Json;
using GpdForge.Guardian;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class HardwareFeedbackEndpointTests : IClassFixture<DaemonUnderTest>
{
    private readonly DaemonUnderTest _daemon;
    public HardwareFeedbackEndpointTests(DaemonUnderTest daemon) => _daemon = daemon;

    [Fact]
    public async Task Fan_status_distinguishes_capability_from_an_observed_write()
    {
        using var response = JsonDocument.Parse(await _daemon.Client.GetStringAsync("/fan"));
        var fan = response.RootElement;
        Assert.False(fan.GetProperty("controllable").GetBoolean());
        Assert.True(fan.TryGetProperty("status", out var status), "Fan endpoint must publish the worker's verification state.");
        Assert.NotEqual(JsonValueKind.True, status.GetProperty("verified").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("observedDuty").ValueKind);
        Assert.True(status.TryGetProperty("requestedDuty", out _));
        Assert.True(status.TryGetProperty("error", out _));
        Assert.True(status.TryGetProperty("atUtc", out _));
        Assert.True(status.TryGetProperty("mode", out _));

        using var posted = await _daemon.Client.PostAsJsonAsync("/fan", new { mode = "Manual", manualDuty = 128 });
        using var postBody = JsonDocument.Parse(await posted.Content.ReadAsStringAsync());
        var pending = postBody.RootElement.GetProperty("status");
        Assert.Equal(128, pending.GetProperty("requestedDuty").GetInt32());
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("verified").ValueKind);
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("atUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("error").ValueKind);
        Assert.Equal("Manual", pending.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Tdp_response_publishes_error_and_verification_classification()
    {
        using var beforeWrite = JsonDocument.Parse(await _daemon.Client.GetStringAsync("/tdp"));
        Assert.True(beforeWrite.RootElement.TryGetProperty("error", out _));
        Assert.True(beforeWrite.RootElement.TryGetProperty("verificationStatus", out _));

        using var applied = await _daemon.Client.PostAsJsonAsync("/tdp", new { stapmW = 12 });
        applied.EnsureSuccessStatusCode();
        using var postBody = JsonDocument.Parse(await applied.Content.ReadAsStringAsync());
        Assert.True(postBody.RootElement.TryGetProperty("error", out _));
        Assert.True(postBody.RootElement.TryGetProperty("verificationStatus", out _));
        using var response = JsonDocument.Parse(await _daemon.Client.GetStringAsync("/tdp"));
        Assert.True(response.RootElement.TryGetProperty("error", out _), "TDP readback failure must reach the UI.");
        Assert.True(response.RootElement.TryGetProperty("verificationStatus", out _));
        Assert.True(response.RootElement.TryGetProperty("observedStapmW", out _));
        Assert.True(response.RootElement.TryGetProperty("attempts", out _));
    }

    [Theory]
    [InlineData(93)]
    [InlineData(98)]
    public void Thermal_alert_is_a_request_until_the_hardware_apply_completes(double temperature)
    {
        var sample = TelemetrySnapshot.Unmeasured with { CpuTempC = temperature, AcConnected = true };
        var decision = GuardianEvaluator.Evaluate(sample, new GuardianConfig(), null);
        Assert.Contains("request", decision.Alert!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("holding", decision.Alert!, StringComparison.OrdinalIgnoreCase);
    }
}
