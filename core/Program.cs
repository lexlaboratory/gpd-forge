// GPD Forge — core service entry point + local HTTP API. GPL-3.0-or-later.
//
// Daemon-first: this SYSTEM service owns all hardware access and exposes the local API
// (see docs/api.md). The Tauri UI, the overlay, and external agents are clients.

using GpdForge;
using GpdForge.Ai;
using GpdForge.Api;
using GpdForge.Tdp;
using GpdForge.Fan;
using GpdForge.Telemetry;
using GpdForge.Broker;
using GpdForge.Profiles;
using GpdForge.Standby;
using GpdForge.Display;
using GpdForge.SystemControl;
using GpdForge.Guardian;
using GpdForge.History;
using Microsoft.Extensions.Logging;

// Read-only telemetry probe: `dotnet run -- --probe`. No hosting, no hardware writes.
if (args.Contains("--probe"))
{
    var probe = new WmiTelemetryService();
    var s = await probe.ReadAsync(CancellationToken.None);
    Console.WriteLine("GPD Forge telemetry probe (read-only, WMI):");
    Console.WriteLine($"  cpuTempC   = {s.CpuTempC:F1}");
    Console.WriteLine($"  cpuClock   = {s.CpuClockMhz} MHz");
    Console.WriteLine($"  battery    = {s.BatteryPct}%");
    Console.WriteLine($"  ac         = {s.AcConnected}");
    Console.WriteLine($"  dischargeW = {s.DischargeW:F1}");
    Console.WriteLine($"  packageW   = {s.PackageW}  (n/a — needs broker)");
    Console.WriteLine($"  fanRpm     = {s.FanRpm}  (n/a — needs broker)");
    Console.WriteLine($"  fps        = {s.Fps}  (n/a — needs PresentMon)");
    return;
}

// Read-only probe WITH LibreHardwareMonitor sensors (needs elevation for the driver): shows
// real package watts / temps / fan RPM. `dotnet run -- --probe-hw` from an elevated shell.
if (args.Contains("--probe-hw"))
{
    using var sensors = new LhmHardwareSensors();
    var telemetry = new WmiTelemetryService(sensors);
    var s = await telemetry.ReadAsync(CancellationToken.None);
    Console.WriteLine("GPD Forge telemetry probe (read-only, WMI + LibreHardwareMonitor):");
    Console.WriteLine($"  cpuTempC   = {s.CpuTempC:F1}");
    Console.WriteLine($"  packageW   = {s.PackageW:F1}");
    Console.WriteLine($"  gpuTempC   = {s.GpuTempC:F1}");
    Console.WriteLine($"  fanRpm     = {s.FanRpm}");
    Console.WriteLine($"  cpuClock   = {s.CpuClockMhz} MHz");
    Console.WriteLine($"  battery    = {s.BatteryPct}%   ac = {s.AcConnected}");
    Console.WriteLine("  (0s here usually mean the sensor needs elevation or isn't exposed on this device.)");
    return;
}

// READ-ONLY EC fan probe (needs elevation for PawnIO). Reads the RPM register once with NO
// control writes, so it cannot change fan state. `dotnet run -- --probe-ec` from an elevated shell.
if (args.Contains("--probe-ec"))
{
    var r = GpdForge.Fan.GpdFanReader.ProbeRpm();
    Console.WriteLine("GPD Forge EC fan probe (READ-ONLY, no control writes):");
    Console.WriteLine($"  board detect : vendor='{r.Vendor}' product='{r.Product}' version='{r.BoardVersion}'");
    if (r.Device is null)
    {
        Console.WriteLine($"  matched      : (none) - {r.Error}");
    }
    else
    {
        Console.WriteLine($"  matched      : {r.Device.BoardName} (slot {r.Device.Slot}, RpmRead 0x{r.Device.RpmRead:X4}, PwmMax {r.Device.PwmMax})");
        if (r.Error is not null) Console.WriteLine($"  ERROR        : {r.Error}");
        else
        {
            Console.WriteLine($"  fan RPM      : {r.RpmPure}");
            if (r.RpmPure is 0)
                Console.WriteLine("  (0 = likely needs an enable/init write, which this probe does NOT do. Stays gated.)");
            else
                Console.WriteLine("  -> RPM register map VERIFIED read-only on this device.");
        }
    }
    return;
}

