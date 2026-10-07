# Power, shutdown recording and calibration follow-up

Measured on 2026-10-07 in the installed 0.4.0 repair; source 6029655 before the persistence follow-up. Alex authorized continued autonomous execution, identified L2/R2 analog range truncation, and reports sudden battery shutdowns. Raw evidence stays under ignored `out/repair-validation/`.

## Normal-use power comparison

At 00:36:52–00:40:55 Mexico City time (06:36:52–06:40:55 UTC), observe normal work with AC connected and Quiet fan control. No benchmark, load generator or battery discharge. The original Windows preset is 15 W STAPM, 20 W fast, 17 W slow, 92°C Tctl. Temporarily set flat 12/12/12 W, then re-select Windows to clear the override and restore the original preset. Readback verifies both requests; fan remains Quiet throughout.

| Phase | Samples, 5-second cadence | Mean CPU | Mean package | Mean RPM |
| --- | ---: | ---: | ---: | ---: |
| Original Windows | 12 | 78.63°C | 15.01 W | 5163 |
| Flat 12 W, whole phase | 36 | 74.35°C | 11.99 W | 4409 |
| Flat 12 W, final 12 samples | 12 | 71.34°C | — | 4181 |

The lower limit and lower RPM were observed on this device; foreground activity was not fixed, so this is not a controlled throughput/acoustic benchmark. Quiet has temperature hysteresis and follows heat; a high duty is not evidence that control is broken. Evidence: `normal-power-comparison-20261007.jsonl` and its metadata, with explicit final restore and manualStapmW=null.

## Dock versus direct charging

Alex identifies a Steren dock, model unknown, connected to HDMI; both charger and dock are described as 100 W. The rating does not establish negotiated power delivered to the GPD. At Alex's confirmation, observe the charger directly connected with the same original Windows profile. 18 samples at approximately 10-second cadence, 06:46:07–06:48:59 UTC: mean CPU80.02°C, package17.00 W, fan5120 RPM, WMI battery charge24.81 W. Battery capacity rose7923→9078 mWh and voltage11947→12027 mV. AC and 15 W readback remained true through the capture.

Dock charging also resumed before this switch (a prior sample was21.529 W into the battery); earlier near-zero charge values are insufficient to blame the dock, charger or EC. Charging rate is battery-side WMI telemetry, not USB PD input or charger negotiation. Direct charging did not remove high CPU heat with the original preset. Evidence: `direct-charger-20261007.jsonl`.

## Preset persistence defect

Confirmed by source review: `/profiles/{mode}` called `ModeProfiles.Set` on an in-memory static dictionary only. The dictionary was seeded from compiled defaults on every service start; `ModeStore` preserves mode selection, not preset values. Settings import similarly changed only memory, despite API documentation claiming persistence. A manual 12 W override also ends at mode selection/reselection or daemon restart. The repair must demonstrate persisted save and restart before describing 12 W as durable.

The implemented store loads sparse, validated overlays before workers start. Saves and imports flush an atomic temporary file before replacing the saved file and changing the locked memory map. Unknown modes return400; disk failures return503 with an `error` field the existing UI can display. Incomplete/out-of-range persisted entries are ignored; corrupt JSON is quarantined. Case aliases canonicalize deterministically and the sustained AI shape is retained. Independent review found no remaining blocking issue.

Frozen-source validation: full Release1722/1722, focused28/28, build with warnings-as-errors passed; root re-runs the full suite before installation. The final collector includes the child daemon. Root's changed-line script measures159/171 executable changed lines =92.98%; the production `GpdForge.Service` package reports80.07% overall. The report-level89.89% also includes test assembly coverage and must not be reported as production coverage. UI build/types/scoped lint passed; fresh npm/NuGet vulnerability reports are clear. Existing Program/ApiStartup whitespace findings outside edited hunks remain explicitly outside this scoped change.

Root independently reran the full suite:1722 passed,0 failed,0 skipped, recorded in `preset-root-tests/presets-root.trx`.

Installed source `e333ec6190e3ed44dbc3e72df2e96b124d35f94c` at07:05 UTC. All61 service manifest destination hashes match; DLL SHA256 `FFE3FE4487E946166460A73E9E8ACD04B6656890DA48E519B65E311C5A06904E`. Native executable remained unchanged with its previously verified hash. The preflight initially rejected a valid signed host because the helper expected CN=Microsoft Corporation; its actual certificate is CN=.NET/O=Microsoft Corporation with Valid Authenticode. Corrected to the measured publisher/certificate before any service mutation. Backup: `C:/ProgramData/GPD Forge Deploy Backups/preset-deploy-20261007-010507-a4ea6596`. Service environment unchanged; exact GPU PID34316 stopped for copy and user-session GPU agent52588 resumed, `/gpu` Ready.

Saved Windows12/12/12 W, requested90°C Tctl, then re-selected Windows to apply it. Restarted the daemon07:06:43 UTC: PID53804→43580, saved-file hash unchanged, `/profiles` reloaded12/12/12/90, new request07:06:44 read back12 W STAPM/12 W PPT verifiedtrue, owner=mode, manualStapmW=null. Fan Quiet restored with confirmed duty readback. Subsequent sample73.5°C/11.8 W/3840 RPM; another sample72.8°C/12 W/3840 RPM. These describe normal usage and a cooling mitigation, not proof of an electrical/thermal shutdown cure. Battery and gaming presets retain their previous values. Native Power page was visually reviewed after restart and showed Windows12/12/12/90 with desired/requested/readback12 W and Verified. Private evidence: `preset-restart-verification.json`, `preset-saved-12w.json`, `preset-persisted-windows-top.png`.

