# Changelog

All notable changes to GPD Forge are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [Unreleased]

### Added
- **A version model with one source of truth, and `GET /version`.** The version used to be a
  hand-typed literal in four independent places, with nothing keeping them equal and nothing failing
  when they drifted. One of those places was `UpdateService`'s `currentVersion` **default parameter** —
  and DI took the default, so the daemon compared every GitHub release against a constant nobody would
  ever bump. It would have kept offering an update that was already installed. Every unit test passed
  a version explicitly; production was the sole caller taking the default, which is the worst possible
  place for that mistake to hide.

  `<GpdForgeVersion>` in `Directory.Build.props` is now the only declaration. It feeds the assembly;
  `/health` and the new `/version` read the assembly; `UpdateService` *requires* the version, so
  forgetting to supply it is a compile error rather than a wrong answer. `ui/package.json` and
  `ui/src-tauri/tauri.conf.json` keep copies because npm and Tauri each demand their own field, and
  `VersionModelTests` asserts all three equal — drift is a failing build, not a slow surprise.

  `/version` also reports the commit and the build timestamp, both **nullable and null when the build
  did not record them**. Deterministic builds put a content hash in the PE timestamp field, which read
  as unix seconds yields a confident, plausible, wrong date; implausible values are rejected rather
  than shipped as a date, because the entire value of that field is that it can be trusted.

- **Settings ▸ About now shows the shell build, the daemon build, and says when they disagree.** This
  is the point of the whole change. On 2026-08-28 the app showed no telemetry while the daemon was
  healthy the entire time — the shell in Program Files predated the commit that fixed it, and
  establishing that took diffing the installed binary against a fresh build hunting for marker
  strings. Nothing on screen could say which build was on screen. Now it can, and agreement stays
  silent so the warning keeps its meaning.

### Changed
- **The AI mode's sustained power shaping is now enforced instead of merely calculated.**
  `ProfileShaper` collapses fast/slow boost onto one flat ceiling for a good reason — boost above
  sustained STAPM buys no throughput once a workload is continuously CPU-bound, it only adds heat,
  fan noise and thermal cycling. It had existed, been unit-tested, and been called from exactly one
  place: `GET /ai`, where its result was rendered and thrown away. The profile that actually reached
  the silicon came straight from the preset map.

  Nothing looked wrong, because the default AI preset is written flat by hand. But nothing was
  *keeping* it flat: `ModeProfiles.Set` clamped ranges without flattening, so a single
  `POST /profiles/ai` put the boost headroom back and the shaper was not in the path to stop it.

  Shaping now lives in `ModeProfiles.For`/`Set` — the point where all six callers converge — so the
  guarantee covers the mode switch, the auto-profile worker, the standby restore and the resume
  worker at once, rather than one call site. It flattens on the way in as well as out, so
  `GET /profiles` cannot report a boost that will never be applied. The user still sets the sustained
  ceiling; only the headroom above it is removed, and `POST` answers with `sustained: true` so a
  client can say why the numbers came back changed.

  The Power page no longer renders fast/slow sliders for this mode. Two controls a user can drag
  that change nothing are worse than their absence, so they are replaced by a sentence explaining
  the trade — and the sliders re-seed from the daemon's reply rather than from what was posted.

### Fixed
- **A Smart App Control block on the service DLL uninstalled the daemon instead of failing the
  publish.** SAC judges each unsigned binary individually and inconsistently — on 2026-08-29 the same
  source produced a build it allowed and, minutes later, one it refused. A refused *service* binary
  does not fail `dotnet publish`; it fails `Start-Service` six steps later with an error naming no
  cause, and by then step 1 has already unregistered the service and overwritten the binary that
  worked. `update-shell.ps1` has verified the shell this way since it existed; the service had no
  such guard, which is precisely the failure that hit.

  `install-gpd-forge.ps1` now loads the published assembly before continuing, and on a block rebuilds
  with `-p:Deterministic=false` — a plain retry is useless, because a deterministic build reproduces
  the identical hash and therefore the identical verdict. Three details the first two attempts at this
  guard got wrong, each of which made it silently useless:
  - `Start-Process -FilePath 'dotnet'` does not resolve a bare command name through `PATH` the way the
    call operator does. The launch failed, the failure was caught, and "could not run the test" was
    indistinguishable from "the test passed" — so a blocked binary sailed through.
  - Only `0x800711C7` counts as blocked. Any other non-zero exit means the assembly *loaded* and
    failed for an unrelated reason, and rebuilding over that would hide a real fault behind a retry.
  - The check is retried: SAC's verdict on a freshly written binary is a cloud lookup, and the same
    file can be refused on one load and accepted seconds later.

