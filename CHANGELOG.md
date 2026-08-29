# Changelog

All notable changes to GPD Forge are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [Unreleased]

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
