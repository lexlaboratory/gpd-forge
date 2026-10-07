# Repair power, thermal feedback and trigger diagnostics

> For agentic workers: execute tasks with Luna agents and independent root integration/review. Use test-driven-development and verification-before-completion.

**Goal:** restore useful, truthful hardware control and establish whether battery shutdowns and L2/R2 failures are software or physical problems.

**Architecture:** preserve the daemon and its existing interfaces. Add optional verification/error fields to current responses; expose the actual fan worker result rather than treating a saved preference as an applied write. Compare Motion Assistant and Forge by transferring ownership reversibly, with backups.

**Tech stack:** .NET 9, React/TypeScript/Vite, Playwright, Windows services and read-only XInput probes.

**Spec:** prior authorized diagnosis `docs/audits/2026-10-06-gpd-thermal-input-review.md` in the main checkout; Alex approved items 1–6 and confirmed L2/R2 analog triggers and sudden shutdown on battery.

## Global constraints

- Base is installed 0.4.0 commit 08fb305, isolated `fix/thermal-input-control` branch.
- GPT Luna builds and handles simple tasks; no available mini model, so no silently invented model routing.
- No BIOS update, controller firmware write, weakened OS protections or deep battery discharge.
- Never lower cooling while hot; transfer fan to firmware auto before leaving a controller.
- One active owner of EC/SMU at a time. Snapshot and restore settings and installed artifacts.
- Missing sensor/readback means unknown; requested watts are not measured package watts.
- Build → types → lint → tests (≥80% coverage for changed logic) → security before installation.
- No release, tag or merge before real-device verification. A physical test not performed remains pending.

## 1. SMU access and backend reliability

Files: `core/Tdp/RyzenAdjBackend.cs`, `ClosedLoopTdpController.cs`, `TdpProfile.cs`; root integrates `TdpState.cs`, `Broker/AuditingControllers.cs`, `Program.cs`.

- [x] Capture `ryzenadj --info` stdout, stderr and ExitCode from elevated context; preserve output as a hardware fixture only if successful.
- [x] Write failing regressions for nonzero exit, stderr flood, cancellation and null readback classification.
- [x] Consume both output streams concurrently; enforce timeout and terminate the child on cancellation; return a bounded error on failed access.
- [x] Append `Error` and `VerificationStatus` to TdpApplyResult, keeping existing callers compatible; classify verified/mismatch/unavailable.
- [x] Publish error/status in GET and POST /tdp, with owner and observed limits unchanged.
- [x] Run focused tests, then confirm real readback before treating thermal throttling as physically applied.

## 2. Battery and thermal shutdown evidence

Files: `scripts/diagnostics/` and `docs/audits/2026-10-06-device-diagnostics.md`.

- [x] Capture battery voltage, remaining/full-charge capacity, AC and shutdown event data with absolute UTC/local timestamps.
- [x] Correlate the four October 1 events with the last available samples; distinguish no-bugcheck events from September 24 0x9F.
- [x] Observe a brief normal-load interval; no stress test or forced discharge.
- [x] In ForgeWorker publish a throttle request as a request, and report a failure if ApplyAsync did not verify; test that no failed apply produces a confirmed-protection message.

## 3. Fan control confirmation

Files: `core/Fan/FanControlState.cs`, `FanWorker.cs`, `Program.cs`, fan tests, FanPage.

- [x] Add thread-safe FanControlState with snapshot: requestedDuty, observedDuty, verified, error, atUtc, mode.
- [x] Record failed/mismatched manual writes and readback. Auto returns unknown unless an independent verification exists; void is never success evidence.
- [x] Return optional `status` from /fan without mutating hardware during GET.
- [x] FanPage polls current status, handles errors visibly, commits selection only after POST, disables conflicting pending writes and displays duty as percent.
- [x] Add red→green tests for rejection, delayed application and mismatched readback. Verify UI at 1280×800 and 380px.

## 4. L2/R2 input

Files: `scripts/diagnostics/trigger-probe.py`, `ui/src/components/TriggerDiagnostics.tsx`, HardwarePage, E2E tests.

- [x] Read XInput triggers without configuration writes; record min/max and duration near saturation across four slots.
- [x] Present browser gamepad diagnostics only for standard mapping, with unsupported/no-controller state explicit.
- [x] Ask Alex to hold each physical trigger to its stop, then release. Expected range 0–255; distinguish low range from game input configuration.
- [x] No recalibration until the input has been physically measured and the correct manufacturer utility is available.

## 5. Understandable power controls

Files: PowerPage, types/api, endpoint regressions.

