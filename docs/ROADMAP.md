# GPD Forge — Roadmap

Living roadmap. Phases are sequential; each ends with a green verification gate
(build → types → lint → tests ≥80% → security → Playwright E2E).

## Phase 0 — Scaffolding (current)
- [x] Repo skeleton, GPL-3 LICENSE, README, NOTICE, .gitignore
- [x] `core/` .NET service + local HTTP API (Kestrel) serving real telemetry
- [x] `ui/` Tauri 2 + React/Vite — desktop exe builds (5.6 MB), web UI E2E-tested
- [x] CI workflow (dotnet build/test + web build/Playwright)

## Phase 1 — Core + parity (MVP) — *required before we can replace MA/GPDT*
- [x] `Broker/` PawnIO integration + audit log — **audited 2026-08-30 and mostly already done.**
      PawnIO is integrated (`core/Fan/PawnIoEcPort.cs`, `PawnIoFanRpm.cs`), the board matches, and the
      device reads real RPM. `IBroker`/`NullBroker` were never used by any of it; the fan path talks
      to PawnIO directly. What was genuinely missing was the **audit log**, which existed only as a
      sentence in a comment — now `core/Broker/HardwareAuditLog.cs` plus decorators over the fan and
      TDP controllers, and `GET /audit`. Decorators rather than per-call-site logging, so a seventh
      caller cannot forget. `verified` is three-valued: true, false, and **null for a call that cannot
      report failure** (`SetAuto` returns void) — collapsing null into true would make the record
      confidently wrong, which is worse than not keeping one.
- [x] `Tdp/` closed-loop controller + RyzenAdj backend (unit-tested; **gated** behind
      `GPDFORGE_ENABLE_HARDWARE=1` + elevation — no hardware write until approved & EC/SMU verified on device)
- [x] `Fan/` EC read — **the "PARKED" note was stale.** PawnIO was integrated directly rather than
      waiting for LHM-PawnIO, and the daemon reads real RPM on this device (4608 measured
      2026-08-30). Board detection confirmed: G1618-04/"Ver.1.0" -> WinMax2, RpmRead 0x0218.
- [x] `Fan/` boot/resume re-init + hysteresis curves — curves with hysteresis are implemented
      (`core/Fan/FanCurve.cs`) and driven every tick from `ForgeWorker`. The **resume re-init was
      broken and silently so**: `StandbyService` was built against the phase-0 `IFanController`, which
      is registered as `StubFanController` and always will be, so every restore reported "no EC fan
      backend is wired (GPDFORGE_ENABLE_HARDWARE is off)" — while the gate was on, the board matched
      and the rest of the daemon was reading 4608 RPM. Two interfaces for one fan, and the restore
      held the dead one. It now uses `IGpdFanController`, hands the EC back to AUTOMATIC after a
      resume, and **reads the duty back** rather than trusting a void call.

### Driver decision log
- 2026-08-25: chose to **defer live fan RPM** rather than reintroduce WinRing0 or adopt .NET 10-preview.
  Honest caveat: the optional richer telemetry (package watts / temps) currently goes through
  LibreHardwareMonitor 0.9.4, which loads a **Ring0 (WinRing0-family)** driver when hardware access is
  enabled. The **default** telemetry path is driverless WMI. Moving both to PawnIO is the target.

  **Work blocked by this decision** — listed here rather than in the phase it nominally belongs to,
  because a checkbox in a phase list reads as something someone could pick up, and none of these are
  independently actionable. They unblock together, the day a PawnIO-capable LibreHardwareMonitor ships
  stable (or PawnIO is integrated directly):
  - `Fan/` runtime EC read (Phase 1) — DeviceDb and indexed access are done and unit-tested; only the
    driver is missing.
  - `Fan/` boot/resume re-init + hysteresis curves (Phase 1).
  - "Sustained" fan curve for AI mode (Phase 3) — the power half of sustained shaping shipped on
    2026-08-29; the thermal half cannot, because it is a fan **write**.
  - The `fan` step of the resume restore, which today honestly reports
    `"No EC fan backend is wired"` rather than claiming a restore it did not perform
    (confirmed against the live daemon 2026-08-29).