## Shutdown evidence

The follow-up System event observation had no new41/6008/1001/WHEA event in06:20–06:33 UTC. Thermal guardian notices at06:24–06:25 UTC describe requests/recovery, not an observed shutdown. The historical October1 events still lack contemporaneous voltage/temperature. No shutdown reproduction or stress test has been performed.

`scripts/diagnostics/watch-power-events.ps1` passed syntax checks and a real one-minute run:12 samples with all four API and battery statuses `ok`, boot time recorded, event queries explicitly `noEvents`. It distinguishes inaccessible events, offline API and unexpected HTML from valid/no-event data. Each JSONL line is physically flushed. No EC/SMU writes, process control or external transmission. Bounds:32MiB/file,128MiB/folder, maximum24h; previous evidence is retained when full rather than automatically deleted.

Task `GPDForge-PowerDiagnostics` is registered and running as Alex, Limited/Interactive, at logon; it starts immediately for1440min with approximately5-second intervals, and can run on battery. Root verified task principal, trigger, settings and growing file `power-events-20261007-005145-d6cdc47c.jsonl`. No elevated/System recorder. This is bounded per-logon monitoring, not an indefinite service. Remove with `Unregister-ScheduledTask -TaskName GPDForge-PowerDiagnostics -Confirm:$false`; stop with `Stop-ScheduledTask -TaskName GPDForge-PowerDiagnostics`. Private task receipt: `power-recorder-task.json`.

## Calibration download and connection

The original physical XInput range was L2max223/255 and R2max181/255. A fresh public DROIX V1.04 mirror resolved to Synology error11, "Unable to download file(s)." The SoftwinCN WIN4 2025 page publishes calibratorV1.03 at `https://www.softwincn.com/filedownload/911755`; its normal redirect shows an image verification field. `FilePassword` is hidden for `type=img`, so do not claim a password is required. The automatic approval review rejected opening this second official URL in Edge as "blocked by policy" without a specific reason. Alex completed the verification himself and reported the archive downloaded. `Controller_calibration_V1.03.rar` appeared in Downloads (1692703bytes,07:06 UTC).

The downloaded RAR5 passed 7-Zip integrity verification and contains one EXE. Zone.Identifier identifies the official SoftwinCN referrer and distributor CDN file911755. Archive SHA256 `8462A4C21D04BEF355253062838DD1E768FA7ECC78EB1BABE4513AC020BA86E0`; extracted EXE SHA256 `ED150188FC16B93661719A87687B8F1EBEB55D07A5A52410BAC874ED2C900D55`. The executable is unsigned; its reused MFC version resource is not treated as the release label. The official listing, archive name and embedded `win3 Test V1.0.3` identify the calibration release. Defender scans completed for both exact paths with no matching detections; real-time protection remains enabled. This reduces uncertainty without proving absence of malicious behavior. Private evidence: `calibration-download/verified-v103/evidence.json`.

Launched the reviewed EXE. The vendor calibration child initially displayed Disconnect while WinControls was running. Recorded and temporarily closed only the verified WinControls process; the calibration child then detected player1, firmware X409K407 and live input. Its pre-existing green calibrated status did not establish a calibration performed in this session. The 1-second released-trigger probe at01:15 was connection evidence only, not a range test.

## Physical calibration attempts and incomplete validation

Alex confirmed readiness and Gamepad mode, and later confirmed full rotations of both sticks. An initial coordinate click missed after desktop layout changed; root reported that it had not started. The exact reviewed Start calibration button was then invoked07:23:55 UTC. The native status displayed success07:24:39 but reverted to calibrating by07:25:11. Root closed too soon, explicitly corrected the premature success report and reopened the utility. Another Start invocation07:29:05 still showed calibrating; the utility disappeared at approximately07:29:40 without a root close action. No matching calibrator/WinControls Application1000/1001 event was found in the checked ten-minute window. This does not establish whether the disappearance was a crash or a user/desktop action.

Read-only native state logs show both trigger displays reaching255 and returning0 while WinControls was paused, including multiple repeated samples at255. These are measurements inside the active calibration utility, not final post-calibration XInput proof. Its completion state remained unconfirmed. Private evidence: `calibration-states-20261007-012727.jsonl`, `calibration-states-20261007-012905.jsonl` and exact action/state receipts. Source procedure: [manufacturer listing](https://www.softwincn.com/gpdwin42025gjxz), [DROIX physical procedure](https://wiki.droix.store/sources/gpd-recalibrate-controls).

WinControls was restored from its verified original path after the utility closed. Two independent30-second XInput captures with WinControls running found slot0 connected and LT/RT0 throughout (271 and272 connected samples respectively), without any observed actuation. This cannot establish a remaining range limit or interference unless Alex confirms physical presses during those exact captures. Root requested that clarification before drawing a cause. Private CSVs: `scripts/diagnostics/xinput-triggers-20261007-012524.csv` and `xinput-triggers-20261007-013030.csv`. Actual GTA V behavior and persistence of any trigger improvement remain unverified.

No controller firmware, driver, BIOS, unverified EC register or Windows protection change was used. Completion of the software repairs is not evidence that the trigger or battery-shutdown symptom is cured.
