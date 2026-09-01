# ADR-0003: The firmware assistant reports and refuses; `canAttempt` is permanently false

**Date**: 2026-08-30 (decided), 2026-08-31 (recorded)
**Status**: accepted
**Deciders**: Alex, KRÓNOS

## Context

Phase 4 of the roadmap carried an item called *"Firmware-update assistant with preconditions"*. It is
a reasonable-sounding feature and there was a concrete motivation on this device: the G1618-04 runs
BIOS **0.10 (2024-11-28)**, and the intermittent hibernation resume failure of 2026-08-29 leaves no
crash dump — which places it *before* Windows takes control, i.e. bootloader or firmware.

So the question was live: should GPD Forge be able to flash?

The constraints are not symmetric with anything else this daemon does. A wrong TDP write is reverted
on the next tick. A wrong fan duty is bounded by the floor and restored to AUTOMATIC. **A failed BIOS
flash on a GPD handheld has no vendor recovery path a normal user can execute** — there is no dual
BIOS, no service programmer in the box, and the machine that would run the recovery tool is the
machine that is bricked.

## Decision

**`GET /firmware` reports and does not update anything. There is no `POST`. `canAttempt` returns
`false` unconditionally** (`core/Program.cs:817`).

What it does provide is the part that is actually useful and carries no risk: what is installed
(version and date, confirmed on device), and the preconditions for updating **by hand** — on AC,
above 50 % charge, no other power tool running, no sleep during the flash.

The refusal is explicit in the response rather than implied by a missing endpoint, because a client
that finds no update route cannot tell "not implemented yet" from "deliberately absent".

## Alternatives considered

### Flash, behind a triple gate and a typed confirmation
- **Pros**: closes the roadmap item; the preconditions are checkable in software.
- **Cons**: it would be the most dangerous code in this repository by a wide margin, and the failure
  mode is a dead handheld with no user-executable recovery.
- **Why not**: the gates protect against *accident*, and the risk here is not accident — it is a
  power loss, a Modern Standby wake, or a firmware image mismatch during the write. No gate this
  daemon can implement covers those.

### Download and stage the image, let the user run the vendor tool
- **Pros**: removes the "find the right file" step, which is where users actually go wrong.
- **Cons**: GPD publishes firmware through channels with no signed manifest we can verify. Staging an
  unverified BIOS image and presenting it as ready to flash transfers our trust to the user without
  transferring the ability to check it.
- **Why not**: reconsider **only** if a verifiable publication channel appears. Recorded here so the
  next session does not re-derive it.

### `canAttempt` computed from the preconditions
- **Pros**: honest-looking API; the field would mean something.
- **Cons**: a field named `canAttempt` that ever returns `true` promises a route that does not exist,
  and invites a client to build a button for it.
- **Why not**: the value is not "are the preconditions met", it is "will this daemon flash", and the
  answer to that is no.

## Consequences

- The Phase 4 item is **closed by decision, not by implementation**. It should not reappear as an
  unticked checkbox — that reads as work someone could pick up.
- The 2026-08-29 resume failure needs a different owner. It is a real, unexplained, pre-Windows event
  and it is now tracked as a triage item (see the post-0.2.0 plan, Phase C), not as a reason to build
  a flasher.
- An assistant that *implied* it might flash would be nearly as bad as one that did. The report text
  and the field name were chosen so no client can reasonably infer a write path.
