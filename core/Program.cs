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
using GpdForge.Import;
using GpdForge.Tuner;
using GpdForge.Update;
using GpdForge.Led;
using GpdForge.Battery;
using GpdForge.Undervolt;
using GpdForge.Health;
using GpdForge.Onboarding;
using GpdForge.Alerts;
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

// GATED fan-WRITE probe (the PARENT runs this manually, on-device, elevated, to validate the manual
// PWM duty write sequence against real hardware — see core/Fan/GpdFanController.cs). Requires BOTH
// GPDFORGE_ENABLE_FAN_CONTROL=1 (this project's extra opt-in for fan WRITES specifically, on top of
// the general hardware gate) and elevation (the PawnIO EC driver). Sets a manual duty, prints the
// read-back + RPM, then AUTOMATICALLY restores AUTOMATIC after a 5s dwell so a probe run and
// forgotten never leaves the fan pinned in manual. `dotnet run -- --probe-fan-set 128` (0-255).
if (args.Contains("--probe-fan-set"))
{
    if (!FanControlPolicy.IsEnvironmentGateOpen())
    {
        Console.WriteLine("--probe-fan-set refused: set BOTH GPDFORGE_ENABLE_HARDWARE=1 and GPDFORGE_ENABLE_FAN_CONTROL=1 (and run elevated) to allow this probe to write the EC.");
        return;
    }
    int flagIdx = Array.IndexOf(args, "--probe-fan-set");
    if (flagIdx < 0 || flagIdx + 1 >= args.Length || !int.TryParse(args[flagIdx + 1], out int requestedDuty))
    {
        Console.WriteLine("Usage: --probe-fan-set <0-255>");
        return;
    }
    var (fsVendor, fsProduct, fsVersion) = GpdForge.Fan.GpdFanReader.DetectBoard();
    var fsDevice = GpdForge.Fan.GpdDeviceDb.MatchBoard(fsVendor, fsProduct, fsVersion);
    if (fsDevice is null)
    {
        Console.WriteLine($"GPD Forge fan-set probe: no matching board for '{fsVendor}/{fsProduct}/{fsVersion}'.");
        return;
    }
    Console.WriteLine($"GPD Forge fan-set probe (GATED — WRITES the EC): {fsDevice.BoardName}, requested duty {requestedDuty} (0-255 user scale).");
    Console.WriteLine("This drives the REAL fan. It will restore AUTOMATIC after a 5s dwell — let it finish.");
    using (var controller = new GpdForge.Fan.GpdFanController(fsDevice))
    {
        bool verified = controller.SetManualDuty(requestedDuty);
        int? readBack = controller.ReadDuty();
        var rpm = GpdForge.Fan.GpdFanReader.ProbeRpm(fsVendor, fsProduct, fsVersion);
        Console.WriteLine($"  verified      : {verified}");
        Console.WriteLine($"  duty read-back: {(readBack?.ToString() ?? "(none)")} (0-255 user scale)");
        Console.WriteLine($"  fan RPM       : {(rpm.RpmPure?.ToString() ?? rpm.Error ?? "(unknown)")}");
        Console.WriteLine("  dwelling 5s before restoring AUTOMATIC...");
        await Task.Delay(5000);
        controller.SetAuto();
        Console.WriteLine("  restored AUTOMATIC (manual_control_enable = 0).");
    }
    return;
}

