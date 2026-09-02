# ADR-0001: PawnIO, not WinRing0, for EC and low-level hardware access

**Date**: 2026-08-25 (decided), 2026-08-31 (recorded)
**Status**: accepted
**Deciders**: Alex, KRÓNOS

## Context

GPD Forge exists partly because `MotionAssistant` and `GPD Tool` ship a **WinRing0** driver, which
Windows Defender flags as a vulnerable driver: it grants unrestricted port and MSR access to any
caller, with no per-operation policy. Shipping the same driver to fix a complaint about that driver
would be self-defeating.

But the fan work needs the Embedded Controller, and the EC is only reachable through a kernel driver.
There is no user-mode path. So the question was never *whether* to load a driver, only which one and
under what constraints.

At decision time, `LibreHardwareMonitor` 0.9.4 was in the tree for optional richer telemetry
(package watts, temperatures) and it loads a Ring0-family driver when hardware access is enabled.
The **default** telemetry path is driverless WMI; the Ring0 path is behind
`GPDFORGE_ENABLE_HARDWARE=1` plus elevation.

## Decision

**Use PawnIO for all EC access. Never WinRing0.**

PawnIO ships a signed kernel driver that executes *sandboxed bytecode modules* rather than exposing
raw port I/O — the module declares which addresses it touches, so a bug in our code cannot become
arbitrary kernel access.

The implementation detail that made this cheap: **PawnIO is already inside `LibreHardwareMonitorLib`**
as an embedded resource (`LibreHardwareMonitor.Resources.PawnIo.LpcIO.bin`). No separate install, no
new binary for Smart App Control to refuse. `core/Fan/PawnIoEcPort.cs` loads it by reflection because
`PawnIo.LoadModuleFromResource` is `internal`, and throws a descriptive error for every missing
type, method or resource so the probe can surface *which* assumption broke.

The read path issues address + read only. **No control writes go through it** without the second gate
(`GPDFORGE_ENABLE_FAN_CONTROL`).

## Alternatives considered

### WinRing0 (what the incumbents use)
- **Pros**: universally documented, every handheld tool uses it, EC access is trivial.
- **Cons**: Defender-flagged vulnerable driver; unrestricted MSR/port access; it is one of the
  problems this project was created to remove.
- **Why not**: adopting it would contradict the project's stated reason to exist, and would make
  GPD Forge indistinguishable from the tools it replaces on the one axis users can actually check.

### Wait for a PawnIO-capable LibreHardwareMonitor to ship stable
- **Pros**: no reflection, no internal API dependency.
- **Cons**: parked four roadmap items behind someone else's release schedule.
- **Why not**: superseded by measurement — the PawnIO module was already embedded in the version in
  the tree. The wait was for something that had arrived. See *Consequences*.

### Write our own signed kernel driver
- **Pros**: total control over the policy surface.
- **Cons**: EV certificate, WHQL attestation, and a kernel driver maintained by a two-person project.
- **Why not**: the risk of a self-maintained kernel driver exceeds the risk it removes.

## Consequences

- The fan path talks to PawnIO **directly**. `IBroker`/`NullBroker` in `core/Broker/` were designed
  as the abstraction for this and ended up used by nothing — the indirection did not earn itself.
- **Reflection against an `internal` API is a real fragility.** A LibreHardwareMonitorLib upgrade can
  rename the type, the method or the resource. The constructor's descriptive throws are the
  mitigation: the failure is loud and names the missing symbol rather than degrading into "no fan".
- 🔴 **Corrected 2026-09-01, and the original text here was wrong.** This ADR said, on the day it was
  written, that "LibreHardwareMonitor still loads a Ring0-family driver for the optional telemetry
  path". That is **false for the version pinned in this repository**. Scanned the shipped
  `LibreHardwareMonitorLib.dll`: **zero** occurrences of `WinRing0`, `Ring0`, `KernelDriver`,
  `OpenLibSys` or `InpOut`, no `.sys` resource, and **15** hits for `PawnIo` plus the
  `PawnIo.RyzenSMU.bin` and `PawnIo.AMDFamily17.bin` modules. The dependency already reads the PM
  table through PawnIO.

  So "moving that half to PawnIO too" is not a target — it is done, by upstream, and this ADR was
  claiming otherwise about its own dependency. The claim was inherited from the 2026-08-25 decision
  note and never re-checked against the pinned binary.

  ⚠️ **This matters beyond tidiness, because the false claim was load-bearing.** The
  `DPC_WATCHDOG_VIOLATION` triage item in the roadmap rests on it: the reason given for not
  dismissing the 2026-08-28 bugcheck was "this project loads a Ring0-family driver through LHM". It
  does not. That does not clear GPD Forge — PawnIO is still a kernel driver and a DPC watchdog
  violation still needs an owner — but the triage must start from what the binary actually loads,
  not from this paragraph.
- Board detection is required before any access: G1618-04 / "Ver.1.0" → WinMax2, `RpmRead 0x0218`
  (`core/Fan/GpdDeviceDb.cs`). A wrong board mapping writes to the wrong EC register.