### Added
- **The resume restore's last empty step now does something.** `hid` has reported
  `restored: false — no backend yet` since the Standby Doctor shipped; it now re-enumerates the
  controller (`core/Hid/HidReenumerator.cs`).

  The interesting constraint is what it must *not* do. Restarting the pad on every wake would yank a
  working controller out from under a running game — worse than the fault being repaired — so it acts
  only on a node Windows itself reports faulted (`ConfigManagerErrorCode != 0`) and otherwise reports
  success *because* it did nothing. When it does act it restarts the USB composite parent: confirmed
  on hardware, the pad presents as **seven** PnP nodes (VID_2F24 & PID_0135 — which also settles the
  `// verify on HX370 Win 4` TODO left in `GpdButtonMap`), and one parent restart re-enumerates all of
  them. Afterwards it re-reads the device, because `pnputil` exits cleanly for a restart that left the
  node exactly as faulted as it was.

  Device identity comes from `PNPDeviceID` and `ConfigManagerErrorCode` — an ID and a number. Nothing
  keys on device names or status text: on this machine the pad is called "Dispositivo definido por el
  proveedor compatible con HID", and a name-matching implementation would report a missing controller
  on any non-English Windows, which is the same trap that produced six phantom sleep blockers.

  `pnputil` was chosen over SetupAPI/CfgMgr32 P/Invoke: an in-box command keeps the layer testable
  behind `IProcessRunner`, and a resume path is the last place to hand-roll native device calls.
- **The sleep study findings now reach the panel.** Parsing them was only half the job: until now the
  only way to see that the machine had hibernated and never come back was `--probe-sleepstudy` from a
  console, which is not where anyone looks after power-cycling a handheld by hand. `GET /standby`
  gains `sleepStudy` + `sleepStudyError`, and the Standby Doctor panel renders failed resumes,
  bugcheck stop codes and the worst measurable drain.

  The report is **never generated on the request path** — it costs tens of seconds and ~9 MB.
  `SleepStudyWorker` samples it two minutes after start (not at start: the daemon comes up with the
  machine, and generating a sleep study while Windows is still starting services would compete with
  the boot it exists to observe) and then every 12 h, into a cache the endpoint reads.

  The wire format carries three states that clients must not collapse: `sleepStudy` and
  `sleepStudyError` both null means the sampler has not run yet; an error means powercfg refused (it
  needs elevation); a summary with no findings means it ran and found nothing. Treating a refusal as
  a clean report would tell the user their machine is healthy on no evidence at all.

### Fixed
- **An absent `sleepStudy` field rendered as a failure with no reason.** The panel tested
  `sleepStudyError !== null`, and a daemon predating these fields omits them entirely — `undefined
  !== null` is true, so an older build produced an empty "unavailable" badge instead of "not sampled
  yet". Caught by the visual baseline, not by the DOM assertions: the E2E asserting on findings
  passed the whole time, because it ran against the mock daemon while the visual spec's own stub
  still had the old shape.
