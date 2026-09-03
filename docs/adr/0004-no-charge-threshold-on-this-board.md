# ADR-0004: There is no charge threshold on the G1618-04, and here is the evidence

**Date**: 2026-09-01
**Status**: accepted
**Deciders**: Alex, KRÓNOS

## Context

"Stop charging at 80 %" is the single most requested battery feature on any handheld, and the one
GPD Forge repeatedly has to say no to. `ChargeLimitService` has reported it unavailable since it was
written, but on the strength of an absence: nobody had found a path, which is not the same as
establishing there is none.

That distinction matters here more than usual, because the cost of being wrong in the other
direction is severe. Guessing an EC register for a **charge controller** on a board with no vendor
recovery path is the most destructive thing available in this repository — a fan spinning wrong is
audible in seconds, a mis-programmed charge IC is not.

So Phase G5 was scoped as **observation only, no writes, no guessed registers**, and defined a
documented "not available" as a successful outcome rather than a failure.

## Decision

**The charge threshold is not reachable on this board. Stop looking without new evidence.**

Four independent lines of investigation, all read-only, all reproducible:

### 1. No vendor tool implements it — so there is nothing to observe

The intended method was to watch what MotionAssistant or GPD Tool writes and diff the EC. Both are
installed on the reference device, and neither has the feature:

- `C:\Program Files\Motion Assistant\Profiles\` — every profile carries `ACTDP`, `DCTDP`,
  `GPUClock`, `FanSettings*`, gyro and button mappings. **No charge key of any kind.**
- `MotionAssistant.exe` string scan: the only `charge` matches are `charge level`, `charge rate`,
  `discharge rate` — the names of Windows' own battery counters. (`es-EC` / `quz-EC` are Ecuador
  locale codes, not Embedded Controller.)
- `GPDToolService.exe`: `Charge Level`, `Charge Rate`, `Fully-Charged Capacity`,
  `Charge/Discharge Current` — the Smart Battery **read** specification — alongside RyzenAdj's
  `set_stapm_limit`. Nothing that writes a threshold.
- `GPDTool.exe`: no charge-related strings at all.

**The observation plan died of its own premise.** You cannot diff a register nobody writes.

### 2. ACPI does not expose the standard mechanism

All 29 ACPI tables were dumped from `HKLM\HARDWARE\ACPI` (263 KB) and searched.

**`_BMC` and `_BMD` are absent from every table.** Those are *Battery Maintenance Control* and
*Battery Maintenance Data* — the only mechanism the ACPI specification defines for an operating
system to ask firmware to control charging. They are not declared here.

What IS present is read-and-notify only: `_BIF`, `_BIX`, `_BST`, `_PCL`, and `_BTP`.

### 3. `_BTP` is a notification, not a control

`_BTP` (Battery Trip Point) is the one battery method here that *writes* anything — it stores a level
into the EC fields `BTPL`/`BTPH` so the firmware raises an event when the charge crosses it. It asks
to be **told about** a level. It does not stop current at one, and the AML confirms the shape:
`_BTP` takes an argument, splits it into the two bytes, and returns.

### 4. The EC's battery block is data, and the promising names are dead

The EC operation region declares a battery field block — `BSNL/BSNH` (serial), `BDCL/BDCH` (design
capacity), `BDVL/BDVH` (design voltage), `BFCL/BFCH` (full charge capacity), `BPVL/BPVH` (present
voltage), `BRCL/BRCH` (rate), `BRSC` (relative state of charge), `BCTH/BCTL`.

`BCTH`/`BCTL` look exactly like a "Battery Charge Threshold High/Low" pair, and that is the trap this
ADR exists to close. **Each appears exactly once in the entire DSDT — in its own field declaration
and nowhere else.** No AML method reads or writes them. A control has at least two references: the
declaration and the code that uses it.

Two other near-misses, recorded so the next search does not repeat them: **`BCLB` is display
backlight** (it sits under `LCD._BCL`/`_BCM`), and **`BTH0` is Bluetooth** (`_HID QCOM6390`).

## Alternatives considered

### Write to `BCTH`/`BCTL` and see what happens
- **Pros**: the names fit; it would take ten minutes.
- **Cons**: they are unreferenced, so their meaning is a guess dressed up as a hypothesis. The write
  goes to a charge controller on hardware with no recovery path.
- **Why not**: this is precisely the guessing the phase forbade. A plausible name is not evidence.

### Dump the EC and diff it across a change
- **Pros**: the technique that would settle it, and the same read-only shape as the HID probes.
- **Cons**: it needs something that CHANGES the value, and finding (1) is that no such thing exists
  on this machine.
- **Why not**: it would be a probe with no resolving power. It becomes worth building the moment
  anything is found that moves a charge setting — including a BIOS option (see below).

### Ship a threshold UI that stores a value and does nothing
- **Why not**: already rejected in the pending-features plan, and worth restating. Someone who
  believes their charge is being held stops worrying about a pack that is still ageing. That is
  worse than the honest refusal.

## Consequences

- `GET /battery/charge-limit` keeps reporting `available: false`, and `GET /battery/charge-guard`
  keeps `canStopCharging: false`. Both are now backed by evidence rather than by absence of a
  finding.
- The charge guard (ADR-adjacent, Phase G3) is the answer instead: it counts hours at high state of
  charge and can hold a cooler ceiling, attacking the temperature half of lithium ageing — the half
  that is reachable from here.
- 🔓 **What would reopen this**, specifically:
  1. ✅ **A BIOS setup option** for a charge limit (hold `DEL` at boot). **Checked 2026-09-03 — it
     exists, and it does not work.** There is a "Battery Charge Limit" / eco-charge option in setup;
     it was set to 85 %. Battery telemetry logged at 60 s resolution through the next full charge
     cycle crossed 85 % with zero pause — `09:15:15 85% charging=True` straight through to
     `10:56:21 100% charging=False`, no plateau, no drop in charge current, nothing. This is the same
     shape as finding 4: a label in the UI with nothing behind it. **Does not reopen this ADR — it
     closes the one lead that was still open**, and by the strongest kind of evidence (observed
     behavior under real load), not static analysis. `root\wmi:ecoChargeMode` was also checked
     directly: the class exists but returns zero instances even elevated, and `Get-CimClass` reports
     no properties or methods — consistent with a declared-but-unimplemented WMI surface, the same
     species of dead end as `BCTH`/`BCTL`.
  2. A firmware update that adds `_BMC`/`_BMD`. Re-run the table dump after any BIOS update.
  3. A vendor tool release that gains the feature — then diff the EC against it.
- The ACPI dump procedure is worth keeping: tables live under `HKLM\HARDWARE\ACPI`, are readable
  without elevation, and answered this question in minutes.
