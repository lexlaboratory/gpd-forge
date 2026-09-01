# GPD Forge — Roadmap

Living roadmap. Phases are sequential; each ends with a green verification gate
(build → types → lint → tests ≥80% → security → Playwright E2E).

**This file owns two roles and no more**: the **map** (what exists, in which phase, where the code
lives) and the **status** (what is open, blocked, or deliberately dropped — see the section at the
end). It does **not** own *why*: decisions that constrain future work live in
[`docs/adr/`](adr/README.md), and what shipped in which release lives in
[`CHANGELOG.md`](../CHANGELOG.md). One canonical owner per fact; everything else links.

> **Reconciled against the tree on 2026-08-31.** The previous revision ticked work in one phase and
> listed the same work as *"the single biggest blocker in the project"* in another, kept two items
> in two places at once, and carried checkboxes for two things with no code in any form. Every
> checkbox below was re-verified against a file path or a live endpoint. **A claim with no evidence
> does not survive the next pass** — this is the fourth audit in a week to find false checkboxes,
> so treat this document as something to verify, not to cite.

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

### Driver decision — resolved

The reasoning lives in **[ADR-0001: PawnIO, not WinRing0](adr/0001-pawnio-over-winring0-for-ec-access.md)**.
Summary of the outcome, because this section used to be a blocker list and is no longer one:

**The block is lifted, and was lifted before anyone noticed.** The decision of 2026-08-25 deferred
live fan RPM until a PawnIO-capable LibreHardwareMonitor shipped — and PawnIO was *already* embedded
in `LibreHardwareMonitorLib` as `LibreHardwareMonitor.Resources.PawnIo.LpcIO.bin`. `PawnIoEcPort`
loads it by reflection (the loader is `internal`), no separate install, no new binary for Smart App
Control to refuse. Everything this section listed as blocked has since shipped:

| Was blocked | Now |
|---|---|
| `Fan/` runtime EC read | shipped — daemon reads real RPM (4608 measured 2026-08-30, 3328 on 2026-08-31) |
| `Fan/` boot/resume re-init + hysteresis curves | shipped — `core/Fan/FanCurve.cs`, driven every tick from `ForgeWorker` |
| The `fan` step of the resume restore | shipped — reaches `IGpdFanController`; verified by effect (`"the EC responded (duty reads 203)"`) |
| "Sustained" fan curve for AI mode (Phase 3) | **unblocked, not built** — see *Open* at the end of this file |

⚠️ **Still an honest caveat, not a resolved item:** the optional richer telemetry (package watts,
temps) goes through LibreHardwareMonitor, which loads a **Ring0-family** driver when
`GPDFORGE_ENABLE_HARDWARE=1`. The **default** telemetry path is driverless WMI. Moving that half to
PawnIO too remains the target — and it is the surface that has to be ruled out for the
`DPC_WATCHDOG_VIOLATION` bugcheck of 2026-08-28.
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
- [x] `Api/` HTTP — done, and it is what every client uses (panel, overlay, tray, MCP server).
      WebSocket and the named pipe are **dropped**, not pending — see *Dropped* at the end.
