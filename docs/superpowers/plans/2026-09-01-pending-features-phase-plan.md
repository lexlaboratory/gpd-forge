# Pending features — phase plan

**Date**: 2026-09-01
**Baseline**: `cc84e85`, v0.2.0 published, 921 unit + 129 E2E + 4 desktop tests
**Method**: measured against the live daemon and the device's own WMI/ACPI surface before planning.
Two premises were checked and one of them was wrong — see *Battery health* below.

---

## What was measured today

| Fact | Value | How |
|---|---|---|
| Battery design capacity | **43,890 mWh** | `powercfg /batteryreport` |
| Battery full-charge capacity | **40,009 mWh** | `MSBatteryClass.FullChargedCapacity` — **live, driverless** |
| Battery health | **91.2 %** | the two above |
| Cycle count | **not reported** (`0`) | `BatteryCycleCount.CycleCount` — the EC does not expose it |
| Battery temperature | **not available** | `BatteryTemperature` has no instances |
| Live charge/discharge | **mW, both directions** | `BatteryStatus.ChargeRate` / `.DischargeRate` |
| Idle discharge | **14.8–16.4 W** while package draws 5–9 W | `GET /telemetry` |
| Gaming preset | STAPM 25 W → **~35 W system** → ~1.1 h | preset + measured overhead |

The number that governs everything below: **the system draws ~9 W before the SoC does anything.**
On a 40 Wh pack that is 4.4 hours of doing nothing at all, and it is the largest single term in
every battery decision in this document.

---

# Part 1 — The gaming-on-battery profile

## Recommendation

**A fifth first-class mode, `gaming-battery`:**

| Setting | Value | Why this number |
|---|---|---|
| STAPM | **15 W** | Sustained draw becomes ~24 W with overhead → **~1.6 h**, against ~1.1 h for the current gaming preset. Below ~12 W the SoC starts losing frames faster than it saves watts on this part. |
| Fast / Slow | **20 W / 17 W** | Headroom for load transitions kept, unlike the AI preset which is deliberately flat. A shader-compile spike that gets throttled costs a visible hitch and saves nothing over a session. |
| Tctl | **90 °C** | Not 95. Lower ceiling means the fan spins less, and the fan is part of that 9 W overhead. |
| FRTC cap | **45 fps** | **This is the real lever, not the TDP.** |
| Chill | **on** | The one AMD feature that genuinely trades frames for watts. Already what the `battery` GPU profile does. |
| Auto-FPS target | **off** | See the rule below. |

## Why the frame cap matters more than the TDP

An uncapped game takes every watt it is allowed and converts it into frames nobody asked for. A
handheld at 800p rendering 90 fps when the panel shows 60 is spending roughly a third of its power
budget on frames that are discarded. The cap stops that at the source: the driver stops asking, so
the SoC clocks down on its own and the TDP ceiling never even comes into play.

That is why the cap is 45 and not 60. The panel here reports **60 Hz with no other supported mode**
(`GET /display/refresh`), so 45 gives up smoothness in exchange for a large, predictable power saving
— and 45 with a stable frametime feels better than 60 that dips.

⚠️ **The rule that must not be broken**, and it is already enforced on both endpoints: a cap BELOW an
active auto-FPS target makes the governor raise power forever chasing frames the driver is
withholding — hot, loud, no extra frames, and no error anywhere. That is why this profile sets a cap
and **no target**. If someone wants a target too, it must be ≤ the cap.

## The honest ceiling on this

With ~9 W of overhead, moving the SoC from 25 W to 15 W improves runtime by about 45 %, not by 40 %
of the total. **The overhead is nearly half the budget of this profile and nobody has measured where
it goes** — panel, backlight, Wi-Fi, USB rails, the fan. Until that is attributed (Phase D of the
post-0.2.0 plan), this profile is the best guess available from real numbers, not a designed
optimum. Its watts should be re-derived once the budget exists.

## What it costs to add a mode

Seven places enumerate modes today: `ModeProfiles.Map`, `AppRulePolicy.Modes`, `ModeRules`,
`GpuModeProfiles.Defaults`, `ui/src/types.ts` (`ModeId`), the mock daemon, and the mode picker.
`ForgeWorker` also special-cases `"gaming"` by name for auto-FPS. That scattering is itself worth
fixing while adding the fifth member — a mode list that lives in seven places gains a bug every time
it grows.

---

# Part 2 — Battery health

## The direct answer: blocking charge is not available on this board, and I checked twice

`ChargeLimitService` already reports this, and the reason is real: the charge threshold is an EC/BIOS
value with no documented driverless read or write path on the G1618-04. `docs/hardware/ec-registers.md`
maps fan registers only — nothing for charging.

I found a WMI class called **`ecoChargeMode`** and thought it contradicted that. It does not: the
class exists in Windows' own WMI battery schema and **has no instances on this machine**. An empty
schema class is not a vendor implementation. Recording it here so the next person does not spend the
same hour on it.

**Never guess an EC register for this.** Writing to the wrong EC RAM offset on a board with no vendor
recovery path is the most destructive thing available in this repository, and a charge controller is
worse than a fan: a fan spinning wrong is audible in seconds.

## What to build instead — most of the benefit, none of the risk

Lithium-ion ages from **time spent at high state-of-charge, multiplied by temperature**. Blocking
charge at 80 % is one way to attack that, and it is not the only one — nor even the largest lever on
a machine that idles at 15 W.