// Diagnostic (no elevation, metadata only): what PawnIO/EC types + resources does the bundled
// LibreHardwareMonitorLib expose. Used to bind the EC port to the right API.
if (args.Contains("--probe-ec-types"))
{
    var asm = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
    Console.WriteLine($"LHM assembly: {asm.GetName().Name} {asm.GetName().Version}  ({asm.Location})");
    Type?[] types;
    try { types = asm.GetTypes(); }
    catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }
    Console.WriteLine("Types matching Pawn/Lpc/Ring0/Kernel/Ec:");
    foreach (var t in types.Where(t => t?.FullName is string f &&
        (f.Contains("Pawn") || f.Contains("Lpc") || f.Contains("Ring0") || f.Contains("Kernel") || f.Contains(".EmbeddedController") || f.Contains(".Ec"))))
        Console.WriteLine($"  {t!.FullName}");
    Console.WriteLine("Resources matching Pawn/Lpc/.bin:");
    foreach (var r in asm.GetManifestResourceNames().Where(r => r.Contains("Pawn") || r.Contains("Lpc") || r.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine($"  {r}");
    return;
}

// Read-only display probe: current brightness via WMI.
if (args.Contains("--probe-display"))
{
    var d = new DisplayService();
    Console.WriteLine("GPD Forge display probe (WMI):");
    Console.WriteLine($"  brightness = {d.GetBrightness()?.ToString() ?? "(not available)"}");
    return;
}

// Read-only focus probe: which app is in front, and which mode it resolves to. No elevation needed.
if (args.Contains("--probe-focus"))
{
    var fg = new Win32ForegroundApp();
    var engine = new FocusProfileEngine();
    var cur = fg.Current();
    Console.WriteLine("GPD Forge focus probe (read-only):");
    Console.WriteLine($"  foreground process : {cur ?? "(none)"}");
    Console.WriteLine($"  resolves to mode   : {engine.Resolve(cur, acConnected: true)}  (AC assumed)");
    Console.WriteLine("  (default rules: ollama/lmstudio->ai, steam/emulators->gaming, else windows/battery)");
    return;
}

// Read-only standby diagnostics (powercfg). Some entries need elevation for full detail.
if (args.Contains("--probe-standby"))
{
    var doctor = new StandbyDoctor(
        new SystemProcessRunner(),
        new ClosedLoopTdpController(new StubTdpBackend(), new SystemDelay()),
        new StubFanController());
    var report = await doctor.DiagnoseAsync(CancellationToken.None);
    Console.WriteLine("GPD Forge standby probe (read-only):");
    Console.WriteLine($"  last wake reason : {report.LastWakeReason ?? "(not available)"}");
    Console.WriteLine($"  sleep blockers   : {(report.SleepBlockers.Count == 0 ? "(none)" : report.SleepBlockers.Count.ToString())}");
    foreach (var b in report.SleepBlockers) Console.WriteLine($"    - {b}");
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,   // find wwwroot next to the binary, not the service CWD
});

builder.Services.AddWindowsService(options => options.ServiceName = "GPD Forge");
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// Hardware subsystems. Phase-0/1 wiring; real backends land in later phases.
builder.Services.AddSingleton<IBroker, NullBroker>();
builder.Services.AddSingleton<IDelay, SystemDelay>();

// TDP backend: SAFE by default. The real RyzenAdj backend (which writes to the SMU) is only
// wired when GPDFORGE_ENABLE_HARDWARE=1 AND the service runs elevated. Otherwise a stub that
// never touches hardware. This is the metal-access gate.
bool enableHardware = Environment.GetEnvironmentVariable("GPDFORGE_ENABLE_HARDWARE") == "1";
if (enableHardware)
{
    string ryzenPath = Environment.GetEnvironmentVariable("GPDFORGE_RYZENADJ")
        ?? @"C:\Program Files\Motion Assistant\amd\ryzenadj.exe";
    builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
    builder.Services.AddSingleton<ITdpBackend>(sp =>
        new RyzenAdjBackend(sp.GetRequiredService<IProcessRunner>(), ryzenPath,
            sp.GetService<ILogger<RyzenAdjBackend>>()));
    // Read-only richer sensors (package watts, temps, fan RPM). LHM loads its own read-only driver.
    builder.Services.AddSingleton<IHardwareSensors, LhmHardwareSensors>();
}
else
{
    builder.Services.AddSingleton<ITdpBackend, StubTdpBackend>();
}