- ⊘ `Rtss/` single-owner arbitration — **dropped**, superseded by FRTC. See *Dropped* at the end.
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
- [x] **Hibernate helper** — the item was "fingerprint toggle / S0↔S3 switch"; the firmware reports
      S1/S2/S3 unsupported, so what shipped is the control that actually exists: how long the machine
      idles in Modern Standby before hibernating. `GET`/`POST /standby/hibernate`. Measured on device
      2026-08-30 — on battery, 300 s to standby and 7200 s to hibernate, which is two hours of S0
      drain before the machine finally stops costing anything.
      Reads come from the **registry**, not from `powercfg /q`: that output is localised (here it says
      "Índice de configuración de corriente continua actual") and a parser keyed on those words finds
      nothing the moment the OS language changes. Writes go through powercfg, including
      `/setactive`, because editing the scheme without re-activating it leaves a setting that reads as
      changed and behaves as it was. The new value is read back rather than assumed.
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
- [ ] "Sustained" fan curve for AI mode — **unblocked 2026-08-30, and now genuinely open.** It is a
      fan *write*, so it used to sit under the driver decision with no checkbox. That decision is
      resolved: the write path works and was verified by effect. This is the one item the driver
      question was actually holding, and it is now work someone can pick up. The power half of
      sustained shaping shipped 2026-08-29 (`ProfileShaper`); the thermal half is this.
      ⚠️ Subject to the double gate (`GPDFORGE_ENABLE_HARDWARE` **and** `GPDFORGE_ENABLE_FAN_CONTROL`)
      and the probe-with-auto-restore pattern. This is the phase where a mistake spins a fan wrong.
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
- [x] **Unblocked: the ADLX calls run in a user-session agent.**
      Decision, rejected alternatives and the crash it prevents:
      **[ADR-0002](adr/0002-adlx-runs-in-a-user-session-agent.md)**. ADLX cannot be reached from the
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
- [x] Autostart ownership — installer registers the service and the tray shortcut, and
      `-Substitute`/`-Restore` own the incumbents' autostart.
      ⊘ The **RTSS** half of this checkbox is dropped, not pending — see *Dropped* at the end. Audited
      2026-08-29: one checkbox covered three unrelated things, of which only one existed.
- [x] Firmware-update assistant — **shipped as a reporter that refuses to flash**, and that is the
      final shape, not a first step. `GET /firmware`, `canAttempt` permanently `false`, no `POST`.
      Reasoning and the rejected alternatives: **[ADR-0003](adr/0003-firmware-assistant-reports-and-refuses.md)**.
      The 2026-08-29 resume failure that motivated it is a real unexplained pre-Windows event and is
      tracked as triage under *Open*, not as a reason to build a flasher.
- [x] First public OSS release — **v0.2.0 is published.** Both halves are done: the version model
      landed 2026-08-29 (one declaration in `Directory.Build.props`, enforced across
      `package.json`/`tauri.conf.json` by `VersionModelTests`) and the release act itself on
      2026-08-30. Details in Phase 5; artefacts and notes in [`CHANGELOG.md`](../CHANGELOG.md).

## Phase 5 — Finish what is nearly done  *(low risk, no blockers)*

Everything here is reachable today; none of it waits on a driver or a decision.

- [x] **The overlay's frame-rate controls are two things again.** Auto-FPS is labelled "FPS target"
      (it steers TDP to REACH a rate) and the driver cap is its own "FPS cap" row wired to
      `POST /gpu/frame-cap` (the driver refusing to EXCEED one). The old single control promised a
      ceiling and delivered a goal.
- [x] **Frame-cap control in the overlay**, hidden entirely when the driver reports no FRTC — on a
      gamepad-first surface an unusable row is one more thing to skip past with the D-pad. A rejected
      cap rolls the control back rather than leaving it showing a value the daemon refused.
- [x] ⚠️ **The auto-FPS / FRTC rule, decided.** Most pairings are fine and some are useful — "aim for
      45, never spike past 60" is a sensible thing to want. Exactly one is pathological: a cap BELOW
      an active target makes auto-FPS raise power forever chasing frames the driver is withholding,
      so the machine runs hot and loud for nothing while no error appears anywhere. That pairing is
      refused, naming both numbers, and **checked on both endpoints** — a rule enforced on one door is
      one you walk around through the other. Refused rather than silently adjusted: quietly moving
      someone's target or cap applies a setting they did not choose and hides which one changed.
- [ ] **Resident overlay hotkey bound to a Home button** — `scripts/overlay-hotkey.ps1` and
      `forge-hotkeys.ps1` exist and the installer can make them resident (`-EnableHotkeys`, opt-in
      because a global hotkey is a claim on a combination the whole machine shares). What does not
      exist is the binding to a WinControls-mapped Home button (L4/R4/Menu). **Open, no blocker.**
- ⛔ **Topmost over exclusive fullscreen** — *split out of the line above on 2026-08-31, because one
      checkbox covering an open item and a blocked one hides both.* Blocked on a decision, not on
      work — see *Blocked* at the end.