- [x] `Telemetry/` read-only via WMI (battery/AC/discharge/clock/thermal-zone) — verified on device
- [x] `Telemetry/` package power + fan RPM (broker) + FPS (Intel PresentMon, `GPDFORGE_ENABLE_FPS=1`)
- ⛔ `Hid/` ViGEmBus + HidHide + L4/R4 remap with 1024B backup/verify — **blocked on a measured fact,
      not on effort.** The safe-write layer (backup → patch → verify → restore) is written and
      unit-tested, and the device identity is confirmed on hardware (VID_2F24 & PID_0135, 7 PnP
      nodes). What is NOT true is the transport this module was built on.

      Measured 2026-08-30 with the new read-only probes: all three HID interfaces (MI_00/01/02) open —
      with zero-access `CreateFile`, since Windows holds the input ones — and **all three report a
      feature-report length of 0**. The 1024-byte config blob is therefore not reachable via
      `HidD_GetFeature` on this device, so the placeholder offsets cannot be confirmed the way the
      module assumes, and no write may be attempted.

      Landed anyway, because it turns an assumption into a fact and makes the next step cheap:
      `--probe-hid-dump` and `--probe-hid-diff` (read-only), interface enumeration that does not
      depend on localised device names, zero-access open, and device-reported report lengths instead
      of a hard-coded 1024. `WindowsHidConfigDevice.SetConfig` throws rather than no-op, so
      `SafeConfigWriter`'s verify can never compare a blob against itself and call it success.

      **Next step is investigation, not coding:** find out how WinControls actually reaches the pad —
      a different PID in a config mode, a WinUSB interface, or a vendor endpoint. That is a piece of
      work to decide on, not a detail to guess at.
- [~] `Rtss/` single-owner arbitration — **mostly obsoleted 2026-08-30.** The reason this existed was
      the frame cap, and AMD's own FRTC now provides a real one through ADLX (`POST /gpu/frame-cap`),
      with no third-party dependency and nothing to arbitrate. What remains of the item is only the
      **OSD overlay**: if GPD Forge ever draws stats over a game, RTSS is the incumbent owner of that
      hook. Re-scoped rather than done.
- [~] `Api/` HTTP + WebSocket + named pipe — **HTTP is done and is what every client uses.** WebSocket
      and the named pipe have no code and, on measurement, no demand: `docs/api.md` records that the
      daemon polls and that no client consumes a stream. Kept open honestly rather than ticked, but it
      is a "not needed yet", not a gap.
- [x] `Profiles/` focus-process profiles with anti-flapping hysteresis (engine unit-tested;
      `--probe-focus` live; opt-in worker via GPDFORGE_AUTO_PROFILES)
- [x] Importer: migrate `C:\Program Files\Motion Assistant\Profiles\*.ini` → native profiles —
      `core/Import/MotionAssistantImporter.cs`, `POST /import/motionassistant`, a card on the Profiles
      page and 34 test assertions. Was still listed as pending on 2026-08-30; audited and corrected.
- [~] UI: dashboard + per-mode panels shipped — 9 pages (Dashboard/Power/Fan/Controller/Display/Profiles/
      Monitor/System/Settings), light+dark themes, live SVG sparklines, toasts. Browser-QA'd at the Win 4's
      native 1280×800 (both themes).
- [~] **Quick Access Menu overlay** shipped as a web view at `/overlay.html` (own lean Vite entry): gamepad-first
      (D-pad focus + A activate, keyboard mirror), right-docked, always-dark; live header + mode/TDP/fan/FPS-cap/
      brightness/battery/standby. E2E-tested, QA'd. **Launch:** `scripts/overlay-launch.ps1` opens it as a signed
      Edge/Chrome app-window (runs under Smart App Control). PENDING: topmost-over-exclusive-fullscreen + a resident
      hotkey listener bound to a WinControls-mapped Home button (L4/R4/Menu) — or code-sign the Tauri window for a
      native transparent overlay.

### Landed features (software; some mock-backed until the broker/driver land)
- [x] **Battery budget**: runtime estimate from live discharge + what-if projections across power levels
      (`BatteryEstimator`, unit-tested; Dashboard card).
- [x] **Freezer**: suspend/resume background processes with a protected-process guard, via `ntdll`
      `NtSuspendProcess`/`NtResumeProcess` (unit-tested; System page).
- [x] **Auto-TDP-to-FPS**: pure-PID controller (anti-windup) that steers STAPM to hold a target FPS at the
      least power (`FpsTdpController`, unit-tested; Power page; engages in gaming mode once FPS is real).
- [x] **Editable per-mode TDP presets** + **fan preference** + **live brightness** (WMI) wired end-to-end.