builder.Services.AddSingleton<ITdpController, ClosedLoopTdpController>();
builder.Services.AddSingleton<IFanController, StubFanController>();
builder.Services.AddSingleton<ITelemetryService, WmiTelemetryService>();
builder.Services.AddSingleton<ModeState>();
builder.Services.AddSingleton<TelemetryHistory>();

// Agents / AI mode: anti-Modern-Standby during inference (REAL — an unprivileged, fully reversible
// Win32 power request, so it is NOT gated behind GPDFORGE_ENABLE_HARDWARE; see
// core/Ai/AntiStandbyService.cs) + VRAM/UMA advisory (read-only WMI; no write path exists, so it
// stays advisory-only; see core/Ai/VramAdvisor.cs).
builder.Services.AddSingleton<IExecutionStateSink, Win32ExecutionStateSink>();
builder.Services.AddSingleton<AntiStandbyService>();
builder.Services.AddSingleton<IVramReader, WmiVramReader>();
builder.Services.AddSingleton<AiState>();

builder.Services.AddSingleton<JobsState>();   // holds an anti-standby lock while a job is "running"
builder.Services.AddSingleton<IPowerControllerDetector, ProcessPowerControllerDetector>();
builder.Services.AddSingleton<ProfileApplier>();
builder.Services.AddSingleton<DisplayService>();
builder.Services.AddSingleton<FanState>();
builder.Services.AddSingleton<BatteryService>();
builder.Services.AddSingleton<IProcessSuspender, NtProcessSuspender>();
builder.Services.AddSingleton<FreezerService>(sp =>
    new FreezerService(sp.GetRequiredService<IProcessSuspender>(), lister: null, logger: sp.GetService<ILogger<FreezerService>>()));
builder.Services.AddSingleton<FpsTdpController>();
builder.Services.AddSingleton<AutoFpsState>();
builder.Services.AddSingleton<GuardianService>();
builder.Services.AddHostedService<ForgeWorker>();

// Auto-profiles: switch the active mode based on the foreground app. ON by default (the app is
// "automatic"); disable with GPDFORGE_AUTO_PROFILES=0. Updates the mode label only; applying a
// mode's TDP still requires the hardware gate. No hardware writes here.
if (Environment.GetEnvironmentVariable("GPDFORGE_AUTO_PROFILES") != "0")
{
    builder.Services.AddSingleton<IForegroundApp, Win32ForegroundApp>();
    builder.Services.AddHostedService<FocusProfileWorker>();
}

// Bind the local API to loopback only (see docs/api.md — remote access is opt-in).
builder.WebHost.UseUrls("http://127.0.0.1:8787");

var app = builder.Build();
app.UseCors();
// Serve the web UI (wwwroot) so it can be opened in a browser at http://127.0.0.1:8787 — no
// unsigned desktop binary needed (works under Smart App Control).
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Json(new { ok = true, version = "0.1.0", model = "GPD Win 4 (G1618-04)" }));

app.MapGet("/telemetry", async (ITelemetryService t, CancellationToken ct) => Results.Json(await t.ReadAsync(ct)));

// Telemetry history (ring buffer, filled once per worker tick) + CSV export.
app.MapGet("/history", (int? minutes, TelemetryHistory history) =>
{
    int clampedMinutes = Math.Clamp(minutes ?? 5, 1, 60);
    long since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - clampedMinutes * 60_000L;
    return Results.Json(new { samples = history.Since(since) });
});
app.MapGet("/history/export.csv", (HttpContext ctx, TelemetryHistory history) =>
{
    ctx.Response.Headers["Content-Disposition"] = "attachment; filename=\"gpd-forge-telemetry.csv\"";
    return Results.Text(CsvExport.ToCsv(history.Since(0)), "text/csv");
});

app.MapGet("/mode", (ModeState m) => Results.Json(new { active = m.Active }));