// Force AUTOMATIC fan control (GATED — writes the EC). The parent's tool to bail a stuck manual
// state back to firmware control. `dotnet run -- --probe-fan-auto` from an elevated shell.
if (args.Contains("--probe-fan-auto"))
{
    if (!FanControlPolicy.IsEnvironmentGateOpen())
    {
        Console.WriteLine("--probe-fan-auto refused: set BOTH GPDFORGE_ENABLE_HARDWARE=1 and GPDFORGE_ENABLE_FAN_CONTROL=1 (and run elevated) to allow this probe to write the EC.");
        return;
    }
    var (faVendor, faProduct, faVersion) = GpdForge.Fan.GpdFanReader.DetectBoard();
    var faDevice = GpdForge.Fan.GpdDeviceDb.MatchBoard(faVendor, faProduct, faVersion);
    if (faDevice is null)
    {
        Console.WriteLine($"GPD Forge fan-auto probe: no matching board for '{faVendor}/{faProduct}/{faVersion}'.");
        return;
    }
    using var autoController = new GpdForge.Fan.GpdFanController(faDevice);
    autoController.SetAuto();
    Console.WriteLine($"GPD Forge fan-auto probe: {faDevice.BoardName} — wrote manual_control_enable = 0 (AUTOMATIC).");
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

// Enums go out as their NAMES, not their ordinals. Without this, System.Text.Json serialized
// AlertSeverity.Aviso as `1`, while the mock daemon, docs/api.md and ui/src/types.ts all agree the
// wire format is "Aviso". The UI called severity.toLowerCase() on a number, React threw during
// render, and with no boundary the whole app unmounted - the window went blank and nothing was
// clickable. An ordinal is also a worse contract: reordering an enum member would silently change
// the meaning of stored alerts.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

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
    // Read-only richer sensors (package watts, temps). LHM loads its own read-only driver.
    builder.Services.AddSingleton<IHardwareSensors, LhmHardwareSensors>();
    // Real GPD fan RPM via the PawnIO EC read (LHM doesn't expose it). Read-only, keeps one port open.
    builder.Services.AddSingleton<GpdForge.Fan.IFanRpm, GpdForge.Fan.PawnIoFanRpm>();
}
else
{
    builder.Services.AddSingleton<ITdpBackend, StubTdpBackend>();
}

// FPS telemetry, behind its OWN gate. Frame timing is an ETW capability with nothing in common with
// the MSR/EC access above, and the hardware path is physically validated — a PresentMon failure must
// not be able to take it down. Not registering the probe is a supported state: WmiTelemetryService
// takes it as a nullable constructor parameter and simply reports fps 0.
if (Environment.GetEnvironmentVariable("GPDFORGE_ENABLE_FPS") == "1")
{
    var presentMon = PresentMonFrameRateProbe.Locate();
    if (presentMon is not null)
    {
        builder.Services.AddSingleton<IFrameRateProbe>(sp =>
            new PresentMonFrameRateProbe(presentMon, sp.GetService<ILogger<PresentMonFrameRateProbe>>()));
    }
    // else: PresentMon is not installed. Left unregistered on purpose — fps stays 0 ("n/a") rather
    // than the service failing to start over an optional sensor.
}