- [x] Separate saved per-mode preset, requested active ceiling, readback, measured package consumption and owner.
- [x] Display backend failure and unverified state; saving a preset does not claim immediate hardware application.
- [x] Keep errors accessible with role=alert and status updates with role=status; retain keyboard/gamepad navigation.

## 6. Reversible vendor comparison and delivery

Files: diagnostics operations scripts, audit, roadmap/API documentation.

- [x] Back up ProgramData settings and installed service/UI artifacts, service state/startup and power-policy originals.
- [x] Stop Forge cleanly so its finally/dispose path returns fan to auto, and verify process exit before starting Motion Assistant.
- [x] Start Motion Assistant alone and observe whether TDP/fan controls work; record crashes or driver errors. Keep GPDTool disabled during this test.
- [x] Restore previous owner/configuration after the comparison unless measurements establish a better stable controller; document final owner.
- [x] Build .NET/UI, types, formatting/lint, full tests and security/dependency checks; install only validated artifacts with rollback available.
- [x] Verify installed build identity, live statuses and controlled hardware response. Update vault operations/memory and git push.

Baseline focused backend tests: 36 passed, zero failed. Hardware baseline: 65.4 °C, 14.3 W package, 3584 RPM, 95% battery, AC disconnected, TDP unverified. These values describe one sample, not idle performance.

## Execution status — 2026-10-07 before installation

Backend: 1,709 passing tests, 91.74% coverage of added executable .NET lines (95.65% with the separate hardware read-only run); full-suite global coverage 70.30%. The >=80% repair gate applies to changed logic, not the unchanged legacy baseline. NuGet reports no vulnerable dependencies. Analyzer checks passed; pre-existing Program.cs whitespace and one test warning are outside this change.

Physical trigger capture confirmed truncated input (223/181). Manufacturer calibration is not complete: official download returns HTTP 429; the published reseller mirror requires Microsoft authentication or returns a Cloudflare 403 challenge. No bypass or controller firmware write was attempted. The automatic approval review separately rejected opening the manufacturer download page in Edge without a specific reason.

Motion Assistant was tested as the only owner, failed to change the read-back 18/24/22 W limits, and offered no verified manual fan control. Its INI files were restored and Forge resumed as owner. Battery shutdown cause remains unresolved; the October 1 events have no contemporaneous voltage/temperature data.

## Installed verification — 2026-10-07

Repair source 6029655 is installed and destination hashes match all 73 staged files. TDP 10 W verified, Battery 8 W restored with no manual override; fan duty255 verified and Quiet restored. Native Power/Fan reviewed against the real daemon. GPU agent resumed after resolving its DLL lock during install. Automatic profiles selected Windows15 W when AC connected; that readback also verified.

The six workstreams were executed, but two problem outcomes remain open: manufacturer calibration/download and the cause of battery shutdowns. Sustained AC cooling also needs observation. The closed checkboxes describe performed tasks, not claims that those hardware symptoms are cured. Operations/memory synchronization is completed in the closure commit.

## Follow-up authorized on 2026-10-07

Alex instructed continued autonomous execution. Three independent follow-up tasks remain within the repair:

- [x] Observe 15/20/17 W versus a temporary flat 12 W without stress; restore original intent after comparison. Record direct-charger observations separately after Alex removes the Steren dock. Do not infer negotiated USB PD watts from the charger label or battery charge rate.
- [x] Add a bounded, read-only JSONL recorder for voltage/capacity/temperature, actual control status and Windows power events. Flush every record to disk. Run as Alex with limited privileges at logon for up to 24 hours; keep raw logs private and do not delete forensic evidence automatically.
- [ ] Fix discovered preset persistence: `/profiles` currently changes a static map only although API documentation promises persistence. Add a dedicated atomic store under existing DataRoot; load before workers start; validate known modes and central clamping/shaping; save disk before claiming success or changing memory. Use the same store on settings import. Prove save/recreate, corrupt input handling, failed-write behavior and host restart, then build/analyzers/tests/security and verify the installed service after restart.
- [ ] Obtain manufacturer calibrator through its published public download. SoftwinCN WIN4 V1.03 is behind a visible image verification code; Alex must complete that verification. No controller firmware updater or unknown binary substitute. Verify raw XInput again only after actual physical calibration.

Service-only deployment for persistence must preserve the already verified native UI and stop only an exact validated user-session GPU-agent process if it holds the service DLL. Preserve rollback and restore that agent afterward. A lower Windows preset is a mitigation to evaluate, not proof of the shutdown cause.
