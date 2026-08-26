# Changelog

All notable changes to GPD Forge are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

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
