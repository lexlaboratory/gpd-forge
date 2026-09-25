# GPD Forge — Local API contract (v0)

The daemon (`core/Api/`) exposes this local API. The UI, the overlay, and **external agents** are all
clients. The Node mock daemon in `tools/mock-daemon/` implements this exact contract so the UI and the
tests can run before the C# service exists — it is the reference the C# `Api/` must match.

## Transport & security
- Bind **localhost only** by default: `http://127.0.0.1:8787`. Remote access (over the tailnet) is opt-in.
- **Auth: none, deliberately** — see [ADR-0005](adr/0005-no-api-token-origin-allowlist-instead.md).
  The boundary is loopback binding plus an **Origin allowlist** (the Tauri shell and the Vite
  dev/preview ports; the panel and overlay are same-origin and need no entry).
  ⚠️ That stops a web page the user visited — the vector that genuinely existed, since the policy was
  `AllowAnyOrigin` until 2026-09-02 and any site could `POST /tdp` and read `GET /audit`. It does
  **nothing** against a local process, which can still reach `127.0.0.1:8787` with `curl`. Accepted
  knowingly: a process running as the user could read any token we handed the clients.
  🔴 This line used to promise "bearer token for HTTP; ACL for the named pipe". Neither has ever
  existed in `core/`, and the named pipe is recorded as dropped in the roadmap.
- Live telemetry: **the daemon polls only** — clients `GET /telemetry` on a timer (the UI does 1 Hz).
  There is no streaming endpoint in production. The mock implements SSE at `/telemetry/stream` for
  convenience, but no client consumes it.
- CORS: the mock allows the dev origin so the browser UI can call it.

## Types (mirror of `ui/src/types.ts` and `core/Telemetry/TelemetrySnapshot`)
```ts
type ModeId = 'gaming' | 'gaming-battery' | 'ai' | 'windows' | 'battery' | 'standby'

interface Telemetry {
  cpuTempC: number | null; gpuTempC: number | null; packageW: number | null
  cpuClockMhz: number | null; fanRpm: number | null; fanDutyPct: number | null
  fps: number | null; fps1PctLow: number | null
  batteryPct: number | null; dischargeW: number | null
  acConnected: boolean; acKnown?: boolean; tdpVerified: boolean | null
  sampledAtMs?: number | null; sampleAgeMs?: number | null   // GET /telemetry only, see below
}

interface ImportedProfile { name: string; stapmW: number; fastW: number; slowW: number; tctlC: number }
```

## Endpoints

### `GET /health`
`200 → { ok: true, version: string, model: string }` — `version` is read from the assembly (see
`GET /version`), never from a literal.

### `GET /version`  (what this build actually is)
`200 → { version: string, commit: string | null, builtUtc: string | null, runtime: string, model: string }`

The version model has **one source of truth**: `<GpdForgeVersion>` in `Directory.Build.props`. It feeds
the assembly, and `ui/package.json` + `ui/src-tauri/tauri.conf.json` carry copies that
`VersionModelTests` asserts equal — so a drifting copy is a failing build, not a support thread.

- `version` — read from the assembly's informational version. Nothing here is hand-typed. The old
  hard-coded `"0.1.0"` also fed `UpdateService`, which therefore compared every GitHub release against
  a constant nobody would remember to bump: it would keep offering an update that was already
  installed. `UpdateService` now *requires* the version and `Program.cs` supplies the real one, so
  forgetting is a compile error rather than a wrong answer.
- `commit` — the source revision, when the build recorded one (`InformationalVersion` gains `+<sha>`
  for a repository build). **`null` when not recorded** — an unknown commit reads as unknown.
- `builtUtc` — the PE header's link timestamp of the running assembly. This is the field that answers
  *"is the thing running older than the fix?"*. ⚠️ Deterministic builds put a content **hash** in that
  header, which read as unix seconds yields a confident, plausible, wrong date; implausible values are
  rejected and reported as `null` rather than shipped as a date.

**Why it exists:** on 2026-08-28 the app showed no telemetry while the daemon was healthy throughout —
the shell in Program Files predated the commit that fixed it, and establishing that meant diffing the
installed binary against a fresh build hunting for marker strings. The Settings ▸ About card now
compares the **shell** build against the **daemon** build and says plainly when they disagree.

### `GET /telemetry`
`200 → Telemetry & { sampledAtMs: number | null, sampleAgeMs: number | null }` — the latest snapshot.

**It is a cached reading, not a hardware read** (changed 2026-09-24). One background loop,
`TelemetrySampler`, reads the hardware at 1 Hz; this endpoint, the worker, auto-profiles, `POST /jobs`,
`GET /health/check` and the standby drain sampler all serve its last reading. Before, each of them
did its own full read — four WMI queries, an `Update()` of every LHM device and an EC read, ~100–140 ms
— so the UI and the overlay polling at 1 Hz each cost the machine two of those every second.

- `sampledAtMs` — when the hardware was read, Unix ms. `sampleAgeMs` — how old that was when this
  response was built; normally under 1000. A value that keeps growing means the sampler has stalled
  and the numbers are the last ones it managed to take. Both are **null before the first sample**,
  when every sensor is null too (the endpoint waits up to 5 s for that first sample at startup
  rather than answering with nothing). That placeholder also carries `acKnown: false` (audit round 3,
  2026-09-24): it went out as `acKnown: true`, a confident "on battery" nobody had read. A client
  should treat `sampledAtMs` present and null as "no reading yet" — neither live nor stale — as the
  UI, the overlay and the MCP tool (`unsampled: true`) do.
- Every published sample becomes one `GET /history` row: the history is fed by the sampler itself,
  not by the worker's tick, which skips samples superseded while a TDP write is in flight (audit
  round 3). Rows per second in `/history` are therefore the sampler's real rate.
- Battery, discharge and the ACPI thermal zone are queried **every 5 s** and served from cache in
  between (also re-read immediately after a suspend). `acConnected` rides on the battery query, so a
  plug-in shows up within 5 s.
- `GET /history` rows carry the bare snapshot, without the two time fields: their `unixMs` is already
  the sample time.

⚠️ **Every sensor field is nullable, and null means "no reading" — never zero.** Changed
2026-09-01. Before that an unreadable sensor came back as `0`, so with the hardware gate closed the
daemon reported `cpuTempC: 0, packageW: 0, fanRpm: 0`: a CPU at zero degrees, which no client could
distinguish from a cold machine. Measured on device with the gate closed, the response is now

```json
{ "cpuTempC": null, "packageW": null, "fanRpm": null, "fps": null,
  "cpuClockMhz": null, "batteryPct": 100, "dischargeW": 0, "acConnected": true }
```

Note what is **not** null there, because the distinction is per-field rather than blanket:
`batteryPct` comes from plain WMI and is a genuine reading, and `dischargeW: 0` is
true — the machine is on AC and nothing is discharging. A **measured zero is still a zero**;
collapsing it into null would lose as much information as the bug this fixed.

`cpuClockMhz` was on that list of genuine readings until 2026-09-24, and it was not one: it came from
`Win32_Processor.CurrentClockSpeed`, which on this HX 370 is the fixed 2000 MHz base clock — 2000 at
idle, 2000 at full boost. It is now LibreHardwareMonitor's **average effective core clock** (hardware
gate open). Without LHM, `CurrentClockSpeed` is still consulted, but reported only once it has been
seen to change; a value that never moves is `null`, as in the example above.

`fps` is null both with no probe and with a probe that has produced no sample: those do not
distinguish "nothing is presenting frames" from "PresentMon has no window of data yet", and
reporting `0` would assert the first when only the second is known. A probe that measures zero
frames reports `0`.

`fps` / `fps1PctLow` describe ONE process, the target (`core/Telemetry/FrameTarget.cs`): the
foreground app when it is presenting and is not a known non-game presenter; else the app-rule-matched app presenting the most frames,
never counting a known non-game presenter (the compositor, browsers, launchers such as
`steamwebhelper` — which the shipped `steam` rule also names — overlays, GPD Forge itself); else the
previous target while it keeps presenting (the overlay or the Steam menu has focus, the game renders
underneath). Only when the foreground is **unknown** — the service in session 0 with no user-session
agent reporting (`GET /session/foreground`) — does it fall back to the busiest presenter that is not a
known non-game one, so a Steam game no rule names still reads as the game rather than as Steam. The
same fallback applies when the foreground is a non-game presenter that is itself presenting: the
overlay (an Edge `--app` window repainting at ~1 fps) or the Steam menu in front of a game never takes
the reading from it; that foreground is the target only when nothing that could be a game presents. With
a known foreground and none of those, it is `null` — never "whichever process presents most". Frames are placed by
PresentMon's own row time, not by when their line reached the daemon, so a burst of buffered output
does not read as a burst of frames. The same probe keeps the target's last 10 s of frame times
in-process (`IFrameTimeSource`); no endpoint serves them yet.

`acConnected` is not nullable — mains power is an answer the daemon almost always has. When the
`Win32_Battery` query fails it falls back to `false` (on battery, the cautious reading), the daemon
logs a warning once per outage, and nothing acts on that fallback: the AC/battery mode switch skips
the tick instead of treating a sensor glitch as an unplug.

`acKnown` (additive, audit round 2, 2026-09-24) says which of those it is: `false` exactly when
`acConnected` is that fallback. Before it existed the fallback reached the UI as a confident "Battery
--%" on a plugged-in machine, and nothing said the switch was paused; the UI now shows the power source
as unknown and `GET /health/check` reports `ac_unknown`. A client that does not find the field (an
older daemon, a `GET /history` row from one) should treat the value as known. `GET /history` rows
from this daemon carry it too, since they are the bare snapshot.

`tdpVerified` **is** nullable, and this document claimed the opposite until 2026-09-02: it read
"`acConnected` and `tdpVerified` are not nullable — they are answers the daemon always has." It was
not an answer at all. The field was a hardcoded `true` at the single construction site, so with the
hardware gate closed — where the stub backend echoes back whatever it was handed — the daemon
reported a verified power limit it had never verified. `null` now means *nothing has written a TDP
yet, or the backend cannot report a readback*, which is a different claim from `false` (*the write
was read back and did not match*). Clients must not collapse the two; see `GET /tdp`.

**Two consequences worth knowing**, because the alternative was silent: `GET /health/check` now
returns `warn` with `telemetry_unavailable` instead of `ok` when it cannot see the CPU, and the
thermal guardian reports *"CPU temperature is unreadable — the thermal guardian cannot protect this
device"*. Both used to pass quietly, because in C# `null >= 90` is false and every threshold simply
declined to fire.

### `GET /telemetry/stream` (mock only)
SSE stream of `Telemetry` JSON events. **Not implemented by the daemon** and not used by any client —
kept in the mock for manual experimentation. Poll `GET /telemetry` instead.