- [x] **First public OSS release — v0.2.0 is published.**
      https://github.com/lexlaboratory/gpd-forge/releases/tag/v0.2.0
      `main` fast-forwarded to `b384a73` (27 commits, no divergence, so the tag was already an
      ancestor and did not need moving). Two artefacts: the NSIS installer and the portable zip.
      ⚠️ Worth knowing for the next one: `release.yml` fires on the tag push and had ALREADY created
      and published the release before anyone ran `gh release create` — with the **entire CHANGELOG**
      as its body, header and `[Unreleased]` section included. The notes were replaced by hand. If
      that workflow is going to own releases, it should take one version's section, not the file.

## Phase 6 — The broker  *(closed 2026-08-31: it was already done)*

🔴 **This phase called itself "the single biggest blocker in the project" while every item in it had
shipped.** It is kept as a heading rather than deleted so that anyone who remembers it as the blocker
finds out here that it is not.

Where the work actually lives, verified against the tree on 2026-08-31:

| Phase 6 item | Status | Code |
|---|---|---|
| PawnIO integration | done | `core/Fan/PawnIoEcPort.cs`, `core/Fan/PawnIoFanRpm.cs` — see [ADR-0001](adr/0001-pawnio-over-winring0-for-ec-access.md) |
| Audit log | done | `core/Broker/HardwareAuditLog.cs`, `core/Broker/AuditingControllers.cs`, `GET /audit` |
| `Fan/` runtime EC read | done | daemon reports live RPM on device |
| Boot/resume re-init + hysteresis curves | done | `core/Fan/FanCurve.cs`, driven from `ForgeWorker` |
| The `fan` step of the resume restore | done | fixed 2026-08-30; verified by effect, not by log |
| AI mode's sustained fan curve | **open** | the only one left — tracked in Phase 3 |

The description was wrong about the shape too: `IBroker`/`NullBroker` were never used by any of it.
The fan path talks to PawnIO directly, and the abstraction the phase was named after did not earn
itself.

⚠️ The safety rule survives the phase: fan writes stay behind the double gate
(`GPDFORGE_ENABLE_HARDWARE` **and** `GPDFORGE_ENABLE_FAN_CONTROL`) with probe-and-auto-restore. That
constraint now belongs to the remaining Phase 3 item.

## Phase 7 — The controller  *(needs hardware work, no external blocker)*

- [ ] **Real HID byte offsets.** The safe-write layer (backup → patch → verify → restore) is written
      and unit-tested, and the device identity is confirmed on hardware (VID_2F24 & PID_0135, seven PnP
      nodes). The offsets are still placeholders, so nothing may be written until they are established
      against the real report descriptor.

## Phase 8 — Standby and firmware  *(both shipped 2026-08-30)*

- [x] **Hibernate helper** — shipped. **Tracked in Phase 2**, which is where the standby work lives;
      this entry was a duplicate of it and is kept only as a pointer. `GET`/`POST /standby/hibernate`.
      There is no S0↔S3 toggle on this board (firmware reports S1/S2/S3 unsupported), so the control
      that exists is how long the machine idles in Modern Standby before hibernating.
- [x] **Firmware assistant** — `GET /firmware`, and it does NOT update anything, by design. It
      reports what is installed (BIOS 0.10, 2024-11-28, confirmed on device) and states the
      preconditions for updating by hand: on AC, above 50%, no other power tool running, no sleep
      during the flash. `canAttempt` is always false. A daemon that flashed firmware on a handheld
      with no vendor recovery path would be the most dangerous thing in this repository by a wide
      margin, and an assistant that implied it might is not much better.

---

# Open, blocked, and dropped

**This section is the status role, and it is the one that rots.** Everything above is the map: what
exists and where. Below is what is actually left. If the two disagree, this section is wrong and the
tree is right — re-verify before planning against either.

Current baseline: **v0.2.0 published**, phases 0–6 and 8 closed, `origin/main` at `f9d5687`.
The sequenced plan for what comes next is
[`superpowers/plans/2026-08-31-post-0.2.0-phase-plan.md`](superpowers/plans/2026-08-31-post-0.2.0-phase-plan.md).