// Gated fan (PWM duty) WRITE control: fan writes are riskier than a read (commanding the wrong duty
// is an immediate physical risk), so they require a SECOND, separate opt-in on top of the general
// hardware gate — GPDFORGE_ENABLE_HARDWARE=1 alone is not enough. Even with both set, an unmatched
// board falls back to the honest no-op (never guesses at a register map). See
// core/Fan/GpdFanController.cs for the write sequence + safety floor.
bool enableFanControl = FanControlPolicy.IsEnvironmentGateOpen();
if (enableFanControl)
{
    var (fcVendor, fcProduct, fcVersion) = GpdForge.Fan.GpdFanReader.DetectBoard();
    var fcDevice = GpdForge.Fan.GpdDeviceDb.MatchBoard(fcVendor, fcProduct, fcVersion);
    if (fcDevice is not null)
    {
        builder.Services.AddSingleton<GpdForge.Fan.IGpdFanController>(sp =>
            new GpdForge.Fan.GpdFanController(fcDevice, logger: sp.GetService<ILogger<GpdForge.Fan.GpdFanController>>()));
    }
    else
    {
        builder.Services.AddSingleton<GpdForge.Fan.IGpdFanController, GpdForge.Fan.NoOpGpdFanController>();
    }
}
else
{
    builder.Services.AddSingleton<GpdForge.Fan.IGpdFanController, GpdForge.Fan.NoOpGpdFanController>();
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

// Display domain extensions: refresh-rate switching + night mode (gamma ramp) are REAL, unprivileged
// OS-level APIs (no EC/BIOS), so they are NOT gated. Tablet mode writes a system-wide registry value
// (see TabletModeService.cs) so, like the TDP backend above, it only WRITES when
// GPDFORGE_ENABLE_HARDWARE=1 — reads always work. Keyboard backlight has no known safe write path at
// all (EC-controlled, same blocked path as the fan) and stays advisory-only unconditionally.
builder.Services.AddSingleton<IDisplayModeSource, Win32DisplayModeSource>();
builder.Services.AddSingleton<RefreshRateService>();
builder.Services.AddSingleton<IGammaRampSink, Win32GammaRampSink>();
builder.Services.AddSingleton<NightModeService>();
builder.Services.AddSingleton<ITabletModeRegistry, WindowsTabletModeRegistry>();
builder.Services.AddSingleton(sp => new TabletModeService(
    sp.GetRequiredService<ITabletModeRegistry>(), enableHardware, sp.GetService<ILogger<TabletModeService>>()));
builder.Services.AddSingleton<KeyboardBacklightService>();

// Advanced (hardware-gated) controls: LED/RGB, battery charge limit, undervolt/Curve Optimizer.
// All three follow the same honesty stance as TabletModeService/KeyboardBacklightService above:
// real pure validators/encoders (unit-tested), a real injectable write-attempt interface where one
// conceptually exists (LED, charge limit), and applied:false + an advisory whenever there is no
// verified path to actually reach the hardware — which today is unconditionally true for all three
// on this HX370 (see each service's file header for specifics). Never a blind EC/registry/SMU write.
builder.Services.AddSingleton<ILedHidWriter, HidLedWriter>();
builder.Services.AddSingleton(sp => new LedService(
    sp.GetRequiredService<ILedHidWriter>(), enableHardware, sp.GetService<ILogger<LedService>>()));
builder.Services.AddSingleton<IChargeLimitBackend, UnavailableChargeLimitBackend>();
builder.Services.AddSingleton(sp => new ChargeLimitService(
    sp.GetRequiredService<IChargeLimitBackend>(), enableHardware, sp.GetService<ILogger<ChargeLimitService>>()));
builder.Services.AddSingleton(sp => new CurveOptimizerService(enableHardware, sp.GetService<ILogger<CurveOptimizerService>>()));

builder.Services.AddSingleton<FanState>();
builder.Services.AddSingleton<BatteryService>();
builder.Services.AddSingleton<IProcessSuspender, NtProcessSuspender>();
builder.Services.AddSingleton<FreezerService>(sp =>
    new FreezerService(sp.GetRequiredService<IProcessSuspender>(), lister: null, logger: sp.GetService<ILogger<FreezerService>>()));
builder.Services.AddSingleton<FpsTdpController>();
builder.Services.AddSingleton<AutoFpsState>();
builder.Services.AddSingleton<GuardianService>();
builder.Services.AddSingleton<IAlertClock, SystemAlertClock>();
builder.Services.AddSingleton<AlertStore>(sp =>
{
    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GPD Forge");
    return new AlertStore(root, sp.GetRequiredService<IAlertClock>());
});
builder.Services.AddSingleton<AlertService>();

// Auto-tuner: sweeps STAPM and picks the best point for a goal (max fps / best efficiency / hold a
// target fps). ForgeWorker steps the sweep each tick; TunerState just holds config + recorded
// points (see core/Tuner/). Not gated behind GPDFORGE_ENABLE_HARDWARE — it only ever applies TDP
// through the same ITdpController every other feature here already uses.
builder.Services.AddSingleton<TunerState>();

// Update checker: read-only GitHub REST call with a short timeout, degrades to "no update" on any
// failure (see core/Update/). Not a hardware/BIOS write, so NOT gated behind GPDFORGE_ENABLE_HARDWARE.
builder.Services.AddSingleton<ILatestReleaseSource, GitHubReleaseSource>();
builder.Services.AddSingleton<UpdateService>();

// Migration + per-power-source config: reading MotionAssistant's saved profiles is read-only
// filesystem access (same trust level as the WMI reads above), so it is NOT gated behind
// GPDFORGE_ENABLE_HARDWARE.
builder.Services.AddSingleton<IIniFileSource, FileIniSource>();
builder.Services.AddSingleton<PowerSourceState>();

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

app.MapGet("/alerts", (HttpContext ctx, AlertService alerts) =>
{
    var rawLimit = ctx.Request.Query["limit"].FirstOrDefault();
    if (rawLimit is not null && (!int.TryParse(rawLimit, out var parsed) || parsed is < 1 or > 500))
        return Results.BadRequest(new { error = "limit must be between 1 and 500" });
    var unreadOnly = string.Equals(ctx.Request.Query["unreadOnly"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
    return Results.Json(new { alerts = alerts.List(unreadOnly, rawLimit is null ? null : int.Parse(rawLimit)) });
});
app.MapGet("/alerts/summary", (AlertService alerts) => Results.Json(alerts.Summary()));
app.MapPost("/alerts/{id:guid}/ack", (Guid id, AlertService alerts) =>
    alerts.Acknowledge(id) ? Results.Json(new { acknowledged = true, id }) : Results.NotFound(new { error = "alert not found or already acknowledged" }));
app.MapPost("/alerts/ack-all", (AlertService alerts) => Results.Json(new { acknowledged = alerts.AcknowledgeAll() }));
app.MapDelete("/alerts/{id:guid}", (Guid id, AlertService alerts) =>
    alerts.Delete(id) ? Results.NoContent() : Results.NotFound(new { error = "alert not found" }));

// Update checker: compares the running version against the latest GitHub release. Never throws — any
// failure (offline, rate-limited, malformed response) degrades honestly to updateAvailable:false.
app.MapGet("/update/check", async (UpdateService updates, CancellationToken ct) =>
{
    var r = await updates.CheckAsync(ct);
    return Results.Json(new { current = r.Current, latest = r.Latest, updateAvailable = r.UpdateAvailable, url = r.Url });
});

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

// Panic cool: an immediate, dead-simple safety floor. Applies a flat 8W ceiling through the same
// closed-loop controller every other TDP write uses (so it's honestly reported, not a blind write)
// and pushes the fan preference to Aggressive. `applied` mirrors the closed loop's verification —
// never claims success the firmware didn't actually hold.
app.MapPost("/panic", async (ITdpController tdp, FanState fan, CancellationToken ct) =>
{
    var floor = new TdpProfile(8, 8, 8, 90);
    var r = await tdp.ApplyAsync(floor, ct);
    fan.Mode = "Aggressive";
    return Results.Json(new { applied = r.Verified, stapmW = floor.StapmW });
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

// MotionAssistant .ini importer: read-only, never throws. Only RETURNS parsed profiles — applying
// one reuses the existing POST /profiles/:mode above.
app.MapPost("/import/motionassistant", (IIniFileSource src) =>
{
    string path = src.ProfilesDirectory;
    if (!src.DirectoryExists())
        return Results.Json(new { found = 0, profiles = Array.Empty<ImportedProfile>(), path });

    var profiles = new List<ImportedProfile>();
    foreach (var text in src.ReadAllIniFiles())
        profiles.AddRange(MotionAssistantImporter.ParseIni(text));

    return Results.Json(new { found = profiles.Count, profiles, path });
});

// First-run setup wizard: are MotionAssistant / GPD Tool currently running? Reuses the same
// IPowerControllerDetector ProfileApplier already yields to, so the wizard's advice ("run the
// installer's -Substitute") and the daemon's actual yield-while-running behavior can never disagree.
app.MapGet("/system/incumbents", (IPowerControllerDetector detector) =>
{
    detector.OthersRunning(out var names);
    var s = IncumbentsCheck.From(names);
    return Results.Json(new { motionAssistant = s.MotionAssistant, gpdTool = s.GpdTool });
});

// Per-power-source auto mode-switch (AC vs battery). ForgeWorker applies it on the AC/battery edge.
app.MapGet("/power-source", (PowerSourceState s) => Results.Json(new
{
    enabled = s.Config.Enabled, onBatteryMode = s.Config.OnBatteryMode, onAcMode = s.Config.OnAcMode,
}));
app.MapPost("/power-source", (PowerSourceRequest r, PowerSourceState s) =>
{
    var c = s.Config;
    s.Config = c with
    {
        Enabled = r.Enabled ?? c.Enabled,
        OnBatteryMode = string.IsNullOrWhiteSpace(r.OnBatteryMode) ? c.OnBatteryMode : r.OnBatteryMode,
        OnAcMode = string.IsNullOrWhiteSpace(r.OnAcMode) ? c.OnAcMode : r.OnAcMode,
    };
    return Results.Json(new { enabled = s.Config.Enabled, onBatteryMode = s.Config.OnBatteryMode, onAcMode = s.Config.OnAcMode });
});

// Display brightness (WMI, no driver).
// Fan mode preference (Auto/Quiet/Balanced/Aggressive/Manual) + manual duty. `controllable` reports
// whether GPD Forge is actually gated to WRITE the EC right now (GPDFORGE_ENABLE_HARDWARE=1 AND
// GPDFORGE_ENABLE_FAN_CONTROL=1 AND a matched board) — see ForgeWorker.cs for the tick that applies
// this, and core/Fan/GpdFanController.cs for the write path itself.
app.MapGet("/fan", (FanState f, IGpdFanController controller) => Results.Json(new { mode = f.Mode, manualDuty = f.ManualDuty, controllable = controller.Available }));
app.MapPost("/fan", (FanRequest r, FanState f, IGpdFanController controller) =>
{
    if (r.Mode is not null && !FanControlPolicy.IsValidMode(r.Mode))
        return Results.BadRequest(new { error = new { code = "bad_mode", message = "mode must be one of Auto, Quiet, Balanced, Aggressive, Manual" } });
    if (r.Mode is not null) f.Mode = r.Mode;
    if (r.ManualDuty is int d) f.ManualDuty = Math.Clamp(d, 0, 255);
    return Results.Json(new { mode = f.Mode, manualDuty = f.ManualDuty, controllable = controller.Available });
});

app.MapGet("/display", (DisplayService d) => Results.Json(new { brightness = d.GetBrightness() }));
app.MapPost("/display/brightness", (BrightnessRequest r, DisplayService d) =>
{
    d.SetBrightness(r.Level);
    return Results.Json(new { brightness = d.GetBrightness() ?? r.Level });
});

// Refresh-rate switching (REAL — Win32 EnumDisplaySettingsEx / ChangeDisplaySettingsEx).
app.MapGet("/display/refresh", (RefreshRateService r) =>
{
    var info = r.GetInfo();
    return Results.Json(new { current = info.CurrentHz, supported = info.SupportedHz });
});
app.MapPost("/display/refresh", (RefreshRateRequest req, RefreshRateService r) =>
{
    var (info, error) = r.SetHz(req.Hz);
    return Results.Json(new { current = info.CurrentHz, supported = info.SupportedHz, error });
});

// Night mode (REAL — GDI gamma ramp; deliberately NOT Windows Night Light, see NightModeService.cs).
app.MapGet("/display/night", (NightModeService n) => Results.Json(new { on = n.On, warmth = n.Warmth }));
app.MapPost("/display/night", (NightModeRequest req, NightModeService n) =>
{
    var (on, warmth) = n.Set(req.On, req.Warmth);
    return Results.Json(new { on, warmth });
});

// Tablet mode (ADVISORY; write GATED behind GPDFORGE_ENABLE_HARDWARE=1 — see TabletModeService.cs).
app.MapGet("/display/tablet", (TabletModeService t) =>
{
    var s = t.Get();
    return Results.Json(new { convertible = s.Convertible, raw = s.Raw, applied = s.Applied, advisory = s.Advisory });
});
app.MapPost("/display/tablet", (TabletModeRequest req, TabletModeService t) =>
{
    var s = t.Set(req.Enable);
    return Results.Json(new { convertible = s.Convertible, raw = s.Raw, applied = s.Applied, advisory = s.Advisory });
});

// Keyboard backlight (ADVISORY — EC-controlled, no known safe write path; see KeyboardBacklightService.cs).
app.MapGet("/display/keyboard-backlight", (KeyboardBacklightService k) =>
{
    var s = k.Get();
    return Results.Json(new { controllable = s.Controllable, applied = s.Applied, advisory = s.Advisory });
});
app.MapPost("/display/keyboard-backlight", (KeyboardBacklightService k) =>
{
    var s = k.Set();
    return Results.Json(new { controllable = s.Controllable, applied = s.Applied, advisory = s.Advisory });
});

// --- Advanced (hardware-gated): LED/RGB, battery charge limit, undervolt/Curve Optimizer ---------
// All three: real validators/encoders below (unit-tested in core.tests), write GATED behind
// GPDFORGE_ENABLE_HARDWARE=1, and honest applied:false + advisory wherever no verified write path
// exists — which today is every case on this HX370 (see LedService.cs / ChargeLimitService.cs /
// CurveOptimizerService.cs). Never a blind EC/registry/SMU write.
app.MapGet("/led", (LedService led) =>
{
    var s = led.Get();
    return Results.Json(new { mode = s.Mode, color = s.Color, controllable = s.Controllable, applied = s.Applied, advisory = s.Advisory });
});
app.MapPost("/led", (LedRequest req, LedService led) =>
{
    if (!Enum.TryParse<LedMode>(req.Mode, ignoreCase: true, out var mode))
        return Results.BadRequest(new { error = new { code = "bad_mode", message = "mode must be one of Off, Solid, Breathe, Rotate" } });

    LedColor? color = null;
    if (!string.IsNullOrWhiteSpace(req.Color))
    {
        if (!LedColor.TryParse(req.Color, out var parsed))
            return Results.BadRequest(new { error = new { code = "bad_color", message = "color must be #RRGGBB or RRGGBB" } });
        color = parsed;
    }

    var s = led.Set(mode, color);
    return Results.Json(new { mode = s.Mode, color = s.Color, controllable = s.Controllable, applied = s.Applied, advisory = s.Advisory });
});

app.MapGet("/battery/charge-limit", (ChargeLimitService cl) =>
{
    var s = cl.Get();
    return Results.Json(new { percent = s.Percent, available = s.Available, applied = s.Applied, advisory = s.Advisory });
});
app.MapPost("/battery/charge-limit", (ChargeLimitRequest req, ChargeLimitService cl) =>
{
    var s = cl.Set(req.Percent);
    return Results.Json(new { percent = s.Percent, available = s.Available, applied = s.Applied, advisory = s.Advisory });
});

app.MapGet("/undervolt", (CurveOptimizerService uv) =>
{
    var s = uv.Get();
    return Results.Json(new { coCount = s.CoCount, offsetMv = s.OffsetMv, applied = s.Applied, advisory = s.Advisory });
});
app.MapPost("/undervolt", (UndervoltRequest req, CurveOptimizerService uv) =>
{
    var s = uv.Set(req.CoCount, req.OffsetMv);
    return Results.Json(new { coCount = s.CoCount, offsetMv = s.OffsetMv, applied = s.Applied, advisory = s.Advisory });
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

// Auto-tuner: sweeps STAPM and picks the best point for a goal (ForgeWorker steps the sweep each
// tick — see TunerState.Tick). Honesty gate: on hardware without FPS telemetry wired yet (this
// HX370, today), a sweep runs but records nothing usable, so `best` stays null with a `note`
// explaining why rather than a faked reading — see core/Tuner/TunerState.cs.
app.MapGet("/tuner", (TunerState tuner) => Results.Json(new
{
    running = tuner.Running, goal = tuner.Goal.ToString(), targetFps = tuner.TargetFps,
    minW = tuner.MinW, maxW = tuner.MaxW, tempCapC = tuner.TempCapC, currentStapmW = tuner.CurrentStapmW,
    points = tuner.Points, best = tuner.Best, note = tuner.Note,
}));
app.MapPost("/tuner/start", (TunerStartRequest req, TunerState tuner) =>
{
    if (!Enum.TryParse<TuneGoal>(req.Goal, ignoreCase: true, out var goal))
        return Results.BadRequest(new { error = new { code = "bad_goal", message = "goal must be one of MaxFps, BestEfficiency, HoldTarget" } });

    tuner.Start(goal, req.TargetFps, req.MinW, req.MaxW, req.TempCapC);
    return Results.Json(new
    {
        running = tuner.Running, goal = tuner.Goal.ToString(), targetFps = tuner.TargetFps,
        minW = tuner.MinW, maxW = tuner.MaxW, tempCapC = tuner.TempCapC, currentStapmW = tuner.CurrentStapmW,
        points = tuner.Points, best = tuner.Best, note = tuner.Note,
    });
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

// System health check / anomaly detection: pure rules (GpdForge.Health.HealthCheck) evaluated
// against a REAL live snapshot. Catches things like this unit's parked-fan-while-warm state, a
// firmware that's silently reverting TDP, or a critical-temp / high-discharge condition.
app.MapGet("/health/check", async (ITelemetryService t, CancellationToken ct) =>
{
    var snapshot = await t.ReadAsync(ct);
    var report = HealthCheck.Evaluate(snapshot, new HealthContext());
    return Results.Json(report);
});

// Settings backup / restore: a straightforward aggregation over the existing services/state above
// (no new persistence layer). Import is tolerant — each section applies only if present, unknown
// JSON fields are ignored by the default deserializer, and every value still goes through the same
// clamping/merge the section's own POST endpoint uses.
app.MapGet("/settings/export", (GuardianService guardian, FanState fan, DisplayService display, PowerSourceState powerSource, AutoFpsState autoFps) =>
    Results.Json(new
    {
        modePresets = ModeProfiles.Map.ToDictionary(k => k.Key, v => new { stapmW = v.Value.StapmW, fastW = v.Value.FastW, slowW = v.Value.SlowW, tctlC = v.Value.TctlC }),
        guardian = new
        {
            enabled = guardian.Config.Enabled, autoThrottle = guardian.Config.AutoThrottle,
            tempThrottleC = guardian.Config.TempThrottleC, tempCriticalC = guardian.Config.TempCriticalC,
            throttleFloorW = guardian.Config.ThrottleFloorW, batteryLowPct = guardian.Config.BatteryLowPct,
            batteryCriticalPct = guardian.Config.BatteryCriticalPct,
        },
        fanMode = fan.Mode,
        brightness = display.GetBrightness(),
        powerSource = new { enabled = powerSource.Config.Enabled, onBatteryMode = powerSource.Config.OnBatteryMode, onAcMode = powerSource.Config.OnAcMode },
        autoFps = new { enabled = autoFps.Enabled, targetFps = autoFps.TargetFps },
    }));

app.MapPost("/settings/import", (SettingsImportRequest req, GuardianService guardian, FanState fan, DisplayService display, PowerSourceState powerSource, AutoFpsState autoFps) =>
{
    var applied = new List<string>();

    if (req.ModePresets is not null)
    {
        foreach (var (presetMode, edit) in req.ModePresets)
        {
            if (string.IsNullOrWhiteSpace(presetMode) || edit is null) continue;
            ModeProfiles.Set(presetMode, new GpdForge.Tdp.TdpProfile(edit.StapmW, edit.FastW, edit.SlowW, edit.TctlC));
        }
        applied.Add("modePresets");
    }
    if (req.Guardian is not null)
    {
        var r = req.Guardian; var c = guardian.Config;
        guardian.Configure(c with
        {
            Enabled = r.Enabled ?? c.Enabled,
            AutoThrottle = r.AutoThrottle ?? c.AutoThrottle,
            TempThrottleC = r.TempThrottleC ?? c.TempThrottleC,
            TempCriticalC = r.TempCriticalC ?? c.TempCriticalC,
            ThrottleFloorW = r.ThrottleFloorW ?? c.ThrottleFloorW,
            BatteryLowPct = r.BatteryLowPct ?? c.BatteryLowPct,
            BatteryCriticalPct = r.BatteryCriticalPct ?? c.BatteryCriticalPct,
        });
        applied.Add("guardian");
    }
    if (FanControlPolicy.IsValidMode(req.FanMode)) { fan.Mode = req.FanMode!; applied.Add("fanMode"); }
    if (req.Brightness is int level) { display.SetBrightness(level); applied.Add("brightness"); }
    if (req.PowerSource is not null)
    {
        var r = req.PowerSource; var c = powerSource.Config;
        powerSource.Config = c with
        {
            Enabled = r.Enabled ?? c.Enabled,
            OnBatteryMode = string.IsNullOrWhiteSpace(r.OnBatteryMode) ? c.OnBatteryMode : r.OnBatteryMode,
            OnAcMode = string.IsNullOrWhiteSpace(r.OnAcMode) ? c.OnAcMode : r.OnAcMode,
        };
        applied.Add("powerSource");
    }
    if (req.AutoFps is not null)
    {
        autoFps.Enabled = req.AutoFps.Enable;
        if (req.AutoFps.TargetFps > 0) autoFps.TargetFps = req.AutoFps.TargetFps;
        applied.Add("autoFps");
    }

    return Results.Json(new { applied });
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
    public sealed record RefreshRateRequest(int Hz);
    public sealed record NightModeRequest(bool On, int? Warmth);
    public sealed record TabletModeRequest(bool Enable);

    // --- Advanced (hardware-gated): LED/RGB, battery charge limit, undervolt/Curve Optimizer ---
    public sealed record LedRequest(string? Mode, string? Color);
    public sealed record ChargeLimitRequest(int Percent);
    public sealed record UndervoltRequest(int? CoCount, int? OffsetMv);

    /// <summary>Mode is Auto/Quiet/Balanced/Aggressive/Manual; ManualDuty (0..255) is the fixed duty
    /// used only while Mode == "Manual" — see ForgeWorker.cs's fan-control tick.</summary>
    public sealed class FanState { public string Mode { get; set; } = "Auto"; public int ManualDuty { get; set; } = 128; }
    public sealed record FanRequest(string? Mode, int? ManualDuty);
    public sealed record FreezerRequest(string? Name);
    public sealed record AutoFpsRequest(double TargetFps, bool Enable);
    public sealed class AutoFpsState { public bool Enabled { get; set; } public double TargetFps { get; set; } = 60; public int CurrentStapm { get; set; } = 25; }
    public sealed record GuardianRequest(bool? Enabled, bool? AutoThrottle, double? TempThrottleC, double? TempCriticalC, int? ThrottleFloorW, int? BatteryLowPct, int? BatteryCriticalPct);

    // --- Auto-tuner (POST /tuner/start body) — see core/Tuner/TunerState.cs for the rest. ---
    public sealed record TunerStartRequest(string? Goal, int? TargetFps, int? MinW, int? MaxW, int? TempCapC);

    // --- Migration + config: per-power-source auto mode-switch + settings backup/restore ---
    /// <summary>Mutable holder for the per-power-source config, alongside FanState/AutoFpsState.</summary>
    public sealed class PowerSourceState { public PowerSourceConfig Config { get; set; } = new(); }
    public sealed record PowerSourceRequest(bool? Enabled, string? OnBatteryMode, string? OnAcMode);

    /// <summary>Tolerant settings-restore payload: every section is optional, and each one that IS
    /// present is applied through the same logic its own POST endpoint uses (see
    /// POST /settings/import). Unknown top-level JSON fields are ignored by the default
    /// deserializer, matching the "apply what's present, ignore unknown" contract.</summary>
    public sealed record SettingsImportRequest(
        Dictionary<string, ProfileEdit>? ModePresets,
        GuardianRequest? Guardian,
        string? FanMode,
        int? Brightness,
        PowerSourceRequest? PowerSource,
        AutoFpsRequest? AutoFps);

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