### `GET /history`  ·  `GET /history/export.csv`  (telemetry history + CSV export)
- `GET /history?minutes=N → { samples: Array<{ unixMs: number, snap: Telemetry }> }` — samples from the
  last `N` minutes, oldest first. `minutes` defaults to 5, clamped to 1..60. Backed by an in-memory ring
  buffer the worker fills once per tick (capacity 3600 = 1h at 1Hz) — a freshly (re)started daemon holds
  less history than that until the buffer fills.
- `GET /history/export.csv` → `text/csv`, `Content-Disposition: attachment;
  filename="gpd-forge-telemetry.csv"` — every currently-held sample as CSV, one row each: `unixMs,
  isoTime, cpuTempC, gpuTempC, packageW, cpuClockMhz, fanRpm, fps, fps1PctLow, batteryPct, dischargeW, acConnected,
  tdpVerified`.

### `GET /mode`  ·  `POST /mode`
- `GET  → { active: ModeId }`
- `POST { name: ModeId } → { active: ModeId, tdp: string, frameCap: string | null }` — switches the
  active mode (applies its TDP + fan curve). `400` on unknown mode. Selecting a mode — the same one
  included — ends a manual `POST /tdp` override. It does not end a game profile's TDP
  (`GET /profiles/active`): re-picking `gaming` with that game in front writes the game's watts
  (owner `game-profile`); picking another mode ends the profile.
- `tdp` is the apply outcome: `AppliedVerified`, `AppliedUnverified`, `SkippedConflict` (a rival
  power controller holds TDP), `UnknownMode`, or `HeldByGuardian` (F1, 2026-09-25) — the thermal
  guardian is throttling, so nothing was written; its ceiling, computed under the new mode, stays in
  force and the mode's TDP comes back when the throttle clears. A mode pick used to lift a hot device
  out of its throttle for up to the guardian's 30 s re-assert.
- The daemon applies the active mode's TDP once when it **starts** (it used to wait for the first
  mode change), yielding like any mode switch when MotionAssistant or GPD Tool is running.

`ModeId` is one of `gaming`, `gaming-battery`, `ai`, `windows`, `battery`, `standby`. The catalogue
lives in `core/Profiles/Modes.cs`, and `ModeCatalogueTests` fails the build if the TypeScript union,
the UI list or the mock daemon falls behind it.

**`frameCap`** reports what the mode did about the driver-level cap, and is `null` for the modes that
have no opinion (which is most of them — silently clearing a cap the user set is the same class of
mistake as silently applying one).

Only `gaming-battery` asks for one today: **45 fps**, which is the larger part of what makes that
mode work. An uncapped game converts every watt it is allowed into frames nobody sees, and this panel
reports 60 Hz with no other supported mode; capping stops the work at the source, so the SoC clocks
down on its own and the TDP ceiling never comes into play.

The cap is requested as **desired state** through the same path as `POST /gpu/frame-cap` — the daemon
cannot reach ADLX from session 0, so the user-session agent reconciles it (see
[ADR-0002](adr/0002-adlx-runs-in-a-user-session-agent.md)).

⚠️ **It is checked, not assumed.** If auto-FPS is running with a target above the mode's cap, applying
it would create the one pathological pairing the API exists to refuse — arriving sideways through a
mode switch rather than through the endpoint that guards it. The **mode still applies**; only the cap
is skipped, and `frameCap` says so naming both numbers:

```
"frameCap": "not applied — A 45 FPS cap sits below the 60 FPS auto-FPS target. Auto-FPS would keep
raising power to reach a frame rate the driver is holding back, so the machine would run hot for no
extra frames. Raise the cap, lower the target, or turn one of them off."
```

### `POST /tdp`
`POST { stapmW: number } → { requested: number, observed: number | null, verified: boolean }`
Applies a sustained TDP through the **closed loop**: the daemon re-reads the PM table. If the firmware
reverted the limit, `verified:false` and `observed` reflects what actually held (this is the honest
behavior that replaces MotionAssistant's blind 30s re-apply).

- `400 { error: { code: "bad_tdp" } }` if `stapmW` is outside **5–40 W** (the preset table's STAPM
  band). Enforced by the daemon since 2026-09-24; before that only the mock refused, and the real
  handler passed any number to ryzenadj. Nothing is written or remembered on a 400.
- The profile is flat (`stapmW = fastW = slowW`) at the **active mode's Tctl** — it was a fixed
  90 °C, which lowered the thermal limit in `windows` (92) and `gaming` (95).
- The value is **remembered as an override until the mode changes** (`core/Profiles/TdpIntent.cs`).
  The guardian's throttle-clear restore, the charge guard's clear, the resume restore
  (`POST /standby/restore` and the automatic one) and the 30 s reassert put the override back, not
  the preset; a guardian throttle is a ceiling under the override, never above it. Any `POST /mode`
  ends it. It is held in memory: after a restart the preset of the mode last picked applies (the
  active mode itself IS kept across restarts, in `mode.json` under the data directory — before audit
  round 2, 2026-09-24, a restart always started, and wrote, `windows`). Only a mode picked with
  `POST /mode` is restored: when the last switch was automatic (auto-profiles, the AC/battery switch),
  the daemon starts in `windows` and auto-profiles re-derive the mode from the power source and the
  app in front (audit round 3, 2026-09-25 — a `battery` picked unplugged held 8 W after a boot on AC).
- `409 { error: { code: "tdp_superseded" } }` when a `POST /mode` (or a later `POST /tdp`) landed
  while this write waited for the TDP write gate. The mode change ended the override and wrote its own
  preset; writing this one anyway left the old mode's manual profile in force under the new mode, with
  `GET /tdp` reporting no override and the 30 s reassert keeping it (audit round 2, 2026-09-24).
  Nothing is written on a 409.

### The 30 s reassert
Every 30 s the worker reads the limits back (`ryzenadj --info`) and compares them with the last TDP
GPD Forge wrote, by the closed loop's own tolerance: ±1 on STAPM and the fast limit, and on the slow
limit and Tctl (`PPT LIMIT SLOW`, `THM LIMIT CORE`) whenever the PM table prints them — a row it does
not print is "not measured", never "moved". Slow and Tctl since audit round 2 (2026-09-24); before,
a firmware that put back only those read as holding. The same rule decides `verified` for every
write. It writes only when they differ — never on a failed or partial read, never while
MotionAssistant or GPD Tool runs, and not when another write started or finished first. The read,
the comparison and the re-apply run as one step under the TDP write gate, so the read never runs
beside another writer's ryzenadj on the SMU mailbox (until audit round 2 it ran outside the gate). A
re-apply appears in `GET /tdp` and `GET /audit` as owner `reassert`. Not during a guardian throttle,
which re-asserts its own ceiling.

Audit round 3 (2026-09-24), two refinements:
- **Rows this APU may not report.** Neither the slow row nor `THM LIMIT CORE` has been seen on the HX
  370 (reading the PM table needs elevation, and no capture exists). Each one is judged until the
  first write it demonstrably follows — from then on for good — and stops being judged, with one
  warning in the service log ("no longer used to judge"), after a write whose STAPM and fast held on
  every attempt while that row never came back at the value written. So a firmware that prints a
  fixed Tctl costs one write's retries, not an unverified write every time and a rewrite every 30 s.
- **A startup apply that yielded is completed.** When MotionAssistant or GPD Tool is running at boot,
  the startup apply yields and writes nothing; once the rival exits, the reassert applies the active
  mode (or its manual override), exactly as it completes a mode switch that yielded mid-session.
  Before, nothing was written until the user picked a mode again.

### `GET /audit`  (every hardware write the daemon has made)
`200 → { capacity: 500, total: number, failed: number, unconfirmed: number,
writes: Array<{ atUtc: string, subsystem: string, operation: string, detail: string,
verified: boolean | null }> }`  ·  `?limit=` 1..500, default 100, newest first.

The in-memory record of every write to the silicon: TDP, fan, GPU. `verified` is the readback where
the subsystem performs one and `null` where it cannot, and `detail` carries the owner of a TDP change
in brackets (`[thermal-guardian]`), which is the same value `GET /tdp` reports.

Deliberately **in memory and capped at 500** — a write log that filled the single SSD of a handheld
would be a worse fault than anything it helps diagnose. It does not survive a restart; anything a
person must still see after a reboot is written to the alert store instead.

⚠️ This endpoint is a complete history of what the daemon did to the hardware, and it was readable
cross-origin by any website until the CORS allowlist landed on 2026-09-02 (see the top of this
document). It went undocumented here until the same day, which is how it stayed unexamined.

### `GET /tdp`  (who set the power limit, and did it hold)
`200 → { stapmW: number | null, owner: string | null, verified: boolean | null, backend: string,
observedStapmW: number | null, observedPptW: number | null, attempts: number | null,
atUtc: string | null, note: string | null, manualStapmW: number | null, intentStapmW: number | null }`

The provenance of the last TDP write. Every field is null and `note` explains why when nothing has
written a limit since the service started — a fresh daemon has no last write, and reporting `0 W`
or `verified: false` for that would be inventing an event.

- `owner` — which subsystem made the write, one of `mode`, `manual`, `panic`, `thermal-guardian`,
  `charge-guard`, `auto-fps`, `tuner`, `restore`, `resume-restore`, `reassert`
  (`GpdForge.Tdp.TdpOwner`). Ten
  call sites write TDP; before 2026-09-02 none of them recorded which, so "why did my wattage
  change" had no answer. `ITdpController.ApplyAsync` now requires the owner, which is what keeps
  this list complete — a new writer does not compile without one.
- `verified` — the closed-loop readback for that write. `null` means the backend cannot report one.
- `backend` — `ryzenadj` or `stub`. **`stub` means no power limit was actually applied to hardware**;
  the stub echoes back whatever it was handed, so a `verified: true` from it attests to nothing.
  This is the distinction `GET /telemetry`'s `tdpVerified` hid while it was a hardcoded `true`.

- `manualStapmW` — the manual override `POST /tdp` set for the **active** mode, or `null` when there
  is none (never set, or a mode change ended it). Present even when nothing has been written yet.
  Added 2026-09-24 so the UI's TDP controls open on the value in force instead of a hardcoded 20 W or
  the preset.
- `intentStapmW` — what the user wants in force for the active mode (`TdpIntent.Resolve`): the manual
  override, else the game profile in front, else the mode's preset. `stapmW` is the last write by ANY
  owner, so mid-throttle it is the guardian's ceiling; this is not. The overlay's "save as profile"
  captures it, so a throttle, an auto-FPS step or a charge-guard ceiling never becomes a game's
  permanent watts (F1 audit round 1, 2026-09-25).

The same owner is written into the `GET /audit` line for the change.

### `POST /panic`  (Panic cool — safety)
`200 → { applied: boolean, stapmW: 8 }` — immediately applies a flat 8 W floor TDP profile
(`stapmW=fastW=slowW=8`, `tctlC=90`) through the same closed-loop `ITdpController` every other TDP
write uses, and sets the fan preference (`GET /fan`) to `Aggressive`. `applied` mirrors the closed
loop's verification (`false` if the firmware reverted the floor) — never a faked success. No request
body; dead simple by design so it's safe to wire to a single always-visible button.

### `GET /profiles`  ·  `POST /profiles/:mode`  (editable per-mode TDP presets)
- `GET → Record<ModeId, { stapmW: number, fastW: number, slowW: number, tctlC: number }>` — the saved
  preset for every mode, keyed by mode id.
- `POST /profiles/:mode { stapmW, fastW, slowW, tctlC } → { mode: ModeId, stapmW, fastW, slowW, tctlC }` —
  persists that mode's preset (what the Power page's "Save preset" writes).