## Open — work someone can pick up today

| Item | Where | Note |
|---|---|---|
| Sustained fan curve for AI mode | Phase 3 | Unblocked 2026-08-30. The double gate and probe-with-auto-restore are mandatory. |
| Resident overlay hotkey bound to a Home button | Phase 5 | The listener scripts exist and are installable; the WinControls binding does not. |
| ~~Packaged-shell E2E~~ | done 2026-08-31 | `tests/desktop/` — pywinauto/UIA over the installed binary. Scope is the window layer only: UIA cannot see the WebView2 DOM. **Skips on CI** (no installation there), so it is a post-install check, not a CI gate. |
| ~~Mock↔daemon contract parity~~ | done 2026-08-31 | `tests/contract/api-contract.json` is the arbiter; the real daemon and the mock are each validated against it, never against each other. |
| ~~Charge guard~~ | done 2026-09-01 | `GET`/`POST /battery/charge-guard` — counts hours spent plugged in at high charge, warns once per episode, and optionally holds a cooler ceiling while the pack sits full. **It cannot stop charging and says so**: `canStopCharging` is in the contract as a permanent `false`. Attacks the reachable half of lithium ageing (temperature), since the threshold is an EC/BIOS value with no verified path. Phase G3. |
| ~~EC charge-threshold research~~ | closed 2026-09-01 | **Not available on this board, with evidence** — [ADR-0004](adr/0004-no-charge-threshold-on-this-board.md). No vendor tool implements it (so nothing to observe), ACPI declares no `_BMC`/`_BMD`, `_BTP` only notifies, and the promising `BCTH`/`BCTL` EC fields appear **once each** — declared and never referenced by any AML method. Closing this as "no" was the defined success condition for Phase G5. |
| ⚠️ Check BIOS setup for a charge threshold | *(open, needs Alex, ~10 min)* | The one remaining lead after ADR-0004, and the cheapest open item in the project. Hold `DEL` at boot. If it exists, the firmware has a path ACPI does not expose and the EC dump-and-diff becomes worth building; if it does not, the question is fully settled. |
| ~~Gaming-on-battery mode~~ | done 2026-09-01 | `gaming-battery` — 15 W sustained, Tctl 90, Chill, and a **45 fps FRTC cap**, which is the larger part of what makes it work. Not auto-FPS eligible: a cap plus a target is the pathological pairing. Mode lists consolidated into `core/Profiles/Modes.cs` first, with `ModeCatalogueTests` guarding the C#, TypeScript, UI and mock copies. ⚠️ **The frames-per-watt claim is still unmeasured** — see *Open*. Phase G2. |
| ⚠️ Verify `gaming-battery` against a real game | *(open, needs Alex)* | The preset is derived from measured idle overhead (~9 W) and pack capacity (40 Wh), predicting ~1.6 h against ~1.1 h for `gaming`. That is arithmetic, not evidence. The gate is two equal-length runs of one real game with `/sessions` reporting fps average, 1 % low and battery consumed. If it does not beat `gaming` on frames-per-watt, the numbers get re-derived. |
| ~~Battery health, reported~~ | done 2026-09-01 | `GET /battery/health` — 40,009 of 43,890 mWh (**91.2 %** on the reference device), with a daily sampler so degradation is a trend. Cycle count and cell temperature are `null` with stated reasons: the EC returns 0 cycles for a pack that has lost 8.8 %, and 0 would read as an unused battery. Phase G1 of the pending-features plan. |
| ~~Telemetry reports **0** for sensors it cannot read~~ | done 2026-09-01 | Every sensor field is nullable; null means "no reading", never zero. A **measured** zero is still reported as zero (nothing presenting frames, nothing discharging on AC), so the distinction is per-field, not blanket. Two silent failures surfaced with it: `/health/check` now warns `telemetry_unavailable` instead of answering `ok`, and the thermal guardian says it cannot protect the device instead of quietly declining to fire — in C# `null >= 90` is false, so every threshold had simply stopped mattering. Phase G4. |
| Run the UI against the real daemon | *(deferred, with reasons)* | Attempted 2026-08-31 and **not viable as planned**: against a gates-closed daemon every sensor reads 0, so an assert demanding "a digit, not `--`" passes without proving anything; against the installed daemon the specs mutate real TDP and fan state. Worth doing as a read-only subset once the zero-vs-null item above is settled — the two are the same problem. |