- **`powercfg /sleepstudy` is parsed, so the Standby Doctor can finally explain a machine that went
  to sleep and never came back** (`core/Standby/SleepStudy.cs`). On the reference Win 4 the System
  event log had recorded *no* standby transition at all for the night in question, while the sleep
  study held the whole session, a `0x133` DPC_WATCHDOG_VIOLATION bugcheck two days earlier, and every
  abnormal shutdown of the week.

  The report defeats the obvious implementation three times over. Its `<table>` elements are
  client-side templates full of `${$Scope.Foo}` placeholders, so scraping the HTML returns the
  scaffolding and none of the data — everything lives in one `var LocalSprData = {…}` blob (whose
  keys stay English on a localised Windows, because the markup is the part that gets translated).
  That blob is a *JavaScript object literal*, not JSON: a handful of values are single-quoted
  (`{"Value":'0x0'}`), which `System.Text.Json` rejects — and the first one sits ~180 KB in, so a
  JSON-only parser passes a short fixture and dies on a real report. The payload is therefore
  extracted by a string-aware scanner that normalises single-quoted literals as it goes, which also
  keeps a brace inside a process name or path from ending the scan early.

  Third and least obvious: **battery drain is not meaningful for every session type.** The report's
  own script restricts discharge to Active/Screen-Off/Modern-Sleep sessions with both full-charge
  readings present, and those rules are mirrored here rather than reinvented. Subtracting the
  capacities of a Hibernate session yields a confident four-figure milliwatt number that means
  nothing: the machine is off, an exit capacity of 0 alongside a full-charge capacity of 0 is the
  *absence* of a reading rather than an empty battery, and the session is timestamped to when the
  user pressed power, not to when the machine stopped drawing.

  What it reports instead is what a user actually asks: bugcheck stop codes, abnormal shutdowns, and
  **failed resumes** — a suspend immediately followed by an abnormal shutdown. That last one is an
  inference from adjacency rather than a field the report provides, and is documented as such; it is
  also what distinguishes "it slept and never woke up" from "it crashed while I was using it".
  Exposed as `--probe-sleepstudy [report.html]`, which re-reads an existing report so the parser can
  be exercised against a real multi-megabyte one without elevation.

### Fixed
- **Six imaginary sleep blockers on every non-English Windows.** `powercfg /requests` prints a
  "nothing here" sentinel under each category, and the parser skipped only the English literal
  `"None."` — so a Spanish install reported `Ninguna.` six times as six reasons the machine could not
  sleep. Matching the translated word instead would have been a lottery per language: powercfg
  localises that sentinel while localising the category headers only *inconsistently* (the same
  machine prints `DISPLAY:`, `SYSTEM:` and `AWAYMODE:` in English but `EJECUCIÓN:` in Spanish). The
  sentinel is now recognised structurally — it is a lone word, whereas every real request is a tag
  plus a driver name, service name or path and so contains whitespace. That test direction also fails
  safe: an unfamiliar line is reported rather than swallowed, so the worst case is one blocker too
  many, never a hidden one.

### Added
- **Waking the machine now restores the fan and the power limits by itself.** `RestoreAsync` has
  existed since the Standby Doctor landed and the only thing that ever called it was a human pressing
  a button on the Standby panel — the wrong shape for the failure it prevents, since the EC comes back
  from a suspend uninitialised whether or not anyone is looking, and re-applying power limits against
  an uninitialised EC is how the Win 4 ends up hot and silent. A resume is now detected and repaired
  without a human (`core/Standby/ResumeRestoreWorker.cs`).

  The resume is detected from clock divergence, not from `WM_POWERBROADCAST`: a Windows Service has
  no message pump, and receiving a power broadcast would mean hosting a hidden window purely to be
  told something two clock reads already prove. `QueryUnbiasedInterruptTime` does not advance while
  the system is suspended — including S0ix, where `TickCount64` keeps counting — so wall-clock delta
  minus unbiased delta is time genuinely spent asleep.

  `ResumeDetector` deliberately does **not** reuse `StandbyDrainTracker`, which already computes the
  same difference. Its gates are correct for a drain figure and wrong for a restore: it ignores
  anything under 15 minutes, anything on the charger, and anything where the battery did not drop,
  and the hardware needs restoring in all three of those cases. Sharing the type would have produced
  a restore that silently skipped short sleeps and every resume on mains — so a test asserts the
  detector takes no AC or battery input at all.

  The floor is 60 s of observed sleep (Modern Standby dips in and out for seconds at a time and
  re-initialising the EC on each would be a write storm), and the poll is 5 s rather than the drain
  sampler's minute — the resolution that matters here is how long the machine runs uninitialised
  after waking. HID re-enumeration still has no backend and continues to report `restored: false`
  with the reason. If `QueryUnbiasedInterruptTime` is unavailable the worker says so once and stops
  instead of polling forever to learn nothing; `POST /standby/restore` still works.

