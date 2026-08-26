# GPD Forge — Roadmap

Living roadmap. Phases are sequential; each ends with a green verification gate
(build → types → lint → tests ≥80% → security → Playwright E2E).

## Phase 0 — Scaffolding (current)
- [x] Repo skeleton, GPL-3 LICENSE, README, NOTICE, .gitignore
- [x] `core/` .NET service + local HTTP API (Kestrel) serving real telemetry
- [x] `ui/` Tauri 2 + React/Vite — desktop exe builds (5.6 MB), web UI E2E-tested
- [x] CI workflow (dotnet build/test + web build/Playwright)

## Phase 1 — Core + parity (MVP) — *required before we can replace MA/GPDT*
- [ ] `Broker/` PawnIO integration + audit log
- [x] `Tdp/` closed-loop controller + RyzenAdj backend (unit-tested; **gated** behind
      `GPDFORGE_ENABLE_HARDWARE=1` + elevation — no hardware write until approved & EC/SMU verified on device)
- [~] `Fan/` EC read: DeviceDb + indexed-access code done and unit-tested; on-device board detection
      confirmed (G1618-04/"Ver.1.0" -> WinMax2, RpmRead 0x0218). **PARKED (decision 2026-08-25, option 1):**
      the runtime EC read needs a PawnIO-capable LibreHardwareMonitor; the stable NuGet (0.9.4) is Ring0/
      WinRing0-only and the PawnIO builds are prereleases pulling .NET 10-preview deps. Revisit when
      LHM-PawnIO ships stable (or integrate PawnIO directly). No fan writes until then.
- [ ] `Fan/` boot/resume re-init + hysteresis curves (WRITES — gated; blocked by the same driver decision)

### Driver decision log
- 2026-08-25: chose to **defer live fan RPM** rather than reintroduce WinRing0 or adopt .NET 10-preview.
  Honest caveat: the optional richer telemetry (package watts / temps) currently goes through
  LibreHardwareMonitor 0.9.4, which loads a **Ring0 (WinRing0-family)** driver when hardware access is
  enabled. The **default** telemetry path is driverless WMI. Moving both to PawnIO is the target.
- [x] `Telemetry/` read-only via WMI (battery/AC/discharge/clock/thermal-zone) — verified on device
- [ ] `Telemetry/` package power + fan RPM (broker) + FPS (PresentMon) — needs hardware-access approval
- [ ] `Hid/` ViGEmBus + HidHide + L4/R4 remap with 1024B backup/verify
- [ ] `Rtss/` single-owner arbitration
- [ ] `Api/` HTTP+WebSocket + named pipe
- [x] `Profiles/` focus-process profiles with anti-flapping hysteresis (engine unit-tested;
      `--probe-focus` live; opt-in worker via GPDFORGE_AUTO_PROFILES)
- [ ] Importer: migrate `C:\Program Files\Motion Assistant\Profiles\*.ini` → native profiles
- [ ] UI: dashboard + per-mode panels + quick-menu overlay (gamepad-navigable)

## Phase 2 — Standby Doctor
- [ ] Restore TDP + fan + HID on resume event
- [ ] Fingerprint toggle / S0↔S3 helper
- [ ] `powercfg sleepstudy` + `/requests` integration and drain diagnostics

## Phase 3 — Agents / AI (local) mode
- [ ] VRAM/UMA reassignment preset
- [ ] Sustained-CPU power shaping + "sustained" fan curve
- [ ] Anti-Modern-Standby during inference (SetThreadExecutionState / power request)
- [ ] Job queue + local API endpoints for external agents
- [ ] Optional MCP server exposing telemetry/control

## Phase 4 — Replacement
- [x] **Conflict guard**: detection + **auto-yield** done — `ProfileApplier` skips the TDP write while
      MotionAssistant / GPD Tool are running (field-confirmed clash), so GPD Forge takes over power only
      when it is the sole controller. `install-gpd-forge.ps1 -Substitute` stops/disables the incumbents.
- [x] **Auto-TDP per mode**: the active mode (auto or manual) applies its TDP preset through the closed
      loop, guarded by the conflict check. Unit-tested.
- [ ] Uninstall/disable MA + GPDT services safely (installer `-Substitute` covers stop+disable)
- [ ] Take ownership of RTSS / driver / autostart
- [ ] Firmware-update assistant with preconditions
- [ ] First public OSS release (semantic version + changelog)

## Non-goals (for now)
- Non-GPD handhelds (design leaves room, but not a target).
- A commercial/closed edition (GPL-3 by choice).