## Blocked — and by what, precisely

| Item | Where | Blocked by |
|---|---|---|
| Real HID byte offsets | Phase 7 | **A measured fact, not effort.** All three pad interfaces report `FeatureReportByteLength = 0`, so the 1024-byte blob is unreachable via `HidD_GetFeature`. Next step is finding how WinControls reaches the pad (config-mode PID? WinUSB? vendor endpoint?) — that is USB tracing, not coding. No write path opens until the transport is known. |
| Overlay topmost over exclusive fullscreen | Phase 5 | **A decision, not work.** Either require borderless-windowed (the user's choice to make, not ours to force) or hook the presentation chain — which is what RTSS exists for, and would resurrect a dependency this project deliberately dropped. Should land as an ADR either way. |
| Overnight drain measurement | Phase 2 | **The environment, not the code.** `lastDrainPctPerHour` is `null` and the only sleep blocker is the agent host process itself. Needs a measurement window with the agent shut down. Until then the drain figure, the `InferenceHoldWorker` evidence and the hibernate policy's real effect are all unfalsifiable. |

## Flaky — seen failing under load, passes in isolation

- ⚠️ `InferenceHoldWorkerTests.A_sampler_that_fails_forever_releases_the_hold_instead_of_freezing_it`
  failed once during a full-suite run on 2026-09-01 and passed 3/3 in isolation and on the next full
  run. Recorded rather than ignored: a test that fails at random teaches people to re-run the suite
  instead of reading it, which is how a real failure gets waved through.

## Triage — reported by the daemon, owned by nobody

`GET /standby` has been returning these since the sleep study shipped. Neither is confirmed to be
ours; both are ours to rule out.

- **Bugcheck `0x133` (`DPC_WATCHDOG_VIOLATION`), 2026-08-28.** A driver held a DPC too long. This
  project loads a Ring0-family driver through LibreHardwareMonitor when hardware access is enabled
  (see [ADR-0001](adr/0001-pawnio-over-winring0-for-ec-access.md) § Consequences).
- **Failed resume, 2026-08-29.** A 5-hour hibernate whose next event was an abnormal shutdown. No
  crash dump, which places it before Windows takes control.

## Dropped — and why, so it is not re-proposed

Items here are **closed by decision**. They are not unticked checkboxes and should never be rewritten
as such: a checkbox reads as work someone could pick up.

- ⊘ **`Rtss/` single-owner arbitration.** The reason it existed was the frame cap, and AMD's own FRTC
  provides a real one through ADLX (`POST /gpu/frame-cap`) with no third-party dependency and nothing
  to arbitrate. Nothing in `core/` has ever referenced RTSS. **Reopen only if** GPD Forge decides to
  draw an OSD over a game, at which point RTSS is the incumbent owner of that hook — which is the
  same question as the fullscreen overlay under *Blocked*.
- ⊘ **`Api/` WebSocket and named pipe.** No code, and on measurement no demand: `docs/api.md` records
  that the daemon polls and no client consumes a stream. HTTP is what the panel, overlay, tray and
  MCP server all use. **Reopen only if** a client appears that a poll cannot serve.
- ⊘ **VRAM/UMA reassignment preset.** The split is applied by the BIOS at boot; there is no verified
  reversible user-mode write, and poking a vendor ACPI/registry value risks a black screen on a
  device with no remote rollback. What shipped instead is confirmation across reboots. Detail in
  Phase 3.
- ⊘ **Firmware flashing.** [ADR-0003](adr/0003-firmware-assistant-reports-and-refuses.md).
  **Reopen only if** a verifiable, signed publication channel for GPD firmware appears.

## Non-goals (for now)
- Non-GPD handhelds (design leaves room, but not a target).
- A commercial/closed edition (GPL-3 by choice).