### Changed
- **The UI no longer presents controls that cannot reach the hardware as if they could.** LED/RGB,
  the battery charge limit, undervolt/Curve Optimizer and the keyboard backlight all store a setting
  and return `applied: false`, because this HX370's firmware accepts no write on the EC/HID paths
  they need. They used to sit inline on the Power and Display pages among controls that really do
  change the machine, with nothing to tell them apart — which is what made the app feel like a
  mock-up. They now live on a new **Hardware** page beside a capability report that states, per
  feature, what blocks it and what would unblock it, read live from the daemon.
- The **Controller** section is gone. It was a top-level page consisting entirely of disabled
  sliders advertising a feature with no daemon endpoint at all; it is now one honest line on the
  Hardware page. The on-screen-display and GPU placeholders moved there too.
- `GET /health` finally has a consumer. It has been served since the first release and no client
  ever called it, so the app could not tell you which daemon build it was talking to or which board
  had been detected. Both now appear on the Hardware page.
- `ui/src/pages.tsx` (856 lines) is now `ui/src/pages/`, one file per page.

### Fixed
- **One bad field killed the whole app.** `AlertSeverity`/`AlertCategory` are C# enums, and without
  `JsonStringEnumConverter` they went out as ordinals (`"severity":1`) while `docs/api.md`, the mock
  daemon and `ui/src/types.ts` all specify names (`"Aviso"`). The Alerts page called
  `severity.toLowerCase()` on a number, React threw during render, and with no error boundary the
  entire tree unmounted: the window went blank and *nothing* was clickable — which reads as "the app
  does nothing", not "one page is broken". The daemon now serializes enum names, an `ErrorBoundary`
  scopes any future panel failure to that panel, and `AlertsPage` coerces the field defensively.
  Covered by `core.tests/AlertWireFormatTests.cs`, including a test that fails if the converter is
  ever removed as redundant.
- **Reinstalling silently closed the fan gate.** Fan writes need `GPDFORGE_ENABLE_FAN_CONTROL=1` on
  top of the hardware gate, and the installer never wrote it — so it wiped a gate an operator had
  opened by hand and left every fan control inert (`controllable: false`). Now set by default, with
  `-NoFanControl` to opt out.
- `scripts/update-shell.ps1` — replaces just the desktop shell, and **verifies the new binary
  actually runs before overwriting the installed one**. Smart App Control judges each unsigned build
  individually and inconsistently; the old flow could swap a working shell for a blocked one.
- **Telemetry was invisible in the native window.** The installed shell binary predated the fix that
  makes the client target the daemon absolutely; inside Tauri the origin is `http://tauri.localhost`,
  so every relative `fetch` 404'd and each tile rendered `--` with no error shown. The root cause was
  packaging, not code: `install-gpd-forge.ps1` copied whatever binary happened to sit in
  `target/release` instead of building one. It now builds the shell, stops a running instance before
  replacing it (a locked image was failing the copy silently), wipes `wwwroot` instead of layering
  stale bundles on top of each other, logs its elevated half, and refuses to finish if
  `scripts/verify-install.ps1` does not pass.
- Removed the `build:desktop` script, whose `set VAR=value &&` form appends a trailing space to the
  value in `cmd.exe` and would have produced an unusable API base. Origin detection at runtime
  (`ui/src/api.ts`) covers both the shell and the browser from one bundle.

### Added
- **Real FPS telemetry** via Intel PresentMon, behind its own `GPDFORGE_ENABLE_FPS=1` gate
  (`core/Telemetry/PresentMonFrameRateProbe.cs`). CSV columns are resolved by name so a PresentMon
  version bump cannot silently misread them; `fps1PctLow` is now populated and exported. With nothing
  presenting, FPS stays 0 meaning "not available" — never a guess. This also revives Auto-TDP-to-FPS
  and the auto-tuner, both of which were dead code behind an `fps > 0` guard that never held.