## Phase 2 — Standby Doctor
- [~] **Restore TDP + fan on resume event — now automatic** (`ResumeRestoreWorker`). The restore
      logic had shipped with the Standby Doctor, but the only thing that ever called it was a human
      pressing a button, which is the wrong shape: the EC comes back from a suspend uninitialised
      whether or not the panel is open. The resume is detected from clock divergence rather than
      `WM_POWERBROADCAST` — `QueryUnbiasedInterruptTime` does not advance while suspended, so wall
      delta minus unbiased delta is time spent asleep, and a Windows Service has no message pump to
      receive a power broadcast without hosting a hidden window for it. Polls every 5 s: the drain
      sampler's 1-minute tick is right for a drain figure and far too slow to stop a wake running hot
      and silent against an uninitialised EC. Unit-tested against a simulated suspend.
      **HID re-enumeration now has a backend** (`core/Hid/HidReenumerator.cs`): it acts only on a
      controller node Windows itself reports faulted, restarts the USB composite parent via
      `pnputil` (one action covers all seven nodes the pad presents — confirmed on device
      2026-08-29, VID_2F24&PID_0135), and re-reads the device afterwards rather than trusting the
      exit code. A pad that survived the suspend is deliberately left untouched.
- [ ] Fingerprint toggle / **hibernate helper** — rewritten 2026-08-29: there is no S0↔S3 toggle to
      build on this board. `powercfg /a` reports S1/S2/S3 as *unsupported by the system firmware*, so
      the only states available are S0 low-power idle (Modern Standby) and Hibernate. The useful
      control is therefore "hibernate instead of Modern Standby", not a sleep-state switch.
- [x] `powercfg` integration — `/requests` (sleep blockers), `/lastwake` and now **`sleepstudy`**
      (`core/Standby/SleepStudy.cs`). The overnight drain stays **measured** from two real battery
      readings separated by an observed suspend, never extrapolated.

## Phase 3 — Agents / AI (local) mode
- [x] **VRAM/UMA — the item was wrong, so it was rewritten rather than built.** "Reassignment preset"
      cannot be delivered honestly on this board: the UMA split is applied by the BIOS at boot
      (GOP/`_DSM`), there is no verified reversible user-mode write, and poking a vendor ACPI/registry
      value risks a black screen on a device nobody can roll back remotely. Same correction as the
      S0↔S3 helper in Phase 2 — the item was written before anyone checked whether the hardware
      permits it. What shipped instead is **confirmation**: the reading is persisted across runs
      (`VramHistory`), so a BIOS edit is detected across the reboot and reported as *confirmed*
      instead of assumed. ⚠️ `Win32_VideoController.AdapterRAM` is a uint32 that **saturates at
      4095/4096 MB**, so a reading at the ceiling is the ceiling and not a measurement of the split —
      no confirmed delta is ever emitted from one. Still **no write path, by design.**
- [x] **Sustained-CPU power shaping — now enforced, not just computed.** `ProfileShaper` had existed
      and been unit-tested while being called from exactly one place (`GET /ai`), where its result was
      displayed and discarded; the applied profile came straight from the preset map. The default AI
      preset happened to be flat, so nothing looked wrong — but `ModeProfiles.Set` clamped ranges
      without flattening, so one POST to `/profiles/ai` put the boost headroom back. Shaping now lives
      in `ModeProfiles.For`/`Set`, which is where all six callers converge, so the guarantee holds for
      the mode switch, the auto-profile worker, the standby restore and the resume worker alike.
      The Power page drops the fast/slow sliders for this mode rather than offering two controls that
      change nothing.
- ⛔ "Sustained" fan curve — **not an item here.** It is a fan *write*, so it lives under the Phase 1
      driver decision with everything else that decision blocks, and carries no checkbox: there is no
      work to pick up until the driver question is answered. See *Driver decision log* above.
- [x] **Anti-Modern-Standby during inference — now covers inference we did not start.** The hold
      existed but only `JobsState` (GPD Forge's own queue) and the manual toggle ever took one, so a
      hand-started `ollama serve`, LM Studio or training script got nothing. Newly urgent: until
      2026-08-29 `STANDBYIDLE` was *never*, so no unheld run could be suspended; it is now 300 s on
      battery. `InferenceHoldWorker` earns the hold from **sustained CPU work** attributable to a
      watched process — never from mere presence, because an idle `ollama serve` is resident 24/7 and
      holding for it recreates the exact all-night drain removed on 2026-08-29. Ships **observe-only**
      (`GPDFORGE_INFERENCE_HOLD=1` to enforce): the feature gathers the evidence for its own
      enforcement before it is allowed to act. The holding process and its start time are surfaced —
      a machine that will not sleep and will not say why is the complaint this otherwise creates.
      🔴 Fixed on the way: `Win32ExecutionStateSink` called `SetThreadExecutionState` from whichever
      thread-pool thread happened to invoke it. That request is **per-thread** (the API is named
      *SetThread*ExecutionState) and the header claimed "per-process", so engaging on one thread and
      releasing on another left the request standing forever — `holders` reading 0 while the machine
      never slept again. Pre-existing and already shipped; the sink now owns one dedicated thread.
- [x] Job queue + local API endpoints for external agents (`/jobs` with requireAC/maxTempC/window)
- [x] **MCP server exposing telemetry/control** — `mcp/server.mjs`, zero-dep stdio MCP with 15 tools
      (read + closed-loop writes + constraint-gated `submit_job`). Verified end-to-end against the live
      service: `set_mode` → `AppliedVerified`, `submit_job` → running. Lets KRÓNOS/CYBERLEX drive the handheld.

### AMD GPU profiles (added 2026-08-29)
- [x] **Read and write the Radeon 3D settings from C#** — Anti-Lag, Chill, Boost, Image Sharpening and
      the driver's own frame-rate cap, through ADLX's C interface with hand-written vtable offsets
      transcribed from the SDK headers. AMD's documented C# route (SWIG + a C++ compiler + an unsigned
      native DLL) was rejected because that DLL is precisely what Smart App Control blocks here. The
      layout is verified at startup against a fact read independently over WMI, so a misaligned vtable
      is caught before anything is called through it. Verified on device: ADLX 1.5.0.124, and the live
      settings read back correctly.
