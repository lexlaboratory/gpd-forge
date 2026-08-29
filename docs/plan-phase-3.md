# Phase 3 plan — Agents / AI (local) mode

Drafted 2026-08-29, after auditing what is actually in the tree. The roadmap's Phase 3 lists three
open items; all three already have code, and none of them means what the checkbox implies. This plan
starts from the corrected state, because two of the three items need rewriting rather than building.

## Corrected state

| Roadmap item | Roadmap says | Actually |
|---|---|---|
| VRAM/UMA reassignment preset | `[ ]` | Read-only advisory, **by design and correctly so** |
| Sustained-CPU power shaping | `[ ]` | Pure function exists, **nothing applies it** |
| …+ "sustained" fan curve | `[ ]` | Not started, **blocked** by the Phase 1 driver decision |
| Anti-Modern-Standby during inference | `[ ]` | **Done for GPD Forge jobs**, absent for anything else |
| Job queue + API | `[x]` | Correct |
| MCP server | `[x]` | Correct |

## P3.1 — Apply the sustained profile (do this first)

`ProfileShaper.Shape` collapses fast/slow boost into one flat ceiling, for a well-argued reason:
boost above sustained STAPM buys no throughput on a continuously CPU-bound inference job, it only
adds heat, fan noise and thermal cycling.

It is called in exactly one place — `GET /ai` — and the result is **returned for display and thrown
away**. AI mode applies `ModeProfiles.For("ai")` *unshaped*, so the boost windows the shaper exists
to remove are live during every inference run. This is the same defect shape as the resume restore
before 2026-08-29: correct logic, no caller. It is the highest-value item here and has no blockers.

Work:
- Apply the shaped profile when AI mode is active, through the existing `ClosedLoopTdpController`
  (so it inherits apply → re-read → retry → honest `verified`) and the existing conflict guard.
- Decide precedence against the user's editable AI preset explicitly, and say which won. Silently
  overriding a preset the user edited would be its own dishonesty.
- Surface `sustainedProfile` on the AI panel as *applied*, not as an advisory number.

Risk: low — no new hardware path, reuses a proven controller.
Guard test: assert the applied profile has `fast == slow == stapm`, so a future refactor cannot
quietly go back to shaping-for-display-only.

## P3.2 — Hold the machine awake for inference we did not start

`JobsState` acquires the keep-awake hold while a GPD Forge job runs. Anything started by hand —
`ollama serve`, LM Studio, a training script in a terminal — gets no hold at all.

This became a *live* problem today. Until 2026-08-29 the machine never entered Modern Standby
(`STANDBYIDLE` was "never"; the sleep study contained zero Sleep sessions), so an unheld inference
run could not be suspended. It now sleeps after 5 minutes on battery, so this gap can suspend a long
local inference mid-run for the first time.

Work:
- Hold while a configured inference process is **actually working**, not merely present. Gating on
  presence would keep the machine awake indefinitely for an idle `ollama serve`, which is the exact
  failure we spent today removing — an all-night drain nobody asked for.
- Activity signal: sustained CPU/NPU utilisation attributable to the process, with hysteresis, in the
  spirit of `FocusProfileWorker`'s anti-flapping.
- Make the hold visible and attributable in the UI: which process is holding, since when. A machine
  that will not sleep and will not say why is the complaint this feature otherwise creates.
- Release on exit, on idle, and on shutdown — the ref-counted `AntiStandbyService` already handles
  the mechanics.

Risk: **the inverse of the bug fixed today.** A wrong heuristic drains the battery overnight. Ship it
conservative (short hold windows, re-evaluated) and observable.

## P3.3 — VRAM: rewrite the item, do not build it

"VRAM/UMA reassignment preset" cannot be delivered honestly on this board. The UMA split is applied
by the BIOS at boot (GOP/_DSM); there is no verified, reversible user-mode write, and poking a
vendor ACPI/registry value risks a black screen on a device that cannot be rolled back remotely.
`VramAdvisor` already says exactly this and is right to.

This is the same correction as the S0↔S3 helper in Phase 2: the item was written before anyone
checked whether the hardware permits it. Replace it with what is real:
- Keep the live read.
- Name the exact BIOS setting and value for the G1618-04, instead of generic advice.
- Detect the split changing across a reboot, so the advice can be **confirmed** rather than assumed.

## P3.4 — Sustained fan curve: blocked, and should say so

Depends entirely on the Phase 1 driver decision (2026-08-25): no fan writes until a PawnIO-capable
LibreHardwareMonitor ships stable. It is not an independent Phase 3 item and should be listed under
that dependency, so it stops reading as work someone could pick up.

## Sequence

1. **P3.1** — no blockers, highest value, closes a real gap between intent and behaviour.
2. **P3.2** — newly urgent because the machine now actually sleeps. Needs the most care.
3. **P3.3** — documentation and a confirmation path, not a feature.
4. **P3.4** — blocked; track under the Phase 1 driver decision.

## Free verification, no work required

The power-policy change of 2026-08-29 means the machine suspends for the first time. Within days,
without anyone building anything:
- `StandbyDrainTracker` gets its first real measurement (`lastDrainPctPerHour` has been `null` since
  release because there was never a suspend to observe, not because measuring was broken).
- `ResumeRestoreWorker` fires against a genuine resume rather than only against unit tests.
- The sleep study shows whether `Sleep` sessions replace `Hibernate` ones, which is the test of
  whether that mitigation worked.

Read those before starting P3.2 — they are evidence about the same subsystem, for free.