### `GET /app-rules` · `POST /app-rules` · `PUT|DELETE /app-rules/:id` · `POST /app-rules/:id/move`  (per-app profile rules)
A rule says "while this process is in the foreground, run in this mode". Precedence is list order:
the first **enabled** rule whose `match` is a substring of the foreground process name wins, so
reordering is how ambiguity is resolved and two rules can never claim the same process at once.
`match` is normalized on write (trimmed, lowercased, a trailing `.exe` stripped).

The prefix is `/app-rules` and deliberately **not** `/profiles/rules`: `POST /profiles/:mode` above
already claims that space, and a literal segment under a parameterized route would make these
endpoints depend on ASP.NET's literal-vs-parameter precedence rather than on their own path.

- `GET → { rules: AppRule[], modes: ModeId[], autoProfiles: boolean, lastMatch: AppRuleMatch | null }`
  - `AppRule = { id: guid, match: string, mode: ModeId, enabled: boolean, overrides: RuleOverrides | null }`,
    in precedence order. `overrides` is null for a rule that only picks a mode (every seeded rule).
  - `RuleOverrides = { stapmW: number | null, frameCapFps: number | null, fanMode: string | null,
    gpu: { antiLag: boolean | null, chill: boolean | null, rsr: boolean | null, rsrSharpness: number | null,
    ris: boolean | null, risSharpness: number | null } | null, freeze: string[] | null }` — per-game
    settings layered over the mode while that rule's app is settled in front (F1, 2026-09-25). Every
    field null = the mode decides:
    - `stapmW` 5–40 W (the preset band), applied **flat** at the mode's Tctl like a manual value, as
      owner `game-profile`. A manual `POST /tdp` mid-game sits above it; the thermal guardian's
      ceiling is computed under it, and its throttle-clear restore, the resume restore and the 30 s
      reassert all put the game's value back rather than the preset.
    - `frameCapFps` `0` = cap off, else a frame rate. Requested through `GpuDesiredState` after the
      same checks as `POST /gpu/frame-cap` (driver range, the auto-FPS pairing); a refused cap is
      reported in `GET /profiles/active` `skipped`, and the rest of the profile still applies.
    - `fanMode` `Auto` / `Quiet` / `Balanced` / `Aggressive` (not `Manual`). Set on `FanState` but
      **never saved to `fan.json`** — a game's fan is not the user's global preference — and put
      back on exit unless the user changed the fan meanwhile.
    - `gpu` Anti-Lag / Chill over the mode's Radeon profile (`GET /gpu/desired` carries them to the
      agent). Both `true` is refused: AMD's driver excludes the pair.
    - `freeze` bare process names, at most 32. **Stored only** until F5 acts on it.
    
    Leaving the game (after the same ~4.5 s hysteresis as a mode switch, so an alt-tab restores
    nothing) removes the layer: the previous cap, fan mode and Radeon profile come back, and TDP goes
    back to the mode's intent. A mode the user picks by hand over the game ends the profile too.
    A value out of range in a hand-edited `app-rules.json` is repaired on load (watts clamped,
    anything unintelligible dropped to null) rather than costing the rule; a wrong JSON type still
    quarantines the file as before. Unknown fields are ignored.
  - `modes` is what a rule may select: `battery` / `windows` / `gaming` / `ai`. `standby` is excluded
    on purpose — it is a preset for a system state, and a foreground app able to select it would be
    a trap.
  - `autoProfiles` is `GPDFORGE_AUTO_PROFILES != 0`. The rules are stored, readable and editable
    either way; `false` only means nothing is currently applying them.
  - `AppRuleMatch = { ruleId: guid | null, match: string | null, mode: ModeId, process: string | null,
    acConnected: boolean, atUtc: string }` — what decided the mode on the daemon's most recent
    foreground tick. `ruleId: null` means no rule matched and the mode came from the AC/battery
    fallback, so the UI can say so instead of implying a rule is in charge. `null` until the focus
    worker has run at all (it does not run when `autoProfiles` is false). `process` is the app that
    decided, which is not always the foreground: a known non-game window over a game that is still
    running — the overlay (an Edge `--app` window), GPD Forge itself, the shell, Steam's overlay —
    leaves the game deciding, so opening the overlay does not switch the mode (audit round 3,
    2026-09-25).
- `POST { match, mode, enabled?, overrides? } → (the GET shape)` — appends a rule at **lowest** precedence.
- `PUT /app-rules/:id { match, mode, enabled, overrides? } → (the GET shape)` — replaces the rule in
  place, keeping its position. `overrides` **absent keeps** the rule's overrides (the Profiles page's
  enable toggle never sends them), `null` clears them, an object replaces them. `404` if the id is
  unknown.
- `DELETE /app-rules/:id → (the GET shape)`, `404` if the id is unknown.
- `POST /app-rules/:id/move { delta: number } → (the GET shape)` — shifts the rule by `delta`
  positions; negative moves it towards **higher** precedence. Clamped to the ends: a rule already at
  the top asked to move up is a no-op, not an error. `404` only if the id is unknown.

Every mutation answers with the **whole** ruleset, not just the row that changed, so a client can
never end up rendering a list the daemon no longer holds. A rejected rule comes back as
`400 { error: string, code: string }` — the bare-`error` shape, not the `{ error: { code, message } }`
used elsewhere — carrying `GpdForge.Profiles.AppRulePolicy`'s message verbatim (e.g.
`"A rule for 'steam' already exists."`). That message is written for the person reading it and the
UI shows it as-is, so it must not be rewritten or reduced to a status code. `code` (added in F1,
beside the message rather than replacing it) is `bad_rule` for the rule itself and `bad_stapm`,
`bad_frame_cap`, `bad_fan_mode`, `bad_gpu`, `bad_freeze` or `bad_overrides` for the overrides — a
wrong JSON type (`"stapmW": "22"`) gets the field's code too, not a framework error.

### `GET /profiles/active`  (the game profile in force)
`→ { active: boolean, game: string | null, ruleId: guid | null, match: string | null,
mode: ModeId | null, applied: { stapmW, frameCapFps, fanMode, gpu: { antiLag, chill, rsr, rsrSharpness, ris, risSharpness } } | null,
skipped: { field, reason }[], superseded: string[], freeze: string[], sinceUtc: string | null }`
(F1, 2026-09-25).

What the focus loop layered for the ruled game settled in front — the source of the "Elden Ring
profile applied: 22 W · 60 FPS · Aggressive" notice. `applied` lists what the profile still holds
(null fields were left to the mode; `frameCapFps: 0` = cap turned off), `skipped` what the rule asked
for and was refused or is being held off, with the reason, `freeze` the stored list (nothing is
frozen before F5). `game` is the app that decided — the game under the overlay, not the overlay. In
memory only: `active: false` with every field null after a restart until the loop settles again,
always while `GPDFORGE_AUTO_PROFILES=0`, and for a rule that only picks a mode (no overrides).

Audit round 1 (2026-09-25) made every field true at the moment it is read:
- the fan is skipped while fan control is off; the cap and `gpu` are skipped while the GPU-profiles
  gate is closed (a default install, without `-EnableGpuProfiles`) or the agent reports ADLX
  unavailable. A silent agent with the gate open still gets the request (desired state converges);
- `stapmW` moves to `skipped` while another power controller keeps TDP (named) or the thermal
  guardian throttles below it — and returns when that ends;
- `superseded` names the fields the user changed since (`stapmW` — a manual TDP other than the
  game's; `fanMode` — a fan picked by hand; `frameCapFps` — a cap requested since). They leave
  `applied`, and the notice says "changed by you".

Audit round 2 (2026-09-25) checks the Radeon side against the GPU agent, which carries it out a tick
(3 s) after the daemon asks:
- at apply, an `antiLag` / `chill` the agent reports the driver does not support is skipped (field
  `antiLag` / `chill`) and not requested; the other one still applies;
- RSR / RIS (F4): one the agent reports unsupported, or a sharpness outside the driver's range, is
  skipped (field `rsr` / `ris`); the rest still applies. When the game leaves, the values the driver
  held before are requested back — read from the agent's report at apply, or its first report after
  — unless `POST /gpu/image` asked for something since (then `superseded` names `rsr` / `ris` and
  nothing is undone). Never read = nothing is restored and nothing forced off;
- when read, `frameCapFps`, `gpu.antiLag`, `gpu.chill`, `gpu.rsr` and `gpu.ris` stay in `applied` only while the agent's
  report agrees. A report taken 8 s or more after `sinceUtc` that shows something else moves the field
  to `skipped` ("the driver did not take it: it holds no cap"); a silent or stale agent (no report in
  30 s) or one reporting ADLX unavailable moves it there too, as not confirmed, until it reports again.

Rules persist to `%ProgramData%\GPD Forge\app-rules.json`. A fresh install is seeded from the exact
ruleset the daemon used to hardcode (`ModeRules.DefaultRuleSet`), so turning rules into data cannot
silently change day-one behaviour. A corrupt file is quarantined rather than taking the daemon down,
and rows the matcher could not honour (blank match, unknown mode, a duplicate) are dropped on load.

### `GET /frames`  (frame pacing of the FPS target, plan F2)
`GET /frames → { available: boolean, process: string | null, frametimesMs: number[], metrics: FramePacing | null }`

The target's frame times over the last 10 s, oldest first by present time, capped to the newest 1000
(a 380 px graph needs no more, and 240 FPS would be 2 400 per poll). `metrics` is computed over the
whole 10 s before the cap, and is `null` with fewer than two frames. `available: false` (with
`process: null`, `frametimesMs: []`, `metrics: null`) when the FPS gate is closed, PresentMon is
absent or nothing is presenting — "no data", never zeros.

```
FramePacing = { frames: number, spanSeconds: number, fpsAvg: number, fps1PctLow: number,
                fps01PctLow: number, frameTimeStdDevMs: number, stutters: number, stuttersPerMin: number }
```

The lows are 1000 / the mean of the slowest 1 % / 0.1 % of frames (at least one frame, as the
telemetry `fps1PctLow`). A **stutter** is a frame slower than both **2x the rolling median** (31 frames
centred on it, so a scene change from 60 to 30 FPS moves the median rather than reading as a run of
stutters) **and 25 ms** (below that a doubled frame is not a visible hitch). `stuttersPerMin` divides
by the time the frames cover. The overlay draws the graph and shows "Steady pacing" or
"Stutters: N/min".

### `GET /advisor/suggestions` · `POST /advisor/apply` · `POST /advisor/dismiss`  (Forge Advisor, plan F3)
`GET /advisor/suggestions?game=name → AdvisorView` (`game` optional)

```
AdvisorView = { game: string | null, live: boolean, refreshHz: number | null, learnedCeilingW: number | null,
                suggestions: Suggestion[], applied: Applied[] }
Suggestion  = { id: string, game: string, kind: string, title: string, detail: string,
                stapmW: number | null, frameCapFps: number | null, applicable: boolean }
Applied     = { id: string, game: string, kind: string, stapmW: number | null, frameCapFps: number | null, atUtc: string }
```

A pure rules engine (`core/Advisor/AdvisorRules.cs`) over the game's live frame pacing (only when that
game is the one presenting), its last recorded session, its stored profile, the panel's refresh rate
and its **learned thermal ceiling** — the watts at which the thermal guardian settles in that game (a
throttle held for 60 s is one sample into an EMA, α 0.3; ~22 W on this device). Without `?game`, the
game presenting frames, else the session recorder's current app; `game: null` and empty lists when
there is none. Kinds:

| kind | when | writes |
|---|---|---|
| `cap_refresh` | FPS > 1.1× refresh with no cap (or a cap above it) | `frameCapFps` = refresh |
| `cap_30` | live, guardian throttling, 1 % low < 50 % of the average, no cap ≤ 30 | `frameCapFps` = 30 |
| `lower_resolution` | as `cap_30`, but already capped at ≤ 30 | nothing (`applicable: false`; RSR arrives in F4) |
| `fewer_watts` | 1 % low ≥ 1.5× refresh, not throttling, limit known | `stapmW` = 75 % of the limit |
| `stapm_ceiling` | a ceiling is learned (and `fewer_watts` did not fire) | `stapmW` = the ceiling |

`cap_refresh` takes precedence over `cap_30` (the smaller step: capping at 60 alone lifted the 1 % low
from 2–17 to ~30 here). Advice the profile already carries is not repeated. Ids are
`kind:game[:value]`, stable while the advice is the same, so a dismissal holds; dismissed ones are
left out. `applied` is this game's accepted suggestions, newest first (at most 10).

`POST /advisor/apply { id } → AdvisorView` writes that one suggestion into the game's profile (the
F1 rule overrides) — merged into the game's own rule, which is enabled, or a new rule in the mode the
game already runs in, moved ahead of any broader rule — and records it. Nothing else is changed; the
focus loop applies the profile as it would one typed on the Games page. The suggestion is re-derived
from the current state: `400 { code: "bad_id" }` for a malformed id, `409 { code: "stale_suggestion" }`
when it no longer applies, `400 { code: "not_applicable" }` for a hint, and the `/app-rules` codes if
the store refuses a value.

`POST /advisor/dismiss { id } → AdvisorView` hides that suggestion (persisted, last 200 kept);
`400 { code: "bad_id" }` for a malformed id.

### `GET /sessions` · `GET /sessions/games` · `GET|DELETE /sessions/:id`  (play-session history)
A session is one continuous stretch during which a single application presented frames. The only
trustworthy evidence a game is running is that it is *presenting*, and that evidence comes from the
PresentMon probe — which is behind `GPDFORGE_ENABLE_FPS=1`. **With no probe there are no sessions**:
the daemon never manufactures one out of "a game was probably running".

- `GET /sessions?appFilter=<name>&limit=1..500 → { fpsAvailable: boolean, current: string | null,
  sessions: GameSession[] }`, newest first, `limit` defaults to 100. `appFilter` matches the app name
  case-insensitively. (It is `appFilter` and not `app` because `app` is the `WebApplication` in
  `core/Program.cs`.)
  - `fpsAvailable: false` means no frame-rate probe is registered at all — the gate is closed,
    PresentMon is not installed, or Smart App Control blocked it. It is the difference between "you
    have not played anything" and "nothing can ever be recorded", and the UI must say which.
  - `current` is the app presenting right now, or `null` when nothing is being recorded.
    It is whatever the frame target chose, which with no game presenting can be a non-game presenter
    (`dwm.exe`, `chrome.exe`); only `/sessions/games` leaves those out. The overlay's "save as profile"
    ignores them (`FrameTarget.NonGamePresenters`, mirrored in `ui/src/gameProfile.ts`).
- `GET /sessions/games → { fpsAvailable: boolean, games: GameSummary[] }` — the per-app rollup, most
  played first. Averages are weighted by duration, so a two-minute run cannot drag the average of a
  three-hour one around. `packageAvgW` (F1, 2026-09-25) is weighted the same way; the Games page
  shows it next to the FPS so a per-game TDP can be judged against what the game actually draws.
  Known non-game presenters (`FrameTarget.NonGamePresenters`: dwm, explorer, browsers, launchers)
  are left out — on the device dwm.exe headed this list. Their sessions stay in `GET /sessions`.
- `GET /sessions/:id → GameSession`, `404 { error: "session not found" }` if unknown.
- `DELETE /sessions/:id → 204`, same `404` if unknown.

```
GameSession = { id: guid, app: string, startedUtc, endedUtc, durationSeconds: number,
                samples: number, samplesWithoutFps: number,
                fpsAvg, fps1PctLow, fpsMax, cpuTempAvgC, cpuTempMaxC, packageAvgW: number | null,
                onBattery: boolean, batteryStartPct, batteryEndPct, batteryUsedPct: number | null,
                fpsTrend: number[],
                // F2 (2026-09-25); null on sessions stored before it and whenever unmeasured
                fps01PctLow, stuttersPerMin, energyWh: number | null,
                energySource: "battery" | "package" | null, mode: string | null,
                frameCapFps: number | null }
GameSummary = { app: string, sessions: number, totalSeconds: number, lastPlayedUtc,
                fpsAvg, fpsBest, fps1PctLow, cpuTempMaxC, packageAvgW: number | null }
```

Every metric is nullable because every sensor behind it is optional on this hardware: `null` means
*not measured* and is never written as a `0`. `samplesWithoutFps` counts the ticks where the app was
presenting but the probe produced no aggregate, so an average built on partial coverage can be
qualified instead of implying full coverage. `onBattery` is true only when the session ran
*entirely* on battery — a session that saw the charger has no meaningful drain figure, so its
battery fields are `null`. `fpsTrend` is downsampled to at most 120 points at close time (a 3-hour
session at 1 Hz would otherwise put megabytes of JSON on the system drive for a 120 px graph).

F2 (2026-09-25) adds frame pacing and cost per session. `fps01PctLow` is the worst per-second 0.1 %
low (the same percentile-of-windows rule as `fps1PctLow`); `stuttersPerMin` is the mean of the
per-second readings, each already a rate over the last 10 s (see `GET /frames`). `energyWh` integrates
power over the session's ticks, each step capped at 5 s so a sleep/resume gap is not billed at the
last reading: `energySource: "battery"` is the whole-machine drain and is used only when the session
ran entirely on battery; otherwise `"package"`, the APU's power. `mode` and `frameCapFps` (the
requested driver cap; `null` = uncapped) are the ones the session spent most of its ticks in.

Sessions persist to `%ProgramData%\GPD Forge\sessions.json`, capped at 200 rows / 90 days, with the
same atomic-write + quarantine-on-corrupt handling as the alert store. A session shorter than 60 s is
dropped rather than stored (that is a launcher splash or a menu, not play), and a gap of 60 s without
presents ends the session (loading screens and alt-tabs routinely produce 10-30 s gaps).

