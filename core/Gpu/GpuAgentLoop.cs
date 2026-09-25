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
//
// Since 2026-09-24 it also reports the FOREGROUND APP (POST /session/foreground; ForegroundReporter).
// The daemon runs in session 0, where GetForegroundWindow is always NULL, so without this the FPS
// target and the auto-profile worker cannot see what the user is playing. This process is in the
// user's session, so it is the one place that can answer.
//
// That half runs WHATEVER the GPU-profiles gate says (audit round 2, 2026-09-24). It used to sit
// behind the gate's early exit, and the installer only autostarted the agent with -EnableGpuProfiles
// (off by default), so on a default install nothing reported and the foreground was null forever. Now
// the installer always starts the agent, the gate governs only ADLX — which is not even initialised
// when it is closed — and the foreground is reported first each tick, on its own try: an agent whose
// ADLX is unusable must still say what is in front.
using System.Net.Http.Json;
using System.Text.Json;
using GpdForge.Profiles;
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

        // The Radeon half honours the same gate as the rest of the feature. Without this it would drive
        // the Radeon settings whenever someone started the agent, regardless of whether the machine was
        // ever configured to allow that — an autostart entry outliving its own opt-in.
        bool gpuProfiles = Environment.GetEnvironmentVariable(GpuProfileService.GateVariable) == "1";
        return await RunAsync(http, new Win32ForegroundApp(), gpuProfiles, logger, ct);
    }

    /// <summary>The loop itself, over an injected client and foreground source (tests).</summary>
    public static async Task<int> RunAsync(
        HttpClient http, IForegroundApp foreground, bool gpuProfiles, ILogger? logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(foreground);
        var reporter = new ForegroundReporter(http, foreground, logger);

        if (!gpuProfiles)
        {
            Console.WriteLine("GPD Forge session agent — reporting the app in front every 3 s.");
            Console.WriteLine($"  Radeon profiles are off ({GpuProfileService.GateVariable} is not set; install with -EnableGpuProfiles to allow them).");
            while (!ct.IsCancellationRequested)
            {
                try { await reporter.ReportAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                try { await Task.Delay(Tick, ct); } catch (OperationCanceledException) { break; }
            }
            return 0;
        }

        return await RunWithGpuProfilesAsync(http, reporter, logger, ct);
    }

    private static async Task<int> RunWithGpuProfilesAsync(
        HttpClient http, ForegroundReporter reporter, ILogger? logger, CancellationToken ct)
    {
        // Exactly ONE AdlxInterop per process, for its whole lifetime. A second one would call
        // ADLXTerminate on Dispose, which invalidates every ADLX object in the process and turns the
        // survivor's pointers into invalid memory — that crashed the daemon on 2026-08-30, and an
        // AccessViolationException cannot be caught, so there is no recovering from it afterwards.
        using var adlx = new AdlxInterop(logger);
        var memory = new WmiSystemMemoryProbe();
        var probe = adlx.Initialise(memory.TotalRamMb());

        Console.WriteLine($"GPD Forge GPU agent — ADLX {probe.Version ?? "unknown"}: {probe.Status}");
        Console.WriteLine($"  {probe.Detail}");

        // Report even when ADLX is unusable. A daemon that hears "unavailable, and here is why" can
        // tell the user something; one that hears nothing cannot distinguish a broken driver from an
        // agent that never started.
        AdlxSettings? settings = probe.Status == AdlxStatus.Ready ? new AdlxSettings(adlx.System, logger) : null;

        // Mode plus a game profile's Anti-Lag / Chill (F1): the profile is re-applied when either moves.
        string? lastAppliedKey = null;
        var caps = new GpuCapReconciler();
        var images = new GpuImageReconciler();

        while (!ct.IsCancellationRequested)
        {
            // Its own try, and first: never costs the GPU work below, and never waits on it.
            try { await reporter.ReportAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }

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
                    // Read before the profile now: a game's Anti-Lag / Chill is part of what to apply.
                    // Unreadable desired state skips the profile for this tick (ReconcileKey) — it is
                    // neither "no game opinion" nor "turn them off" — and a failed read does not throw
                    // past the profile either: it is the same "unreadable".
                    var desired = await TryReadDesiredAsync(http, logger, ct);
                    var key = GpuFeatureOverride.ReconcileKey(mode, desired is not null, desired?.AntiLag, desired?.Chill);
                    if (mode is not null && key is not null && key != lastAppliedKey)
                    {
                        var profile = GpuFeatureOverride.Merge(GpuModeProfiles.For(mode), desired?.AntiLag, desired?.Chill);
                        if (profile is not null && profile.Conflict is null)
                        {
                            var applied = settings.Apply(profile);
                            foreach (var (feature, ok) in applied)
                                Console.WriteLine($"  {profile.Name}: {feature} -> {(ok ? "applied" : "NOT applied")}");
                        }
                        // Recorded even when the mode had no profile, so an unmapped mode does not
                        // make every subsequent tick retry the same nothing.
                        lastAppliedKey = key;
                    }

                    // Reconcile the frame cap towards what the daemon wants. Desired state, not
                    // commands: an agent that missed ten ticks or restarted converges on the same
                    // result instead of replaying a queue. Keyed on the request's identity, not its
                    // value (F1 audit round 4): a game asking for the 60 the agent wrote yesterday,
                    // while the user has 45 in Adrenalin since, is a new request and gets written.
                    if (desired is not null && caps.ShouldApply(desired))
                    {
                        var (ok, detail) = settings.SetFrameRateCapDetailed(desired.FrameCapFps);
                        Console.WriteLine(desired.FrameCapFps is int fps
                            ? $"  frame cap {fps} FPS -> {(ok ? "applied" : "NOT applied")}: {detail}"
                            : $"  frame cap off -> {(ok ? "applied" : "NOT applied")}: {detail}");

                        // Recorded even on failure (GpuCapReconciler.Handled).
                        caps.Handled(desired);
                    }

                    // RSR / RIS (F4): the same once-per-request reconcile as the cap, for the same
                    // reason — these are values the user may also set in Adrenalin.
                    if (desired is not null && images.ShouldApply(desired.Image, desired.ImageVersion))
                    {
                        foreach (var (field, (ok, detail)) in settings.ApplyImage(desired.Image!))
                            Console.WriteLine($"  {field} -> {(ok ? "applied" : "NOT applied")}: {detail}");
                        images.Handled(desired.ImageVersion);
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

    /// <summary><see cref="ReadDesiredAsync"/>, with a transport failure (timeout, refused) as null too.</summary>
    private static async Task<DesiredGpuState?> TryReadDesiredAsync(HttpClient http, ILogger? logger, CancellationToken ct)
    {
        try { return await ReadDesiredAsync(http, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger?.LogDebug(e, "GPU agent: /gpu/desired unreadable this tick.");
            return null;
        }
    }

    /// <summary>What the daemon wants (<see cref="DesiredGpuState.Parse"/>). Null when it could not
    /// be read — which must NOT be treated as "no cap wanted".</summary>
    private static async Task<DesiredGpuState?> ReadDesiredAsync(HttpClient http, CancellationToken ct)
    {
        using var res = await http.GetAsync("/gpu/desired", ct);
        if (!res.IsSuccessStatusCode) return null;
        return DesiredGpuState.Parse(await res.Content.ReadAsStringAsync(ct));
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
