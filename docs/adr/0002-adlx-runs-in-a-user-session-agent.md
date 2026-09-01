# ADR-0002: ADLX runs in a user-session agent, and the daemon never holds a handle

**Date**: 2026-08-29 / 2026-08-30 (decided), 2026-08-31 (recorded)
**Status**: accepted
**Deciders**: Alex, KRÓNOS

## Context

The Radeon 3D settings — Anti-Lag, Chill, Boost, Image Sharpening and FRTC — are only reachable
through **ADLX**, AMD's device library. Two facts collided:

1. **ADLX cannot be initialised from the daemon.** GPD Forge's service runs as LocalSystem in
   **session 0**, and ADLX needs the display driver stack of an interactive session. Identical code
   initialises fine as a user and fails as a service. The first version's error message was true and
   useless — it said ADLX was unavailable without saying that the caller's session was the reason.
2. **AMD's documented C# route is unusable here.** It is SWIG plus a C++ compiler producing an
   unsigned native DLL — and an unsigned native DLL is precisely what Smart App Control blocks on
   this machine. That is not a preference; it is a measured constraint that has bitten this project
   repeatedly (`os error 4551`).

## Decision

**Reach ADLX through its C interface with hand-written vtable offsets, and make the calls from
`--gpu-agent`: the same assembly, started in the user's session.**

Same assembly matters. A separate helper binary would be a new unsigned executable for Smart App
Control to refuse, and would need its own signing story. Starting the existing signed binary with a
different switch introduces nothing new to block.

The vtable layout is **verified at startup against a fact read independently over WMI**
(`core/Gpu/WmiSystemMemoryProbe.cs`), so a misaligned vtable is caught before anything is called
through it — hand-transcribed offsets from SDK headers are exactly the kind of thing that is wrong
silently.

**The daemon holds only what the agent reports, and says how stale it is.** Reports carry the
agent's read time; anything older than 30 s is returned but marked unusable; and *"no agent has
reported yet"* is a distinct status from *"the agent says ADLX is unavailable"*. Two different
unknowns that a boolean would have merged.

Writes are **desired state, not a command queue** (`core/Gpu/GpuDesiredState.cs`). The daemon records
intention via `POST /gpu/frame-cap`, the agent reconciles, and `GET /gpu` reports what the driver
actually did. An agent that restarts or drops ticks converges instead of replaying.

## Alternatives considered

### Call ADLX from the daemon
- **Pros**: no second process, no staleness model, no reconciliation.
- **Cons**: does not work. Session 0 has no display driver stack.
- **Why not**: measured, not assumed — the same code path succeeds as a user and fails as a service.

### AMD's SWIG + native shim C# binding
- **Pros**: AMD-documented and supported.
- **Cons**: produces an unsigned native DLL, which Smart App Control blocks on this device.
- **Why not**: the artefact it produces is the artefact this machine refuses.

### A separate signed helper executable
- **Pros**: clean process boundary, no `--switch` dispatch in `Program.cs`.
- **Cons**: a new binary to sign and to get past SAC, for zero functional gain.
- **Why not**: same-assembly re-entry gets the session change for free.

## Consequences

- 🔴 **The daemon must never hold an ADLX handle, and this is not a style rule.** It briefly did, and
  a second handle's `ADLXTerminate` invalidated the first one's pointers, crashing the service with
  an access violation.
  **An `AccessViolationException` is not catchable in .NET.** The `try/catch` around those interop
  calls *reads* like containment and provides none. The only containment is not making the call from
  there. Any future code that "just reads one value" from the daemon reintroduces the crash.
- GPU features are unavailable whenever no user is logged in. This is correct and is reported as
  such rather than hidden.
- The GPU profile hangs off the **mode**, so the existing per-app rules and their hysteresis drive
  it and every path that sets a mode applies it. No second matching system was built.
- The agent locks `GpdForge.Service.dll` while running, which silently broke `dotnet publish` during
  installation until the installer learned to stop it first.
- ⚠️ FRTC has a non-obvious write order: **enable before writing the FPS value.** The intuitive order
  (set the value, then enable, so activation cannot apply a stale cap for an instant) returns
  `ADLX_FAIL` (rc=3). Found only by surfacing the error code; the boolean version said "NOT applied"
  and nothing else.