### `POST /import/motionassistant`  (MotionAssistant `.ini` profile importer)
`200 → { found: number, profiles: ImportedProfile[], path: string }` — reads every `*.ini` file
under MotionAssistant's saved-profiles directory (default `C:\Program Files\Motion
Assistant\Profiles`) and parses each `[ProfileName]` section into an `ImportedProfile`. Read-only
and tolerant: an absent directory or a malformed file never throws — worst case is `found: 0` with
`profiles: []` and `path` set to where GPD Forge looked. This endpoint only *returns* the parsed
profiles; to apply one, POST its numbers to the existing `POST /profiles/:mode`.

### `GET /system/incumbents`  (first-run setup wizard)
`200 → { motionAssistant: boolean, gpdTool: boolean }` — whether MotionAssistant / GPD Tool is
currently running, reusing the same `IPowerControllerDetector` (`ProcessPowerControllerDetector`,
watching `MotionAssistant`/`pmgui` and `GPDTool`/`GPDToolService`) that `ProfileApplier` already
yields to — so the wizard's advice and the daemon's actual yield-while-running behavior can never
disagree. Read-only. The setup wizard calls this once on its incumbents-check step: if either is
`true`, it advises running the installer with `-Substitute`; otherwise it reports clear.

### `GET /power-source`  ·  `POST /power-source`  (per-power-source auto mode-switch)
- `GET → { enabled: boolean, onBatteryMode: ModeId, onAcMode: ModeId }`
- `POST { enabled?, onBatteryMode?, onAcMode? } → { …config }` — partial update (only sent fields
  change; a blank/whitespace mode string is ignored rather than clearing the field).

When enabled, the daemon switches the active mode the instant AC connects or disconnects (edge-
triggered, not every tick) — e.g. auto-drop to Battery mode on unplug, back to Windows mode on
plug-in. Applied the same way `POST /mode` is: through `ProfileApplier`, which yields if another
power controller (MotionAssistant/GPD Tool) is running.

### `GET /fan`  ·  `POST /fan`  (fan mode + manual duty — WRITES are GATED)
- `GET → { mode: 'Auto' | 'Quiet' | 'Balanced' | 'Aggressive' | 'Manual', manualDuty: number,
  controllable: boolean }`
  - `POST { mode?: string, manualDuty?: number } → (same shape as GET)` — `mode` must be exactly one
    of `Auto` / `Quiet` / `Balanced` / `Aggressive` / `Manual` (`400 bad_mode` otherwise);
    `manualDuty` (0–255, clamped) is the fixed duty used only while `mode === 'Manual'`.
    `controllable` is true only when a matched board's EC port is actually open and writable.

The daemon always stores the preference (so the UI round-trips even with the gate closed). Applying
it to hardware requires **both** `GPDFORGE_ENABLE_HARDWARE=1` **and** a second, separate opt-in
`GPDFORGE_ENABLE_FAN_CONTROL=1` (fan writes are gated more strictly than other hardware writes — see
`core/Fan/GpdFanController.cs`) — with both set and a matched board, `FanWorker` drives the EC once a second on its own timer
(`core/Fan/FanWorker.cs`, independent of the TDP loop, reading the latest cached telemetry sample): `Auto` restores automatic (once, on the transition), `Quiet`/`Balanced`/`Aggressive` compute a
duty from a temp→duty curve with hysteresis (`core/Fan/FanCurve.cs`) via `FanMath`'s PWM-scale cast
  (`core/Fan/FanMath.cs`), and `Manual` holds `manualDuty`. A safety floor (`GpdFanController.MinManualDuty`,
  40/255) means GPD Forge never commands a near-stopped fan, and AUTOMATIC is always restored on
  service shutdown. If CPU temperature telemetry is absent/non-finite, curve modes fail safe to
  firmware AUTOMATIC instead of interpreting the missing `0` reading as a cold CPU; a missed reading, or a telemetry
  sampler that stops publishing, is bridged for up to 3 s before that happens. With either gate
  closed, an unmatched board, or an unavailable EC port, `controllable:false` and nothing is written.

### `GET /display`  ·  `POST /display/brightness`
- `GET → { brightness: number }` — 0–100, read live over WMI.
- `POST /display/brightness { level: number } → { brightness: number }` — clamped to 0–100.

### `GET /display/refresh`  ·  `POST /display/refresh`  (refresh-rate switching — REAL)
- `GET → { current: number, supported: number[] }` — the primary display's current refresh rate
  (Hz) and every rate it supports at the current resolution/color depth, read live via
  `EnumDisplaySettingsEx`.
- `POST { hz: number } → { current, supported, error: string | null }` — switches via
  `ChangeDisplaySettingsEx`, applied for this session only (not written to the registry, so a bad
  pick never survives a reboot). `hz` must be one of `supported`; otherwise `current` is left
  unchanged and `error` explains why.

### `GET /display/night`  ·  `POST /display/night`  (warm-screen night mode — REAL, gamma ramp)
- `GET → { on: boolean, warmth: number }`
- `POST { on: boolean, warmth?: number } → { on, warmth }` — warms the screen via the GDI gamma
  ramp (`SetDeviceGammaRamp`), reducing blue (and, less, green) as `warmth` (0–100) rises;
  `warmth` always reports what's actually applied right now, so `on:false` reports `warmth: 0` (the
  identity ramp really is what's on screen, not just remembered). **This is not Windows Night
  Light** — that feature's state lives in an undocumented, build-fragile registry blob GPD Forge
  deliberately does not touch; this is an independent, real, fully reversible gamma-based warm mode.

### `GET /display/tablet`  ·  `POST /display/tablet`  (tablet-mode advisory — ADVISORY, GATED)
- `GET → { convertible: boolean | null, raw: number | null, applied: false, advisory: string }` —
  reads the `ConvertibilityEnabled` registry DWORD
  (`HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl`), the documented Windows 11 22H2+
  override for a device's chassis-type/DeviceForm convertible detection (the Win 4 reports as a
  convertible in SMBIOS, the root of its known "everything opens maximized" behavior). `raw: null`
  means the value isn't set (default OS detection applies).
- `POST { enable: boolean } → { convertible, raw, applied, advisory }` — writes `1` (convertible)
  or `0` (the known fix) to that value. **Gated behind `GPDFORGE_ENABLE_HARDWARE=1`**: with the gate
  closed this only reads and returns `applied:false` with an advisory explaining why — the registry
  is never written otherwise.

### `GET /display/keyboard-backlight`  ·  `POST /display/keyboard-backlight`  (ADVISORY)
`200 → { controllable: false, applied: false, advisory: string }` for both verbs. The Win 4's
keyboard backlight is EC/Fn-controlled — the same access path already blocked on this board's
firmware (see the `--probe-ec` notes in `core/Program.cs`) — so GPD Forge has no verified write
path and never attempts a blind one; this always reports the honest advisory rather than faking
success.

### `GET /led`  ·  `POST /led`  (RGB/LED — ADVISORY, GATED)
- `GET → { mode: 'Off'|'Solid'|'Breathe'|'Rotate', color: string, controllable: false, applied: false,
  advisory: string }` — the last-set (or default) desired config GPD Forge is holding. There is no
  readable live state for this board, so this is never a live read.
- `POST { mode: string, color?: string } → (same shape as GET)` — `mode` must be one of `Off` /
  `Solid` / `Breathe` / `Rotate` (`400` otherwise); `color` is `#RRGGBB` or `RRGGBB`, case-
  insensitive (`400` on a malformed color). The desired config is always stored (so the UI round-
  trips), but a WRITE is only attempted when `GPDFORGE_ENABLE_HARDWARE=1` — and even then this
  always reports `applied:false`: the LED sits on the same HID feature-report config interface as
  the controller's button/deadzone blob (`core/Hid/SafeConfigWriter.cs`), which on this HX370 unit
  is already known to reject the very first `HidD_SetFeature` call (see
  `docs/overlay-home-button.md`). GPD Forge never blind-writes it; see `core/Led/LedService.cs`.

### `GET /battery/charge-limit`  ·  `POST /battery/charge-limit`  (ADVISORY, GATED)
- `GET → { percent: number, available: boolean, applied: false, advisory: string }` — `available`
  is `true` only if a driverless source could report the live threshold; today none is known for
  this board, so this is the last-set (or default, 100) value GPD Forge is holding.