- [x] **Applied automatically per app** — the profile hangs off the MODE, so the existing per-app rules
      and their hysteresis drive it, and every path that sets a mode applies it. No second matching
      system.
- [x] **Unblocked: the ADLX calls run in a user-session agent.** ADLX cannot be reached from the
      daemon — it is LocalSystem in **session 0**, and ADLX needs the display driver stack of an
      interactive session (identical code initialises fine as a user and fails as a service). The
      calls therefore live in `--gpu-agent`, the **same assembly** started in the user's session, so
      no new unsigned binary is introduced for Smart App Control to refuse. The daemon holds only what
      the agent reports, and says so: reports carry the agent's read time, anything older than 30 s is
      returned but marked unusable, and "no agent has reported yet" is a distinct status from "the
      agent says ADLX is unavailable".
      **Verified end to end on device 2026-08-30:** ADLX 1.5.0.124, `available:true`, and in `battery`
      mode Chill reads enabled at 60 — which is exactly what the battery profile applies.
      ⚠️ The daemon must never hold an ADLX handle. It briefly did, and a second handle's
      `ADLXTerminate` invalidated the first one's pointers, crashing the service with an access
      violation. **An `AccessViolationException` is not catchable in .NET** — the `try/catch` around
      those interop calls reads like containment and provides none. The only containment is not making
      the call from there.

### Desktop shell
- [x] **Closing the window hides it to the tray** rather than exiting, with a tray icon whose left
      click toggles the window and whose menu is on right click. Confirmed by the user on 2026-08-30.
      The tray "quit" closes the WINDOW and is labelled as such: the thing controlling the handheld is
      the Windows service, and TDP, fan and profile enforcement continue with nothing on screen.

## Phase 4 — Replacement
- [x] **Conflict guard**: detection + **auto-yield** done — `ProfileApplier` skips the TDP write while
      MotionAssistant / GPD Tool are running (field-confirmed clash), so GPD Forge takes over power only
      when it is the sole controller. `install-gpd-forge.ps1 -Substitute` stops/disables the incumbents.
- [x] **Auto-TDP per mode**: the active mode (auto or manual) applies its TDP preset through the closed
      loop, guarded by the conflict check. Unit-tested.
- [x] **Uninstall/disable MA + GPDT services safely — and, now, undo it.** `-Substitute` stopped the
      incumbents, disabled `GPDToolService`, renamed their `Run` keys and disabled their tasks. It was
      written to be reversible (renaming rather than deleting) but **nothing could reverse it**, and
      `-Uninstall` removed GPD Forge while leaving all of that in place: a user uninstalls and is left
      with **no power controller at all**, no message saying why, and no route back. A change that is
      only reversible in principle is not reversible. `-Restore` is that mechanism, `-Uninstall` runs
      it first, and `-Substitute` now records the prior service start type under `%ProgramData%`
      (which `-Uninstall` does not delete) so the undo restores what was there instead of a guess —
      and says so plainly when no record exists.
      `-DryRun` rehearses the undo without writing, because handing a power controller back is not a
      step to learn the behaviour of by running it. That rehearsal immediately earned itself: it
      caught a `-replace` precedence bug that left the renamed key unchanged, and a `schtasks`
      `NativeCommandError` that aborted the whole run on a machine with no GPD scheduled tasks.