- `scripts/verify-install.ps1` — checks the service, live telemetry, that the installed shell carries
  the current markers, and that `wwwroot` has no dangling asset references.
- `scripts/fetch-presentmon.ps1` — downloads PresentMon and refuses it unless Windows reports a valid
  Authenticode signature from Intel Corporation.

### Changed
- `docs/api.md` no longer claims a production WebSocket at `/telemetry/stream`; the daemon polls only,
  and that endpoint exists in the mock alone.

## [0.1.0] — 2026-08-26

First tagged release. GPD Forge already **substitutes** MotionAssistant + GPD Tool on a GPD Win 4
(HX370 / G1618-04): it owns TDP through a verified closed loop and serves real telemetry.

### Added
- **Daemon-first architecture** — a .NET 9 Windows Service (Kestrel on `127.0.0.1:8787`) exposing a
  local HTTP API and serving the web UI. Runs under **Smart App Control** with no unsigned binary:
  hosted by the signed `dotnet.exe`, UI opened in the browser.
- **Closed-loop TDP** via RyzenAdj — apply → re-read the PM table → retry/backoff → honest `verified`
  flag (replaces MotionAssistant's blind re-apply). Verified on real HX370 hardware.
- **Conflict guard** — GPD Forge yields TDP while another controller (MA/GPD Tool) runs; takes over
  only as sole owner. `install-gpd-forge.ps1 -Substitute` stops + disables the incumbents (reversibly).
- **Real telemetry** — driverless WMI by default (battery/AC/discharge/clock/thermal); optional richer
  package-watts/temps via LibreHardwareMonitor behind `GPDFORGE_ENABLE_HARDWARE=1`.
- **Modes** — Gaming, Agents/AI, Windows, Battery, Standby Doctor, with per-mode TDP presets.
- **Web UI** — 9 pages (Dashboard/Power/Fan/Controller/Display/Profiles/Monitor/System/Settings),
  light + dark themes, live SVG sparklines, toast notifications. Browser-QA'd at the Win 4's native
  1280×800 in both themes.
- **Features** — Freezer (suspend/resume background processes, protected-list guarded), Battery budget
  (runtime + what-if projections), Auto-TDP-to-FPS (PID controller), editable presets, fan preference,
  live WMI brightness, Standby Doctor (drain diagnostics + resume restore).
- **Quick Access Menu overlay** (`/overlay.html`) — gamepad-first, right-docked; live header +
  mode/TDP/fan/FPS-cap/brightness/battery/standby. Launch via `scripts/overlay-launch.ps1` (signed
  browser app-window) + `scripts/overlay-hotkey.ps1` (resident global-hotkey listener).
- **MCP server** (`mcp/server.mjs`) — zero-dependency stdio Model Context Protocol server exposing 15
  tools (telemetry, mode, TDP, fan, auto-FPS, freezer, constraint-gated jobs, standby) so agents can
  drive the handheld. Verified end-to-end against the live service.
- **Standby Doctor** and **HID safe-writer** (backup → patch → verify → restore, anti-brick).
- Node **mock daemon** implementing the API contract, **105** core unit tests (xUnit) and **23**
  Playwright E2E, wired in CI.

### Known limitations
- **Fan RPM / curve control is parked** — the runtime EC read needs a PawnIO-capable
  LibreHardwareMonitor; the stable path is Ring0/WinRing0-only. Fan shows 0 rpm and stays on the BIOS
  auto curve until the driver decision lands.
- **Native desktop `.exe` needs code-signing** to run under Smart App Control — the service + browser
  model is the supported way today. A signing pipeline is prepared (see `docs/signing.md`).
- The scripted controller remap (`gpd-winctl.ps1`) does not work on the HX370 Win 4 (its config
  firmware differs from what pyWinControls supports) — use GPD's official WinControls there.

[0.1.0]: https://github.com/lexlaboratory/gpd-forge/releases/tag/v0.1.0
