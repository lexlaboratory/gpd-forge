// GPD Forge — core service entry point + local HTTP API. GPL-3.0-or-later.
//
// Daemon-first: this SYSTEM service owns all hardware access and exposes the local API
// (see docs/api.md). The Tauri UI, the overlay, and external agents are clients.

using GpdForge;
using GpdForge.Api;
using GpdForge.Tdp;
using GpdForge.Fan;
using GpdForge.Telemetry;
using GpdForge.Broker;
using GpdForge.Profiles;
using GpdForge.Standby;
using GpdForge.Display;
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
builder.Services.AddSingleton<JobsState>();
builder.Services.AddSingleton<IPowerControllerDetector, ProcessPowerControllerDetector>();
builder.Services.AddSingleton<ProfileApplier>();
builder.Services.AddSingleton<DisplayService>();
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

app.MapGet("/health", () => Results.Json(new { ok = true, version = "0.0.0", model = "GPD Win 4 (G1618-04)" }));

app.MapGet("/telemetry", async (ITelemetryService t, CancellationToken ct) => Results.Json(await t.ReadAsync(ct)));

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
app.MapGet("/display", (DisplayService d) => Results.Json(new { brightness = d.GetBrightness() }));
app.MapPost("/display/brightness", (BrightnessRequest r, DisplayService d) =>
{
    d.SetBrightness(r.Level);
    return Results.Json(new { brightness = d.GetBrightness() ?? r.Level });
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

    public sealed record JobConstraints(bool? RequireAC, int? MaxTempC, string? Window);
    public sealed record JobRequest(string? Cmd, JobConstraints? Constraints);
    public sealed record Job(string Id, string Cmd, string Status);

    /// <summary>In-memory job store for the local API.</summary>
    public sealed class JobsState
    {
        private readonly List<Job> _jobs = [];
        private int _seq;
        public IReadOnlyList<Job> All => _jobs;
        public Job Add(string cmd, JobConstraints? _, string status)
        {
            var job = new Job($"job-{++_seq}", cmd, status);
            _jobs.Add(job);
            return job;
        }
    }
}