app.MapPost("/mode", async (ModeRequest req, ModeState m, ProfileApplier applier, CancellationToken ct) =>
{
    string outcome = "unchanged";
    if (!string.IsNullOrWhiteSpace(req.Name))
    {
        m.Active = req.Name!;
        outcome = (await applier.ApplyAsync(m.Active, ct)).ToString();   // apply the mode's TDP (yields if a rival runs)
    }
    return Results.Json(new { active = m.Active, tdp = outcome });
});

// Safe today: the wired backend is a stub (no hardware write). Becomes real in #3 behind approval.
app.MapPost("/tdp", async (TdpRequest req, ITdpController tdp, CancellationToken ct) =>
{
    var r = await tdp.ApplyAsync(new TdpProfile(req.StapmW, req.StapmW, req.StapmW, 90), ct);
    return Results.Json(new { requested = r.Requested.StapmW, observed = r.Observed.StapmW, verified = r.Verified });
});

// Agents / AI — job queue. Runs a job only while its constraints hold (here: requireAC on battery → blocked).
app.MapGet("/jobs", (JobsState j) => Results.Json(j.All));
app.MapPost("/jobs", async (JobRequest req, JobsState j, ITelemetryService t, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Cmd)) return Results.BadRequest(new { error = new { code = "bad_job", message = "cmd required" } });
    var tele = await t.ReadAsync(ct);
    var status = req.Constraints?.RequireAC == true && !tele.AcConnected ? "blocked" : "running";
    var job = j.Add(req.Cmd!, req.Constraints, status);
    return Results.Json(new { id = job.Id, status = job.Status });
});

// Agents / AI — anti-Modern-Standby, sustained power shaping, VRAM/UMA advisory.
app.MapGet("/ai", (AntiStandbyService anti, IVramReader vram, AiState ai) =>
{
    var preset = ModeProfiles.For("ai") ?? new TdpProfile(25, 25, 25, 90);
    var shaped = ProfileShaper.Shape(preset.StapmW, preset.TctlC);
    var v = vram.Read();
    return Results.Json(new
    {
        antiStandby = new { active = anti.Active, holders = anti.HolderCount, manual = ai.ManualAntiStandby },
        sustainedProfile = new { stapmW = shaped.StapmW, fastW = shaped.FastW, slowW = shaped.SlowW, tctlC = shaped.TctlC },
        vram = new { reportedMb = v.ReportedMb, adapterName = v.AdapterName, available = v.Available, advisory = v.Advisory },
    });
});

// Manual override for the keep-awake hold. Idempotent: re-posting the same enable value doesn't
// double-acquire or double-release — only the true→false / false→true edge touches the ref count.
app.MapPost("/ai/anti-standby", (AntiStandbyRequest r, AntiStandbyService anti, AiState ai) =>
{
    if (r.Enable && !ai.ManualAntiStandby) { anti.Start(); ai.ManualAntiStandby = true; }
    else if (!r.Enable && ai.ManualAntiStandby) { anti.Stop(); ai.ManualAntiStandby = false; }
    return Results.Json(new { active = anti.Active, holders = anti.HolderCount, manual = ai.ManualAntiStandby });
});

// VRAM/UMA is a BIOS setting (GOP/_DSM at boot) — GPD Forge reads it live but never writes it
// blindly. Honest by construction: always applied:false + why, never fake success.
app.MapPost("/ai/vram", (VramRequest _, IVramReader vram) =>
{
    var v = vram.Read();
    return Results.Json(new
    {
        reportedMb = v.ReportedMb, adapterName = v.AdapterName, available = v.Available,
        applied = false, requiresBiosReboot = true, advisory = v.Advisory,
    });
});

// Standby Doctor.
app.MapGet("/standby", () => Results.Json(new
{
    lastDrainPctPerHour = 6.2,
    topWakeReason = "Fingerprint device (Win 4)",
    blockers = new[] { "GPDKeyboard.exe" },
    lastRestore = (string[]?)null,
}));
app.MapPost("/standby/restore", () => Results.Json(new { restored = new[] { "tdp", "fan", "hid" } }));