- [ ] Take ownership of RTSS / driver / autostart — **autostart is done** (installer registers the
      service and the tray shortcut, and `-Substitute`/`-Restore` own the incumbents' autostart).
      **RTSS is not started and has no code**: `Rtss/ single-owner arbitration` is still open in Phase 1
      too, and nothing in `core/` references RTSS. Audited 2026-08-29 — the checkbox covered three
      unrelated things, of which only one existed.
- [ ] Firmware-update assistant with preconditions — **not started, no code.** Relevant on this device:
      the G1618-04 is on BIOS **0.10 (Nov 2024)**, and the intermittent hibernation resume failure of
      2026-08-29 leaves no crash dump, which places it *before* Windows takes control — bootloader or
      firmware. Any assistant must treat a BIOS update as the irreversible, user-authorised step it is.
- [~] First public OSS release (semantic version + changelog) — **the versioning half landed
      2026-08-29**: one declared version in `Directory.Build.props`, read from the assembly by
      `/health` and `/version`, enforced across `package.json`/`tauri.conf.json` by `VersionModelTests`,
      and consumed by the update checker (which previously compared releases against a literal default
      that nobody would ever bump). `CHANGELOG.md` is maintained. What remains is the release act
      itself: tag, release notes, and a published artefact.

## Phase 5 — Finish what is nearly done  *(low risk, no blockers)*

Everything here is reachable today; none of it waits on a driver or a decision.

- [ ] **Wire the overlay's "FPS cap" to the real cap.** It currently calls auto-FPS, which steers TDP
      toward a target — a control whose label promises something it does not do. Point it at
      `POST /gpu/frame-cap` and rename the auto-FPS control to what it is.
- [ ] **Frame-cap control in the UI**, with the driver's reported range (15–1000 here) as the bounds.
- [ ] ⚠️ **Decide what happens when auto-FPS and FRTC are both on.** One steers watts to reach a frame
      rate while the other refuses to exceed one; together they can chase each other. This needs a
      rule, not a discovery in the field.
- [ ] **Resident overlay hotkey + topmost over exclusive fullscreen** — `scripts/overlay-hotkey.ps1`
      and `forge-hotkeys.ps1` exist; the binding to a WinControls Home button does not.
- [ ] **First public OSS release.** The versioning half landed 2026-08-29 (one declared version,
      `/version`, drift enforced by tests). What remains is the release act: tag, notes, artefact.

## Phase 6 — The broker  *(unblocks the fan, and is the single biggest blocker in the project)*

`core/Broker/` is still `IBroker` + `NullBroker`. Everything fan-related waits on it, and it is worth
doing as one piece precisely because four separate items unblock together.

- [ ] **PawnIO integration + audit log** — directly, or via a PawnIO-capable LibreHardwareMonitor if
      one ships stable. Never WinRing0.
- [ ] Then, in order: `Fan/` runtime EC read → boot/resume re-init + hysteresis curves → the AI mode's
      sustained fan curve → the `fan` step of the resume restore, which today honestly reports that no
      EC backend is wired.
- ⚠️ This is the phase where a mistake spins a fan wrong. The existing double gate
      (`GPDFORGE_ENABLE_HARDWARE` **and** `GPDFORGE_ENABLE_FAN_CONTROL`) and the probe-with-auto-restore
      pattern already established for PWM stay mandatory.

## Phase 7 — The controller  *(needs hardware work, no external blocker)*

- [ ] **Real HID byte offsets.** The safe-write layer (backup → patch → verify → restore) is written
      and unit-tested, and the device identity is confirmed on hardware (VID_2F24 & PID_0135, seven PnP
      nodes). The offsets are still placeholders, so nothing may be written until they are established
      against the real report descriptor.

## Phase 8 — Standby and firmware  *(each needs a decision before code)*

- [ ] **Hibernate helper.** There is no S0↔S3 toggle on this board — firmware reports S1/S2/S3
      unsupported — so the only useful control is "hibernate instead of Modern Standby".
- [ ] **Firmware-update assistant with preconditions.** Not started. This device is on BIOS 0.10
      (Nov 2024), and the intermittent hibernate-resume failure leaves no crash dump, placing it before
      Windows takes control. Any assistant must treat a BIOS update as the irreversible,
      explicitly-authorised step it is.

### Sequencing, and why

Phase 5 first because it is the only phase with no blockers and it closes controls that currently
mislead — a mislabelled switch costs trust every time someone uses it. Phase 6 next because it is the
largest single unblocking, and four parked items move together the day it lands. Phase 7 and 8 are
independent of both and can be reordered freely; 8 in particular is gated on decisions rather than on
work.

## Non-goals (for now)
- Non-GPD handhelds (design leaves room, but not a target).
- A commercial/closed edition (GPL-3 by choice).
