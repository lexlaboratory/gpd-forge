// GPD Forge — the GPU agent: the half of GPU control that must live in your session. GPL-3.0-or-later.
//
// Run as `dotnet GpdForge.Service.dll --gpu-agent`. Deliberately the SAME assembly as the daemon
// rather than a new executable: every unsigned binary this project adds is another thing Smart App
// Control can refuse, and it has refused six different ones in a single day. Reusing an assembly
// Windows has already accepted costs nothing and removes that risk entirely.
//
// What it does is small on purpose. It asks the daemon which mode is active, applies that mode's
// Radeon profile through ADLX, and posts back what it sees. It holds no state the daemon does not
// have, so it can be killed and restarted at any moment; the next tick re-reads and re-applies.
//
// It writes to the GPU only when the mode CHANGES, not every tick. Re-sending the same settings a few
// times a second would be pointless driver traffic, and it would also fight the user: someone who
// flips Chill in Adrenalin while the mode is steady should keep their change, not have it stamped
// over within seconds by a tool that was not asked to.
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GpdForge.Gpu;

public static class GpuAgentLoop
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(3);

    /// <summary>How long to keep trying to reach the daemon before giving up on a cycle. The agent
    /// starts at logon and may well win the race against the service.</summary>
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(string baseUrl, ILogger? logger, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = HttpTimeout };

        using var adlx = new AdlxInterop(logger);
        var memory = new WmiSystemMemoryProbe();
        var probe = adlx.Initialise(memory.TotalRamMb());

        Console.WriteLine($"GPD Forge GPU agent — ADLX {probe.Version ?? "unknown"}: {probe.Status}");
        Console.WriteLine($"  {probe.Detail}");

        // Report even when ADLX is unusable. A daemon that hears "unavailable, and here is why" can
        // tell the user something; one that hears nothing cannot distinguish a broken driver from an
        // agent that never started.
        AdlxSettings? settings = probe.Status == AdlxStatus.Ready ? new AdlxSettings(adlx.System, logger) : null;

        string? lastAppliedMode = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snapshot = settings?.Read();

                await http.PostAsJsonAsync("/gpu/state", new
                {
                    available = probe.Status == AdlxStatus.Ready,
                    status = probe.Status.ToString(),
                    adlxVersion = probe.Version,
                    detail = probe.Detail,
                    settings = snapshot,
                }, ct);

                if (settings is not null)
                {
                    var mode = await ReadActiveModeAsync(http, ct);
                    if (mode is not null && mode != lastAppliedMode)
                    {
                        var profile = GpuModeProfiles.For(mode);
                        if (profile is not null && profile.Conflict is null)
                        {
                            var applied = settings.Apply(profile);
                            foreach (var (feature, ok) in applied)
                                Console.WriteLine($"  {mode}: {feature} -> {(ok ? "applied" : "NOT applied")}");
                        }
                        // Recorded even when the mode had no profile, so an unmapped mode does not
                        // make every subsequent tick retry the same nothing.
                        lastAppliedMode = mode;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // The daemon restarting, or not up yet, is entirely normal at logon. Keep going; the
                // agent is worth nothing if it dies the first time the service blinks.
                logger?.LogDebug(e, "GPU agent tick failed.");
            }

            try { await Task.Delay(Tick, ct); } catch (OperationCanceledException) { break; }
        }

        return 0;
    }

    private static async Task<string?> ReadActiveModeAsync(HttpClient http, CancellationToken ct)
    {
        using var res = await http.GetAsync("/mode", ct);
        if (!res.IsSuccessStatusCode) return null;

        // Parsed defensively: this endpoint also serves the SPA fallback, which answers 200 with HTML.
        // A 200 is not proof the route exists — that trap cost real time on 2026-08-29.
        var body = await res.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("active", out var active) ? active.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