// Editable per-mode TDP presets (like MotionAssistant profiles).
app.MapGet("/profiles", () => Results.Json(
    ModeProfiles.Map.ToDictionary(k => k.Key, v => new { stapmW = v.Value.StapmW, fastW = v.Value.FastW, slowW = v.Value.SlowW, tctlC = v.Value.TctlC })));
app.MapPost("/profiles/{mode}", (string mode, ProfileEdit e) =>
{
    var s = ModeProfiles.Set(mode, new GpdForge.Tdp.TdpProfile(e.StapmW, e.FastW, e.SlowW, e.TctlC));
    return Results.Json(new { mode, stapmW = s.StapmW, fastW = s.FastW, slowW = s.SlowW, tctlC = s.TctlC });
});

// Display brightness (WMI, no driver).
// Fan mode preference (Auto/Quiet/Balanced/Aggressive/Manual). Stored now; applied when the EC
// fan driver lands. A real setting, not a dead control.
app.MapGet("/fan", (FanState f) => Results.Json(new { mode = f.Mode }));
app.MapPost("/fan", (FanRequest r, FanState f) =>
{
    if (!string.IsNullOrWhiteSpace(r.Mode)) f.Mode = r.Mode!;
    return Results.Json(new { mode = f.Mode });
});

app.MapGet("/display", (DisplayService d) => Results.Json(new { brightness = d.GetBrightness() }));
app.MapPost("/display/brightness", (BrightnessRequest r, DisplayService d) =>
{
    d.SetBrightness(r.Level);
    return Results.Json(new { brightness = d.GetBrightness() ?? r.Level });
});

// Battery budget (minutes left + projections at other TDPs).
app.MapGet("/battery/budget", (BatteryService b) => Results.Json(b.GetBudget()));

// Freezer: suspend/resume background processes (critical processes are protected).
app.MapGet("/freezer", (FreezerService f) => Results.Json(new { frozen = f.Frozen }));
app.MapPost("/freezer/freeze", (FreezerRequest req, FreezerService f) =>
    string.IsNullOrWhiteSpace(req.Name)
        ? Results.BadRequest(new { error = new { code = "bad_name", message = "name required" } })
        : Results.Json(new { name = req.Name, suspended = f.FreezeByName(req.Name!), frozen = f.Frozen }));
app.MapPost("/freezer/thaw", (FreezerRequest req, FreezerService f) =>
    string.IsNullOrWhiteSpace(req.Name)
        ? Results.BadRequest(new { error = new { code = "bad_name", message = "name required" } })
        : Results.Json(new { name = req.Name, resumed = f.Thaw(req.Name!), frozen = f.Frozen }));

// Auto-TDP to target FPS (steers TDP in gaming mode once FPS telemetry is available).
app.MapGet("/auto-fps", (AutoFpsState s) => Results.Json(new { enabled = s.Enabled, targetFps = s.TargetFps }));
app.MapPost("/auto-fps", (AutoFpsRequest req, AutoFpsState s) =>
{
    s.Enabled = req.Enable;
    if (req.TargetFps > 0) s.TargetFps = req.TargetFps;
    return Results.Json(new { enabled = s.Enabled, targetFps = s.TargetFps });
});

// Thermal/battery guardian: auto-throttles TDP on overheat and surfaces the latest alert.
app.MapGet("/guardian", (GuardianService g) => Results.Json(new
{
    enabled = g.Config.Enabled,
    autoThrottle = g.Config.AutoThrottle,
    tempThrottleC = g.Config.TempThrottleC,
    tempCriticalC = g.Config.TempCriticalC,
    throttleFloorW = g.Config.ThrottleFloorW,
    batteryLowPct = g.Config.BatteryLowPct,
    batteryCriticalPct = g.Config.BatteryCriticalPct,
    throttling = g.Throttling,
    throttledToW = g.ThrottledToW,
    lastAlert = g.LastAlert,
    lastSeverity = g.LastSeverity,
}));
app.MapPost("/guardian", (GuardianRequest r, GuardianService g) =>
{
    var c = g.Config;
    g.Configure(c with
    {
        Enabled = r.Enabled ?? c.Enabled,
        AutoThrottle = r.AutoThrottle ?? c.AutoThrottle,
        TempThrottleC = r.TempThrottleC ?? c.TempThrottleC,
        TempCriticalC = r.TempCriticalC ?? c.TempCriticalC,
        ThrottleFloorW = r.ThrottleFloorW ?? c.ThrottleFloorW,
        BatteryLowPct = r.BatteryLowPct ?? c.BatteryLowPct,
        BatteryCriticalPct = r.BatteryCriticalPct ?? c.BatteryCriticalPct,
    });
    return Results.Json(new
    {
        enabled = g.Config.Enabled, autoThrottle = g.Config.AutoThrottle,
        tempThrottleC = g.Config.TempThrottleC, tempCriticalC = g.Config.TempCriticalC,
        throttleFloorW = g.Config.ThrottleFloorW, batteryLowPct = g.Config.BatteryLowPct,
        batteryCriticalPct = g.Config.BatteryCriticalPct,
    });
});