### 1. Report the health that is already measurable *(no hardware access, ship first)*

`GET /battery/health`: design 43,890 mWh, full-charge 40,009 mWh, **91.2 %**, with the trend over
time. GPD Forge does not surface this at all today, and it is the number that tells someone whether
any of this is working.

Honest about what is missing: **cycle count reads 0 because the EC does not report it**, and battery
temperature has no WMI instance. Both must be `null`, never a plausible substitute — the rule this
codebase already applies to standby drain, and which `GET /telemetry` currently breaks (see the
zero-vs-null item in the roadmap).

### 2. A charge guard that acts on what it can reach *(the real substitute for a threshold)*

The daemon cannot stop current. It **can** see `BatteryStatus.Charging`, `PowerOnline`,
`RemainingCapacity` and `ChargeRate` in mW, live. So:

- **Notice the damaging pattern**: plugged in, at/near 100 %, for hours. Alert once, with the hours
  accumulated — not a nag per tick, and the alert coalescing already built handles that.
- **Cut the heat during the top of the charge**, which is the half of the damage GPD Forge *can*
  actually influence: when `acConnected && batteryPct >= 95`, drop to a cool preset. A cell held at
  full charge at 30 °C ages far slower than one held at full charge at 45 °C, and the SoC is what
  heats it.
- **Track hours-at-high-SoC as a number**, so the advice stops being folklore and the guard can be
  judged by whether health degrades slower.

### 3. Check the BIOS — one reboot, and it may make this moot *(Alex's call, costs nothing)*

Several GPD handhelds expose a charge limit in BIOS setup. If the Win 4's BIOS 0.10 has one, the
right shape is what `/firmware` already does: **report it and tell the user exactly where it is**,
rather than write anything. That is a ten-minute documentation task instead of a research project.

### 4. Only then: investigate the EC threshold *(research, not coding)*

Same discipline as the HID work in Phase 7 — and the same reason it is listed as research: the next
step is finding out **what MotionAssistant or the BIOS actually writes**, by observation. Never by
inference from another board's map. If no observation is possible, this stays closed, and saying so
is a better outcome than a plausible guess.

---

# Part 3 — The phases

Ordered so that nothing is built on an unmeasured assumption, and so each phase is useful alone.

### Phase G1 — Battery health, reported *(no hardware writes, no risk)*

1. `GET /battery/health` from `MSBatteryClass` + design capacity, with history so degradation is a
   trend rather than a reading.
2. Surface it on the Power page next to the existing budget card.
3. `null` for cycle count and cell temperature, with the reason shown — not zeros.
4. Contract entries + mock, so it cannot ship without E2E coverage (the machinery from Phase B).

**Gate**: the reported health matches `powercfg /batteryreport` on the device, and a fresh machine
with no history shows "not enough data" rather than a fabricated trend.

### Phase G2 — The gaming-on-battery mode

1. Consolidate the mode list before growing it. Seven enumerations and a `"gaming"` string comparison
   in `ForgeWorker` is the bug surface, not the new preset.
2. Add `gaming-battery` with the numbers above, its GPU profile (Chill on), and its frame cap.
3. Enforce the cap/target rule at the profile level, so selecting the mode cannot produce the
   pathological pairing.
4. Add it to the overlay's mode picker — this is a mode chosen mid-session, away from a desk, which
   is exactly what the overlay is for.

**Gate**: measured on device, not estimated. One real game, two runs of equal length, `/sessions`
reporting fps average and 1 % low for each, and the battery percentage actually consumed. If it does
not beat the gaming preset on frames-per-watt, the numbers were wrong and get re-derived.

### Phase G3 — The charge guard

1. Track state-of-charge, AC state and charge rate over time; persist hours-at-high-SoC.
2. Alert on the sustained plugged-at-100 % pattern, coalesced.
3. Optional, opt-in: cool preset while charging above 95 %.
4. BIOS check documented (Phase 2, item 3) — reported, never written.

**Gate**: falsify it. Simulate the plugged-at-100 % pattern against a fake clock and confirm the
alert fires exactly once and names the hours; confirm the cool preset engages and, crucially,
**disengages** — a guard that lowers TDP and forgets to restore it is worse than no guard.

### Phase G4 — The zero-vs-null correction *(blocks honest battery work)*

`GET /telemetry` reports `0` for sensors it cannot read. Any health or budget feature built on top
inherits that lie. This is listed as a separate phase because it changes what the panel displays on
every machine without the hardware gate open, and that is a product decision, not a patch.

### Phase G5 — EC charge-threshold research *(gated on a decision to spend the time)*

Observation only, no writes, no guessed registers. Ends in either a verified path or a documented
"not available on this board", and the second outcome is a success.

---

## Sequencing, and why

**G1 first** because it is the only one that ships value with zero hardware risk, and because
everything else in Part 2 is judged against the number it produces. **G2 next** because it is what
was asked for and it is reachable today. **G3 after G1**, since a guard with nothing to measure
cannot be evaluated. **G4 whenever Alex decides** — it is small, and it is a decision rather than a
task. **G5 last**, and only if the BIOS check does not make it unnecessary.

## Not doing

- Guessing an EC register for charge control. Stated twice on purpose.
- A charge threshold UI that stores a value and does nothing. The current one already round-trips
  honestly with `applied: false`; adding polish to a dead control makes it look alive.
- Tuning the gaming-battery watts further before the 9 W overhead is attributed. That is arithmetic
  on an unknown.
