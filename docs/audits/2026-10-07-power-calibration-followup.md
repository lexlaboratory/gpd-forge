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

Installed restart verification remains pending until the service deployment and real saved12 W replay are measured. Native UI is preserved in this service-only deployment.

## Shutdown evidence

The follow-up System event observation had no new41/6008/1001/WHEA event in06:20–06:33 UTC. Thermal guardian notices at06:24–06:25 UTC describe requests/recovery, not an observed shutdown. The historical October1 events still lack contemporaneous voltage/temperature. No shutdown reproduction or stress test has been performed.

`scripts/diagnostics/watch-power-events.ps1` passed syntax checks and a real one-minute run:12 samples with all four API and battery statuses `ok`, boot time recorded, event queries explicitly `noEvents`. It distinguishes inaccessible events, offline API and unexpected HTML from valid/no-event data. Each JSONL line is physically flushed. No EC/SMU writes, process control or external transmission. Bounds:32MiB/file,128MiB/folder, maximum24h; previous evidence is retained when full rather than automatically deleted.

Task `GPDForge-PowerDiagnostics` is registered and running as Alex, Limited/Interactive, at logon; it starts immediately for1440min with approximately5-second intervals, and can run on battery. Root verified task principal, trigger, settings and growing file `power-events-20261007-005145-d6cdc47c.jsonl`. No elevated/System recorder. This is bounded per-logon monitoring, not an indefinite service. Remove with `Unregister-ScheduledTask -TaskName GPDForge-PowerDiagnostics -Confirm:$false`; stop with `Stop-ScheduledTask -TaskName GPDForge-PowerDiagnostics`. Private task receipt: `power-recorder-task.json`.

## Calibration download status

L2max223/255 and R2max181/255 remain the last physical XInput measurement. Manufacturer calibration has not been performed. A fresh public DROIX V1.04 mirror resolved to Synology error11, "Unable to download file(s)." The SoftwinCN WIN4 2025 page publishes calibratorV1.03 at `https://www.softwincn.com/filedownload/911755`; its normal redirect shows an image verification field. `FilePassword` is hidden for `type=img`, so do not claim a password is required. No verification image was solved, no form submitted and no executable obtained. The automatic approval review rejected opening this second official URL in Edge as "blocked by policy" without a specific reason; Alex has been asked to open it and complete the verification himself. Detailed retrieval evidence stays private in `calibration-download/`.

No controller firmware, driver, BIOS, unverified EC register or Windows protection change was used. Completion of the software repairs is not evidence that the trigger or battery-shutdown symptom is cured.