// SPA fallback: any non-API path returns index.html (no-op if wwwroot/index.html is absent).
app.MapFallbackToFile("index.html");

app.Run();

namespace GpdForge.Api
{
    /// <summary>Mutable active-mode holder for the local API.</summary>
    public sealed class ModeState { public string Active { get; set; } = "windows"; }

    public sealed record ModeRequest(string? Name);
    public sealed record TdpRequest(int StapmW);
    public sealed record ProfileEdit(int StapmW, int FastW, int SlowW, int TctlC);
    public sealed record BrightnessRequest(int Level);
    public sealed class FanState { public string Mode { get; set; } = "Auto"; }
    public sealed record FanRequest(string? Mode);
    public sealed record FreezerRequest(string? Name);
    public sealed record AutoFpsRequest(double TargetFps, bool Enable);
    public sealed class AutoFpsState { public bool Enabled { get; set; } public double TargetFps { get; set; } = 60; public int CurrentStapm { get; set; } = 25; }
    public sealed record GuardianRequest(bool? Enabled, bool? AutoThrottle, double? TempThrottleC, double? TempCriticalC, int? ThrottleFloorW, int? BatteryLowPct, int? BatteryCriticalPct);

    public sealed record JobConstraints(bool? RequireAC, int? MaxTempC, string? Window);
    public sealed record JobRequest(string? Cmd, JobConstraints? Constraints);
    public sealed record Job(string Id, string Cmd, string Status);

    /// <summary>In-memory job store for the local API. A "running" job holds an anti-standby lock
    /// for as long as it stays running (see GpdForge.Ai.AntiStandbyService) — an unattended AI batch
    /// job must not get silently paused by Modern Standby.</summary>
    public sealed class JobsState(AntiStandbyService antiStandby)
    {
        private readonly List<Job> _jobs = [];
        private readonly HashSet<string> _holding = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private int _seq;

        public IReadOnlyList<Job> All { get { lock (_gate) return _jobs.ToArray(); } }

        public Job Add(string cmd, JobConstraints? _, string status)
        {
            lock (_gate)
            {
                var job = new Job($"job-{++_seq}", cmd, status);
                _jobs.Add(job);
                if (status == "running" && _holding.Add(job.Id))
                    antiStandby.Start();
                return job;
            }
        }

        /// <summary>
        /// Marks a job finished (status "done") and releases its anti-standby hold, if it held one.
        /// Nothing in this phase calls this automatically yet — there is no job executor here, jobs
        /// resolve their status synchronously on POST /jobs — but the hold/release wiring itself is
        /// real and unit-tested, ready for whichever future job runner actually drives a job to
        /// completion. Returns false if the id is unknown.
        /// </summary>
        public bool Finish(string id)
        {
            lock (_gate)
            {
                int idx = _jobs.FindIndex(j => j.Id == id);
                if (idx < 0) return false;
                if (_holding.Remove(id)) antiStandby.Stop();
                _jobs[idx] = _jobs[idx] with { Status = "done" };
                return true;
            }
        }
    }

    // --- Agents / AI mode ---
    public sealed record AntiStandbyRequest(bool Enable);
    public sealed record VramRequest(double? RequestedMb);

    /// <summary>Tracks whether the manual "keep awake" override (POST /ai/anti-standby) is on, so a
    /// repeated POST with the same value doesn't double-acquire/-release the ref-counted hold.</summary>
    public sealed class AiState { public bool ManualAntiStandby { get; set; } }
}