- `POST { percent: number } → (same shape as GET)` — `percent` is clamped to 50–100
  (`core/Battery/ChargeLimit.cs`'s `ChargeLimitValidator.Normalize`). A WRITE is only attempted when
  `GPDFORGE_ENABLE_HARDWARE=1`; "stop charging at N%" is an EC/BIOS feature with no verified,
  driverless write path on this board, so this always reports `applied:false` + why rather than a
  blind write — see `core/Battery/ChargeLimitService.cs`.

### `GET /undervolt`  ·  `POST /undervolt`  (Undervolt / Curve Optimizer — ADVISORY, GATED)
- `GET → { coCount: number, offsetMv: number, applied: false, advisory: string }` — the last-set (or
  default, `0`/`0`) desired values GPD Forge is holding.
- `POST { coCount?: number, offsetMv?: number } → (same shape as GET)` — `coCount` (AMD PBO Curve-
  Optimizer magnitude; negative = undervolt) is clamped to -30..+30, `offsetMv` to -100..+100
  (`core/Undervolt/CurveOptimizer.cs`'s `CurveOptimizerValidator`). Always `applied:false`: RyzenAdj,
  the only TDP backend this project drives (`core/Tdp/RyzenAdjBackend.cs`), does not expose Curve
  Optimizer / PBO at all, so there is no implemented write path regardless of the hardware gate —
  see `core/Undervolt/CurveOptimizerService.cs`.

### `GET /battery/budget`
`200 → { minutesRemaining: number | null, remainingWh: number, dischargeW: number,
  projections: Array<{ watts: number, minutes: number }> }`
Runtime estimate from the current discharge rate, plus what-if runtimes at a spread of power levels.
`minutesRemaining` is `null` on AC (nothing to project).

### `GET /battery/charge-guard`  ·  `POST /battery/charge-guard`

`GET → { enabled, highSocPct, alertAfterHours, coolWhileCharging, coolToW, totalHoursAtHighSoc,
  episodes, episodeStartedUtc, episodeHours, canStopCharging, advisory }`

**`canStopCharging` is always `false`, and it is in the contract rather than omitted.** Anything
called a charge guard invites the assumption "it stops at 80 %"; without the field a client could
reasonably build that switch. This board has no path to it — the threshold is an EC/BIOS value with
no verified driverless read or write on the G1618-04, `docs/hardware/ec-registers.md` maps fan
registers only, and the `ecoChargeMode` WMI class that looks like the answer is Windows' own schema
with **no instances** here. Guessing an EC register for a charge controller on hardware with no
vendor recovery path is not a risk worth taking.

So the guard attacks the half of the problem that is reachable. Lithium-ion ages from **time at a
high state of charge multiplied by temperature**; the daemon cannot stop the current, but it can:

- **Count** the hours the pack spends plugged in at or above `highSocPct` (default 95), across
  episodes, persisted. `episodeHours` is `null` when none is running — never `0`, which would read
  as one that just began.
- **Warn once per episode** past `alertAfterHours` (default 4), naming the hours. Once per episode,
  not once per tick: republishing every second and relying on the alert store to coalesce is a guard
  hiding its own noise.
- **Hold a cooler ceiling** while an episode runs, if `coolWhileCharging` is on. **Off by default** —
  silently capping someone's performance because their machine is plugged in is not a decision to
  make on their behalf.

⚠️ The cooling value is a **ceiling, never a target**. In battery mode (8 W) a 15 W "cooling" setting
must not raise the limit, so it applies only when it is genuinely lower than the active mode. And it
is released when the episode ends — on unplug, on falling below the threshold, or on the guard being
disabled mid-episode. A guard that lowers TDP and forgets to restore it is worse than no guard.

`POST` accepts any subset of the five settings; omitted fields keep their current value rather than
reverting to defaults. Values are clamped, not rejected: `highSocPct` to 50–100, `alertAfterHours` to
0.25–72, and `coolToW` to 8–30 — never below the thermal guardian's own floor, since a "cooling"
ceiling that starved the machine harder than the safety net is a stall, not cooling.

**If you want a real charge threshold**, the cheapest next step is not code: check whether BIOS setup
on this board exposes one (hold `DEL` during boot). Several GPD handhelds do. If it is there, the
right shape for GPD Forge is the one `GET /firmware` already uses — report it and say where it is,
and write nothing.

### `GET /battery/health`  (how much of the pack's factory capacity survives)
`200 → { designedMwh, fullChargeMwh, healthPercent, cycleCount, cycleCountUnavailable,
  cellTemperatureC, cellTemperatureUnavailable, chemistry, unavailable, degradationPoints,
  trendUnavailable, samples: Array<{ atUtc, fullChargeMwh, healthPercent }> }`

`healthPercent` is `fullChargeMwh / designedMwh`. On the reference device: **40,009 of 43,890 mWh —
91.2 %**, matching `powercfg /batteryreport` exactly.

**Nearly every field is nullable, and that is the design.** Two of the four things anyone wants here
are not available on this board, and each null carries its own reason string:

- **`cycleCount` is `null`, never `0`.** Both `powercfg` and the `BatteryCycleCount` WMI class report
  0 for a pack that has demonstrably lost 8.8 % of its capacity — so 0 means "the EC does not keep
  this number". Reporting it would print *0 cycles* beside *91 % health* and leave the reader to
  resolve the contradiction.
- **`cellTemperatureC` is `null`** — the `BatteryTemperature` WMI class has no instances here.
- **`degradationPoints` is `null` until two samples exist on different days**, with
  `trendUnavailable` explaining the wait. One reading is a value, not a trend, and this pack loses
  single-digit percent over *years*, so anything sub-daily is measurement jitter.

Where the numbers come from differs by field, deliberately: full-charge capacity is a live, cheap WMI
read, while design capacity exists only in `powercfg /batteryreport` (a process spawn and a 76 KB
report) and is a factory constant — so it is read once and cached to disk.

Nothing here writes to the battery, and nothing can. A charge threshold is a separate matter with no
verified path on this board — see `GET /battery/charge-limit` above.

### `GET /freezer`  ·  `POST /freezer/freeze`  ·  `POST /freezer/thaw`
Suspend/resume background processes to free CPU/RAM during a game or a heavy inference run.
- `GET → { frozen: string[] }` — process names currently suspended.
- `POST /freezer/freeze { name: string } → { name, suspended: number, frozen: string[] }` — suspends every
  matching process. Critical system processes are on a protected list and are never suspended.
- `POST /freezer/thaw { name: string } → { name, resumed: number, frozen: string[] }` — resumes them.

### `GET /auto-fps`  ·  `POST /auto-fps`  (Auto-TDP to a target FPS)
- `GET → { enabled: boolean, targetFps: number }`
- `POST { targetFps: number, enable: boolean } → { enabled, targetFps }` — a PID loop then steers sustained
  TDP to hold `targetFps` at the least power, active in gaming mode once FPS telemetry is available.
  It writes only when its STAPM changes what is in force: inside the ±2 FPS deadband (or pinned at
  8/30 W) nothing is written, and firmware reverts are left to the 30 s reassert. It steers from the
  last TDP actually written — after a mode switch, the mode's preset — not from a stale counter.

### `GET /tuner`  ·  `POST /tuner/start`  (auto-tuner TDP sweep)
- `GET → { running: boolean, goal: 'MaxFps'|'BestEfficiency'|'HoldTarget', targetFps: number|null,
  minW: number, maxW: number, tempCapC: number, currentStapmW: number,
  points: Array<{ stapmW: number, fps: number, tempC: number }>,
  best: { stapmW: number, fps: number, tempC: number, note: string } | null, note: string | null }` —
  current sweep state.
- `POST { goal: string, targetFps?: number, minW?: number, maxW?: number, tempCapC?: number } →
  (same shape as GET)` — (re)starts a sweep from `minW`, clearing any previously recorded points.
  `400` if `goal` isn't one of `MaxFps` / `BestEfficiency` / `HoldTarget`. `minW`/`maxW` are clamped
  into the safe TDP band (5–40 W) and normalized if swapped; omitted bounds keep the previous
  sweep's values (defaults: 8–30 W, 95 °C cap).

The worker steps the sweep once per tick: hold each candidate STAPM (a flat profile — no boost above
it, so any FPS change is attributable to STAPM alone) for `TunerState.DwellTicks` ticks, then record
one `(stapmW, fps, tempC)` point and move to the next candidate (`TunerState.StepW` watts higher),
until `maxW` is covered. `best` is picked by `AutoTuner.PickBest` for the configured `goal`, among
points at or under `tempCapC`: **MaxFps** — highest fps; **BestEfficiency** — highest fps-per-watt;
**HoldTarget** — lowest watts whose fps still meets `targetFps`. Any of these can come back `null`
(no points yet, everything over the temp cap, or the target unreachable) — that's an honest "nothing
usable" rather than a guess.

**Honesty note:** FPS telemetry is wired (Intel PresentMon behind `GPDFORGE_ENABLE_FPS=1`, see
`core/Telemetry/PresentMonFrameRateProbe.cs`), but it only reports while something is actually
presenting frames. With nothing rendering — or in a Remote Desktop session, where there is no normal
GPU present chain to observe — `Fps` stays 0, and that 0 means "not available", never "zero frames".
A sweep run in that state records nothing (a non-positive `fps` reading is never recorded — see
`TunerState.Tick`), so it finishes with `points: []`, `best: null`, and `note` explaining why. GPD
Forge never fakes an FPS reading to produce a result. The mock daemon simulates a small FPS curve so
the UI/E2E can exercise a populated sweep in dev without real hardware.

### `GET /guardian`  ·  `POST /guardian`  (thermal / battery guardian)
- `GET → { enabled, autoThrottle, tempThrottleC, tempCriticalC, throttleFloorW, batteryLowPct,
  batteryCriticalPct, throttling: boolean, throttledToW: number | null, lastAlert: string | null,
  lastSeverity: 'ok'|'info'|'warn'|'critical' }` — config + live guardian state.
- `POST { enabled?, autoThrottle?, tempThrottleC?, tempCriticalC?, throttleFloorW?, batteryLowPct?,
  batteryCriticalPct? } → { …config }` — partial update (only the sent fields change).

The worker evaluates every tick: above `tempThrottleC` it eases the STAPM ceiling down a ramp to
`throttleFloorW` by `tempCriticalC` (a safety throttle that takes priority over Auto-TDP-to-FPS), and
clears once temps recover; on battery it raises low/critical alerts. Throttle actions are gated by
`autoThrottle`; alerts always surface via `lastAlert`.

### `GET /health/check`  (system health check / anomaly detection)
`200 → { status: 'ok'|'warn'|'critical', issues: Array<{ level: string, code: string, message: string }> }`
Pure rules (`GpdForge.Health.HealthCheck.Evaluate`, unit-tested exhaustively) evaluated against a REAL
live telemetry snapshot — never a hardware write, purely diagnostic. `status` is the max severity
across `issues` (`ok` when empty). Rules today, by the `code` each emits:
- `telemetry_stale` → warn, listed first — the sampler's last reading is more than 3 s old (three
  1 Hz ticks), or no sample has been taken yet. Added 2026-09-24: the endpoint serves a cached
  sample, and a sampler whose hardware read hangs keeps serving its last one, so grading the snapshot
  alone answered `ok` while the thermal guardian had stopped. The rules below still run on the stale
  snapshot — what the machine last looked like is still worth knowing.
- `telemetry_unavailable` → warn — `cpuTempC` is null, so the two thermal rules below cannot run at
  all. Reported rather than skipped: a health check that cannot measure and answers `ok` is worse
  than one that answers nothing.
- `fan_not_spinning` → warn — fan reads 0 rpm while `cpuTempC` is above 70 °C (this literally catches
  a parked-fan-while-warm state). Requires a real rpm reading; a null fan source does not fire it.
- `thermal_critical` → critical — `cpuTempC >= 95`.
- `tdp_not_holding` → warn — `tdpVerified` is **`false`** (firmware silently reverting TDP). Not
  `!tdpVerified`: null means nothing has written a limit yet, and warning about a write that never
  happened would fire on every freshly started daemon.
- `high_discharge` → warn — on battery with `dischargeW > 30`.
- `ac_unknown` → warn — the battery query failed, so `acConnected` is the cautious fallback
  (`acKnown: false` on `GET /telemetry`) and the AC/battery mode switch and the per-app rules are
  paused until it recovers. Added in audit round 2 (2026-09-24); that pause was a service-log line only.
- `foreground_unreported` → warn — the daemon runs in session 0 and no session agent has reported the
  app in front within 10 s (`GET /session/foreground` answers `source: "local"`), so the FPS target and
  the per-app rules cannot see what is running. Never raised when the daemon itself runs in a user
  session (a dev run), where the local answer is the right one. Audit round 2 (2026-09-24).

The
System page's health card polls this and shows a green "All good" when `issues` is empty, or the
issue list colored by severity otherwise.

### `POST /jobs`  ·  `GET /jobs`  (Agents / AI mode)

> ⚠️ **This endpoint records jobs. It does not run them.** There is no executor and no scheduler in
> the daemon: `cmd` is checked for emptiness, stored, and never passed to a process. Read the rest of
> this section as a description of a registry, not a queue.

- `POST { cmd: string, constraints?: { requireAC?: boolean, maxTempC?: number, window?: string } }`
  `→ { id: string, status: 'running' | 'blocked' }`
  `requireAC` is evaluated **once**, at POST time, against a live telemetry read: on battery the job
  is recorded as `blocked`, otherwise `running`. `maxTempC` and `window` are accepted and discarded —
  `JobsState.Add` takes the constraints as `_`. Status never changes afterwards, because nothing
  advances it.
- `GET /jobs → Array<{ id: string, cmd: string, status: string }>`

**What this document claimed until 2026-09-02**, all of it false: a `GET /jobs/:id` route (no such
route is registered); fields `startedAt`, `finishedAt` and `log: string[]` on the job (the record is
`Job(Id, Cmd, Status)` — those three fields have never existed); statuses `queued` and `done` (only
`running` and `blocked` are ever produced, and `Finish`, which would set `done`, has no callers); and
a scheduler that "runs the job only while its constraints hold", offered with the example *"run this
batch only on AC, under 80 °C, between 02:00–07:00"* — two of those three constraints are discarded
and the batch is never run at all.

The one real behaviour this endpoint used to have was a bug: a `running` job took an anti-standby
hold that nothing could release, silently defeating the Standby Doctor for the remaining uptime of
the service. That was removed on 2026-09-02; see the comment on `JobsState.Add`.

### `GET /ai`  ·  `GET /ai/inference-hold`  ·  `POST /ai/anti-standby`  ·  `POST /ai/vram`  (Agents / AI mode — anti-standby, sustained profile, VRAM/UMA)
- `GET /ai → { antiStandby: { active: boolean, holders: number, manual: boolean }, sustainedProfile:
  { stapmW, fastW, slowW, tctlC }, vram: { reportedMb: number, adapterName: string | null,
  available: boolean, advisory: string } }`
  - `antiStandby` — whether GPD Forge is currently holding Windows awake (`SetThreadExecutionState`,
    `ES_CONTINUOUS | ES_SYSTEM_REQUIRED`) and how many concurrent holders there are. Each running job
    from `POST /jobs` and the manual toggle below each hold independently (ref-counted); the Win32 call
    only fires on the 0→1 / 1→0 edges. **REAL** — an unprivileged, fully reversible power request, not
    gated behind `GPDFORGE_ENABLE_HARDWARE` (it isn't a hardware/BIOS write).
  - `sustainedProfile` — a FLAT preset (`stapmW = fastW = slowW`, no boost above the sustained target)
    shaped from the current `ai` mode preset via `ProfileShaper`. Informational; apply it through the
    normal `POST /profiles/ai` + mode-switch / `POST /tdp` flow.
  - `vram` — the iGPU's current UMA/VRAM allocation, read live over WMI
    (`Win32_VideoController.AdapterRAM`, driverless, no elevation). **READ-ONLY**: the frame-buffer
    split is a BIOS/GOP setting applied at boot, not something Windows lets user-mode reassign; `advisory`
    always explains that changing it needs BIOS setup or a reboot.
  - `vram.history: { kind, summary, previousMb: number | null, sinceUtc, bootUtc, rebootConfirmed:
    boolean }` — the reading persisted across runs so a BIOS edit can be **confirmed** instead of
    assumed. `rebootConfirmed: false` means a reboot between the two readings could not be
    *established*, **not** that none happened. ⚠️ `Win32_VideoController.AdapterRAM` is a uint32 that
    **saturates at 4095/4096 MB**, so a value at that ceiling is the ceiling, not a measurement of the
    split — a delta involving it is never reported as a confirmed change. Render `summary`; do not
    re-derive a verdict from the numbers.
  - `inferenceHold: { enforcing, holding, holdingSince: string | null, workers: [...] }` — a summary of
    `GET /ai/inference-hold` below, so the panel needs only one request.
- `GET /ai/inference-hold → { enforcing: boolean, holding: boolean, holdingSince: string | null,
  lastTickAt: string | null, reason: string | null, watchedNames: string[], busyCpuFraction: number,
  workers: [{ pid, name, cpuFraction: number | null, busySince }],
  unmeasured: [{ name, pid: number | null, why }] }` — the keep-awake for inference GPD
  Forge did **not** start (`ollama`, LM Studio, `llama-server`, a training script in a terminal).
  - `unmeasured` — watched processes we could **not read**, which is a different fact from "not
    working". An unelevated daemon cannot read an elevated `ollama`'s CPU time; without this list the
    endpoint would report *"no sustained inference work"* confidently and wrongly. Failing to measure
    biases toward letting the machine **sleep** (never toward holding), so an unmeasurable process is
    reported, not held for.
  - The hold is earned by sustained CPU work attributable to a watched process, never by the process
    merely being resident: an idle `ollama serve` sits there 24/7, and holding for it recreates the
    all-night drain this project removed on 2026-08-29.
  - `enforcing` is **false by default**. The worker always samples and always reports what it *would*
    hold for; it only takes a real hold when `GPDFORGE_INFERENCE_HOLD=1`. The feature collects the
    evidence for its own enforcement before it is allowed to act.
  - The nulls are load-bearing. `lastTickAt` is null until the worker has ticked, `holdingSince` is null
    when nothing is held, and `cpuFraction` is null when a tick produced no usable measurement (new PID,
    recycled PID, stepped clock, or CPU time we were refused). **Render null as "—", never as 0** — 0
    reads as "idle" when the truth is "unknown". `cpuFraction` is a fraction of the *whole machine's*
    CPU capacity, not of one core.
  - **REAL**, not gated behind `GPDFORGE_ENABLE_HARDWARE` — same unprivileged power request as
    `antiStandby` above.
- `POST /ai/anti-standby { enable: boolean } → { active, holders, manual }` — manual override. Only the
  `false→true` / `true→false` edge touches the ref count, so re-posting the same value is a no-op (never
  double-acquires or double-releases the hold).
- `POST /ai/vram { requestedMb?: number } → { reportedMb, adapterName, available, applied: false,
  requiresBiosReboot: true, advisory }` — always `applied:false`: GPD Forge does not perform a blind UMA
  write (see `vram` above). Honest by construction rather than faking success.

### `GET /gpu`  ·  `POST /gpu/state`  (AMD Radeon profiles via ADLX)
`200 → { available: false, status, detail, adapter, lastReportUtc }`
`200 → { available: true, status, adlxVersion, adapter, detail, lastReportUtc, settings, modeProfiles }`

Anti-Lag, Chill, Boost, Image Sharpening, the driver's own frame-rate cap (FRTC) and, since F4,
Radeon Super Resolution (`settings.superResolution`: `value` = its sharpness, `min`/`max` = the
driver's sharpness range; `null` on a driver whose ADLX lacks the RSR interface). `imageSharpening`
now carries its sharpness range too.

🔴 **The daemon cannot read these itself, and does not pretend to.** Measured 2026-08-29: identical
code initialises ADLX from an interactive session and fails under the service with *"ADLXInitialize
did not return a system interface"* — the service is LocalSystem in **session 0**, and ADLX needs the
display driver stack of an interactive session. The ADLX calls therefore run in a **GPU agent** in the
user's session (`dotnet GpdForge.Service.dll --gpu-agent`, the same assembly so no new unsigned binary
is introduced), which posts to `POST /gpu/state`. Everything `GET /gpu` returns is second-hand.

- `lastReportUtc` is when the agent last checked in; the daemon stamps arrival itself rather than
  trusting a clock it does not control, since freshness is the one thing that endpoint establishes.
- A report older than 30 s is returned but marked `available:false`, with a detail saying how long it
  has been quiet — the values describe that moment, not now.
- `status: "NoAgent"` with `lastReportUtc: null` means **nothing has looked yet**. That is a different
  answer from the agent reporting ADLX unavailable, and telling a user their GPU cannot be controlled
  when the truth is "we have not checked" sends them hunting for a hardware fault.

- **`available: false` means the client renders NOTHING**, not a disabled row. A greyed-out control
  still reads as "nearly working" when the honest answer is "this machine cannot" or "you have not
  switched it on". `detail` says which.
- `settings.<feature>` is `{ supported, enabled, value }` **or `null`**. The three not-on states are
  different facts and must not be collapsed: `null` = the driver did not answer, `supported:false` =
  this GPU cannot do it, `enabled:false` = it can and it is off.
- `modeProfiles` is what each mode will apply when it becomes active, so the panel can say what is
  about to happen rather than only what happened.

**How the automatic part works.** The GPU profile hangs off the **mode**, not off each per-app rule.
The rules in `GET /app-rules` already map a foreground process to a mode, so attaching the GPU there
would mean a second matching system to keep in step with the first. Every path that sets a mode — the
focus worker, a manual switch, the AC/battery rule, the standby restore — applies the GPU profile
through `ProfileApplier`, without knowing ADLX exists.

⚠️ **AMD refuses Radeon Chill together with Boost or Anti-Lag**; it does not merge them. Profiles are
applied in an order that turns the conflicting feature off first, and a profile that requests the
forbidden pair is rejected with a reason rather than sent and silently half-applied.

#### `POST /gpu/frame-cap`  ·  `GET /gpu/desired`  (the driver's real frame cap, FRTC)
`POST { fps: number | null } → { applied: false, pending: boolean, requested?, reason }`

A **real** cap: the driver holds each frame back. Distinct from the Power page's auto-FPS, which
steers TDP toward a target and does not stop the GPU exceeding it. `fps: null` disables it.

- **Never answers `applied: true`.** The daemon cannot reach ADLX, so it records an intent and the
  agent reconciles within a few seconds; `GET /gpu` then reports what the driver actually did. An
  endpoint claiming success for work that has not happened is the thing this project keeps deleting.
- `400` with the driver's real limit when the value is out of range (this device reports **15–1000**),
  so a refused value teaches what would work. `409` when no agent is reporting or the GPU has no FRTC.
- ⚠️ **`409` when the cap would sit below an ACTIVE auto-FPS target.** Auto-FPS steers TDP to *reach*
  a rate; FRTC refuses to *exceed* one. A cap under the target makes auto-FPS raise power forever
  chasing frames the driver is holding back — hot, loud, no extra frames, and no error anywhere. The
  same check runs on `POST /auto-fps`, so it cannot be walked around from the other side. A disabled
  auto-FPS never blocks a cap: its target governs nothing.
- `GET /gpu/desired` is what the agent reconciles towards (the overlay also reads it, to save the
  cap a game profile asked for before the agent has carried it out). `requested: false` means nobody has asked for anything and the GPU must be left alone — starting the daemon is not a
  reason to change someone's Adrenalin settings. Desired state rather than a command queue, so an
  agent that restarts or misses ticks converges instead of replaying.
- `GET /gpu/desired` also carries `capVersion` (`number`, F1 audit round 4), which rises on every cap
  request. With `requestedAtUtc` it identifies the request: the agent carries out a NEW request even
  when its value is the one it last wrote (the user may have moved the cap in Adrenalin since), and
  never re-asserts an OLD one over the user's change.
- `GET /gpu/desired` also carries `antiLag` / `chill` (`boolean | null`, F1): a game profile's Radeon
  features, which the agent layers over the mode's profile while the game is in front and drops when
  it leaves. A game's Chill turns the mode's Anti-Lag off (and vice versa) instead of sending the pair
  the driver refuses. `null` = the mode decides; independent of `requested`, which is about the cap.

#### `POST /gpu/image`  (Radeon Super Resolution and Image Sharpening, F4)
`POST { rsr?: boolean | null, rsrSharpness?: number | null, ris?: boolean | null, risSharpness?: number | null }
→ { applied: false, pending: boolean, requested?, reason }`

Same contract as `POST /gpu/frame-cap`: an intent the agent carries out within a few seconds, never
`applied: true`. Omitted / `null` fields are left as the driver has them. `400` for an empty body or a
sharpness outside 0–100 or the driver's reported range; `409` when no agent is reporting or the agent
reports the feature unsupported (or its interface unobtainable). `GET /gpu/desired` carries it as
`image` (`{ rsr, rsrSharpness, ris, risSharpness } | null`) and `imageVersion` (`number`, rises on every
request): the agent writes each request ONCE, so a change made in Adrenalin afterwards is kept.

⚠️ **Order:** Boost is turned OFF before RSR goes on (AMD does not run RSR with Boost; both change the
render resolution), and each feature is ENABLED before its sharpness is written, as FRTC requires. No
sharpness is written for a feature being turned off. RSR is a system-wide setting (ADLX obtains it
without a GPU); RIS is per GPU. Modes never turn either on: they change how the picture looks.

⚠️ **Order is forced by the driver:** FRTC must be ENABLED before its FPS can be written. The
intuitive order (value first, so enabling never briefly applies a stale cap) returns `ADLX_FAIL`
(rc=3) — measured on device. The accepted cost is that enabling re-applies the previous cap for an
instant before the new one lands.

**Gated** behind `GPDFORGE_ENABLE_GPU_PROFILES=1` (installer: `-EnableGpuProfiles`). Its own gate, not
the hardware one: ADLX is a user-mode driver API with nothing to do with the MSR/EC paths, and a fault
here must not be able to take down power control that has been validated on the metal.

**Implementation note.** ADLX is reached through its C interface with hand-written vtable offsets,
because AMD's documented C# route needs SWIG plus a C++ compiler and produces an unsigned native DLL —
which is exactly what Smart App Control blocks on this hardware. A wrong slot index calls an arbitrary
driver function, so the layout is transcribed from the SDK headers and **verified at startup**: the
daemon calls `TotalSystemRAM` and checks it against the machine's RAM read over WMI. Disagreement
means the library is marked unusable and nothing else is called through it. `--probe-gpu` reproduces
that check and writes nothing.

### `GET /session/foreground`  ·  `POST /session/foreground`  (what is in front, seen from your session)
- `POST { process: string | null } → { accepted: true }` — posted by the session agent (`--gpu-agent`)
  every 3 s, whether or not GPU profiles are enabled (the installer always starts it; the gate
  governs only its ADLX half). The agent checks the status: a refusal is logged at Warning once per
  outage.
  `process` is the foreground process name without a path or `.exe` (`eldenring`, `GPD Forge`), or
  `null` when nothing is in front (the lock screen, the bare desktop). `400 bad_process` for anything
  else: an empty string, a path, a control character, more than 260 characters.
- `GET → { process: string | null, source: "agent" | "local", ageMs: number | null }` — the answer the
  FPS target and the auto-profile worker use, and where it came from. `source: "agent"` means the agent
  reported within the last **10 s** (`ageMs` says how long ago); otherwise `source: "local"` and
  `ageMs: null`.

🔴 **Why this exists.** The service is LocalSystem in **session 0**, which has no interactive desktop,
so `GetForegroundWindow` there is always NULL. Until 2026-09-24 that made the installed daemon blind
to what the user was doing: `GET /app-rules` reported `lastMatch.process: null` three times in a row
while windows were open, auto-profiles never switched on focus, and the FPS reading had no foreground
to follow. The answer has to come from the user's session, and the session agent is the process
running there. `source: "local"` on an installed machine therefore means **the agent is not
reporting** (not running, or an install older than audit round 2, 2026-09-24, which started it only
with `-EnableGpuProfiles`) — the FPS target falls back to the busiest presenter that could be a game
(see `GET /telemetry`, `fps`), and `GET /health/check` reports `foreground_unreported`.

### `GET /power-policy`  (processor power policy per mode)
`GET → { enabled, mode, scheme: string | null, desired: Policy | null, current: Policy | null, matches: boolean | null, originalsCaptured, lastApply: { atUtc, mode, verified, detail } | null, detail: string | null }`
where `Policy = { ac: Settings, dc: Settings }` and `Settings = { epp, boostMode, maxProcessorState }`.

With a fixed 22-25 W budget, how the CPU spends the watts matters as much as how many it gets. On
each mode change the daemon writes three processor settings into the **active** power scheme with
`powercfg /setacvalueindex|/setdcvalueindex` (by GUID), re-activates that same scheme, and reads them
back with `powercfg /q` to verify. Every write lands in `GET /audit` under `power-policy`.

| mode | EPP | boost mode (`PERFBOOSTMODE`) | max state |
|---|---|---|---|
| `gaming` | 33 | 3 efficient enabled | 100 % |
| `gaming-battery` | 50 | 3 efficient enabled | 100 % |
| `ai` | 25 | 3 efficient enabled | 100 % |
| `windows` | 50 | 3 efficient enabled | 100 % |
| `battery` | 80 | AC 3 efficient · DC 0 disabled | 100 % |
| `standby` | — not touched — | | |

- **Only the active scheme**, resolved to its GUID once per apply. Other schemes are never read or written.
- **Originals first.** Before the first write to a scheme, its values are saved to
  `%ProgramData%\GPD Forge\power-policy-originals.json`, once; later applies never overwrite them. With
  no record (or an unreadable one) nothing is written. `install-gpd-forge.ps1 -Restore` and `-Uninstall`
  put them back (`GpdForge.Service.dll --restore-power-policy`) without switching the user's plan.
- **Writes only with `GPDFORGE_ENABLE_HARDWARE=1`** (`enabled`). Otherwise the endpoint still reports
  what Windows holds. `current` is read fresh, so a value another tool changed shows up here.
- `matches` is null when there is nothing to compare (standby, or the readback failed).
- Read-only check from a shell: `GpdForge.Service.dll --probe-power-policy`.

### `GET /standby/hibernate`  ·  `POST /standby/hibernate`  (hibernate instead of draining)
`GET → { hibernateAvailable, unavailable: string | null, onAc: {...}, onBattery: {...} }`
`POST { onBatterySeconds?: number, onAcSeconds?: number } → { applied, reason, onAc, onBattery }`

There is no S0↔S3 toggle on this board — firmware reports S1/S2/S3 unsupported — so the control that
exists is how long the machine idles in Modern Standby before hibernating. Modern Standby keeps
drawing power; hibernate does not, because the machine is off. Measured here: 300 s to standby and
**7200 s to hibernate** on battery, i.e. two hours of S0 drain before it stops costing anything.

- Timeouts are in **seconds**; `0` means *never*; **`null` means the value could not be read**, which
  is not the same claim. Out-of-range values are refused with a reason rather than clamped — silently
  turning a mistyped 100000 into an hour applies something nobody asked for.
- Reads come from the registry, not from `powercfg /q`, whose output is **localised**: on this device
  it reads *"Índice de configuración de corriente continua actual"*, and a parser keyed on those words
  finds nothing the moment the OS language changes. GUIDs and registry keys do not translate.
- Writes go through powercfg **including `/setactive`**: editing the scheme without re-activating it
  leaves a setting that reads as changed and behaves as it was. The result is re-read afterwards, so
  `applied:false` with a reason is what you get when powercfg exits quietly without the rights to
  change the active scheme.

### `GET /firmware`  (what is installed — it does NOT update anything)
`200 → { biosVersion, biosReleaseDate, model, canAttempt: false, advisory }`

Reports the installed BIOS so it can be compared against GPD's release notes, and states the
preconditions for updating **by hand**: on AC, above 50% charge, no other power tool running, no sleep
during the flash. `canAttempt` is always false and there is no POST. A daemon that flashed firmware on
a handheld with no vendor recovery path would be the most dangerous thing in this repository, and an
assistant that implied it might is not much better.

### `GET /settings/export`  ·  `POST /settings/import`  (settings backup / restore)
- `GET /settings/export → { modePresets: Record<ModeId, Preset>, guardian: Guardian-config,
  fanMode: string, brightness: number | null, powerSource: PowerSource-config, autoFps: AutoFps }`
  — a straightforward aggregation of every tunable above; no new persistence layer, this just reads
  the same services `GET /profiles`, `GET /guardian`, `GET /fan`, `GET /display`, `GET
  /power-source`, and `GET /auto-fps` each already expose.
- `POST /settings/import { modePresets?, guardian?, fanMode?, brightness?, powerSource?, autoFps? }
  → { applied: string[] }` — tolerant: every top-level section is optional and applied only if
  present (unknown JSON fields are ignored), and each section goes through the exact same
  clamping/merge its own POST endpoint uses (e.g. `modePresets` entries are clamped via
  `ModeProfiles.Set`, `guardian` merges partially like `POST /guardian`). `applied` lists which
  sections were actually recognized and applied.

### `GET /profiles` · `POST /profiles/{mode}`  (per-mode TDP presets)
- `GET → { [mode]: { stapmW, fastW, slowW, tctlC } }`
- `POST /profiles/{mode} { stapmW, fastW, slowW, tctlC } → { mode, stapmW, fastW, slowW, tctlC, sustained }`
  - Values are clamped to the device's safe band rather than rejected.
  - **The `ai` mode is a sustained ceiling, not a burst budget.** `fastW` and `slowW` are collapsed
    onto `stapmW` on the way in, so what `GET /profiles` reports is what actually reaches the
    silicon — boost above the sustained limit buys no throughput once a job is continuously
    CPU-bound, it only adds heat, fan noise and thermal cycling. `sustained: true` on the response
    tells a client *why* the boost figures it posted came back equal to STAPM, instead of leaving it
    to guess its edit was ignored. The user still sets the ceiling; only the headroom is removed.

### `GET /standby`  ·  `POST /standby/restore`  (Standby Doctor)
- `GET → { lastDrainPctPerHour, lastDrainSleptHours, lastDrainAt, topWakeReason, blockers: string[],
  diagnosticsAvailable: boolean, diagnosticsError: string | null, lastRestore: StandbyRestoreOutcome | null,
  sleepStudy: SleepStudySummary | null, sleepStudyError: string | null }`
  - Every measurement is nullable and `null` means **not measured**, never zero. `blockers` being
    empty only means anything when `diagnosticsAvailable` is `true`: powercfg refusing to run is not
    the same as there being no blockers.
  - `lastDrain*` comes from two real battery readings separated by an observed suspend — never
    extrapolated, so it stays `null` until the machine has actually slept on battery.
- `POST /standby/restore → StandbyRestoreOutcome` — `{ at, steps: [{ name, restored, detail }], anyRestored }`.
  Re-applies fan then TDP (that order: the EC comes back from a suspend uninitialised, and writing
  power limits against an uninitialised EC is how the Win 4 ends up hot and silent). Each step
  reports whether it *actually* happened and why not. The daemon already does this automatically on
  resume (`ResumeRestoreWorker`); this endpoint triggers it on demand.
  - The `hid` step re-enumerates the controller, but **only when Windows reports a node as faulted**
    (`ConfigManagerErrorCode != 0`). A pad that survived the suspend is left alone and the step still
    reports `restored: true`, because doing nothing was the correct outcome — restarting a working
    controller mid-game would be worse than the fault being repaired. When it does act it restarts
    the USB composite parent (one action re-enumerates all seven nodes the pad presents), then
    re-reads the device: `pnputil` exits cleanly for a restart that changed nothing, so success is
    verified rather than inferred.

#### `sleepStudy` — `powercfg /sleepstudy` findings
`{ measuredAt, sessions: number, findings: [{ kind, at, detail }] }`, sampled by a background worker
(shortly after start, then every 12 h) and cached. It is **never generated on the request path**: the
report costs tens of seconds and ~9 MB.

Three states that must not be collapsed by a client:

| `sleepStudy` | `sleepStudyError` | meaning |
|---|---|---|
| `null` | `null` | the sampler has not run yet |
| `null` | set | powercfg refused — `/sleepstudy` needs an elevated session |
| set, `findings: []` | `null` | it ran and found nothing |

`kind` is `failed-resume` (a suspend immediately followed by an abnormal shutdown — inferred from
adjacency, which is what separates "it slept and never woke up" from "it crashed while in use"),
`bugcheck` (carries the stop code), or `worst-drain`. Drain is only ever reported for the session
types the report itself permits it for: subtracting the capacities of a Hibernate session yields a
confident milliwatt figure that means nothing, because the machine is off, a zero exit capacity
beside a zero full-charge capacity is a *missing reading* rather than an empty battery, and the
session ends when the user presses power rather than when the machine stopped drawing.

### `GET /update/check`  (update checker)
`200 → { current: string, latest: string | null, updateAvailable: boolean, url: string | null }` —
`current` is this build's version; `latest`/`url` come from GitHub's
`repos/lexlaboratory/gpd-forge/releases/latest` (short-timeout HTTP, explicit User-Agent);
`updateAvailable` is `GpdForge.Update.VersionCompare.IsNewer(latest, current)`. Degrades honestly to
`{ latest: null, updateAvailable: false, url: null }` on any failure (offline, rate-limited,
malformed response) — never throws, never guesses. Not gated behind `GPDFORGE_ENABLE_HARDWARE` (a
read-only HTTP call, not a hardware/BIOS write).

### `GET /alerts` · `GET /alerts/summary` · alert actions
- `GET /alerts?limit=1..500&unreadOnly=true|false → { alerts: AlertEvent[] }` ordenado de más nuevo a más antiguo.
- `GET /alerts/summary → { unread, unreadInfo, unreadAviso, unreadCritica, latest }`.
- `POST /alerts/{id}/ack → { acknowledged: true, id }`, `POST /alerts/ack-all → { acknowledged: number }`.
- `DELETE /alerts/{id} → 204`; las alertas se guardan localmente en `%ProgramData%\GPD Forge\alerts.json`, con retención de 500 eventos/30 días.

## Error shape
`{ error: { code: string, message: string } }` with the appropriate HTTP status.

## Versioning
The `version` field of `GET /health` carries the contract version. Breaking changes bump the major and are noted here.
