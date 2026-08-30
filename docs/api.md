# GPD Forge — Local API contract (v0)

The daemon (`core/Api/`) exposes this local API. The UI, the overlay, and **external agents** are all
clients. The Node mock daemon in `tools/mock-daemon/` implements this exact contract so the UI and the
tests can run before the C# service exists — it is the reference the C# `Api/` must match.

## Transport & security
- Bind **localhost only** by default: `http://127.0.0.1:8787`. Remote access (over the tailnet) is opt-in.
- Auth: bearer token for HTTP; ACL for the named pipe. The mock skips auth (localhost, dev).
- Live telemetry: **the daemon polls only** — clients `GET /telemetry` on a timer (the UI does 1 Hz).
  There is no streaming endpoint in production. The mock implements SSE at `/telemetry/stream` for
  convenience, but no client consumes it.
- CORS: the mock allows the dev origin so the browser UI can call it.

## Types (mirror of `ui/src/types.ts` and `core/Telemetry/TelemetrySnapshot`)
```ts
type ModeId = 'gaming' | 'ai' | 'windows' | 'battery' | 'standby'

interface Telemetry {
  cpuTempC: number; gpuTempC: number; packageW: number; cpuClockMhz: number
  fanRpm: number; fanDutyPct: number; fps: number; fps1PctLow: number
  batteryPct: number; dischargeW: number; acConnected: boolean; tdpVerified: boolean
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
`200 → Telemetry` — the latest snapshot.

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
- `POST { name: ModeId } → { active: ModeId }` — switches the active mode (applies its TDP + fan curve).
  `400` on unknown mode.

### `POST /tdp`
`POST { stapmW: number } → { requested: number, observed: number, verified: boolean }`
Applies a sustained TDP through the **closed loop**: the daemon re-reads the PM table. If the firmware
reverted the limit, `verified:false` and `observed` reflects what actually held (this is the honest
behavior that replaces MotionAssistant's blind 30s re-apply). `400` if `stapmW` is out of the safe band.

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
  - `AppRule = { id: guid, match: string, mode: ModeId, enabled: boolean }`, in precedence order.
  - `modes` is what a rule may select: `battery` / `windows` / `gaming` / `ai`. `standby` is excluded
    on purpose — it is a preset for a system state, and a foreground app able to select it would be
    a trap.
  - `autoProfiles` is `GPDFORGE_AUTO_PROFILES != 0`. The rules are stored, readable and editable
    either way; `false` only means nothing is currently applying them.
  - `AppRuleMatch = { ruleId: guid | null, match: string | null, mode: ModeId, process: string | null,
    acConnected: boolean, atUtc: string }` — what decided the mode on the daemon's most recent
    foreground tick. `ruleId: null` means no rule matched and the mode came from the AC/battery
    fallback, so the UI can say so instead of implying a rule is in charge. `null` until the focus
    worker has run at all (it does not run when `autoProfiles` is false).
- `POST { match, mode, enabled? } → (the GET shape)` — appends a rule at **lowest** precedence.
- `PUT /app-rules/:id { match, mode, enabled } → (the GET shape)` — replaces the rule in place,
  keeping its position. `404` if the id is unknown.
- `DELETE /app-rules/:id → (the GET shape)`, `404` if the id is unknown.
- `POST /app-rules/:id/move { delta: number } → (the GET shape)` — shifts the rule by `delta`
  positions; negative moves it towards **higher** precedence. Clamped to the ends: a rule already at
  the top asked to move up is a no-op, not an error. `404` only if the id is unknown.

Every mutation answers with the **whole** ruleset, not just the row that changed, so a client can
never end up rendering a list the daemon no longer holds. A rejected rule comes back as
`400 { error: string }` — the bare-`error` shape, not the `{ error: { code, message } }` used
elsewhere — carrying `GpdForge.Profiles.AppRulePolicy`'s message verbatim (e.g.
`"A rule for 'steam' already exists."`). That message is written for the person reading it and the
UI shows it as-is, so it must not be rewritten or reduced to a status code.

Rules persist to `%ProgramData%\GPD Forge\app-rules.json`. A fresh install is seeded from the exact
ruleset the daemon used to hardcode (`ModeRules.DefaultRuleSet`), so turning rules into data cannot
silently change day-one behaviour. A corrupt file is quarantined rather than taking the daemon down,
and rows the matcher could not honour (blank match, unknown mode, a duplicate) are dropped on load.

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
- `GET /sessions/games → { fpsAvailable: boolean, games: GameSummary[] }` — the per-app rollup, most
  played first. Averages are weighted by duration, so a two-minute run cannot drag the average of a
  three-hour one around.
- `GET /sessions/:id → GameSession`, `404 { error: "session not found" }` if unknown.
- `DELETE /sessions/:id → 204`, same `404` if unknown.

```
GameSession = { id: guid, app: string, startedUtc, endedUtc, durationSeconds: number,
                samples: number, samplesWithoutFps: number,
                fpsAvg, fps1PctLow, fpsMax, cpuTempAvgC, cpuTempMaxC, packageAvgW: number | null,
                onBattery: boolean, batteryStartPct, batteryEndPct, batteryUsedPct: number | null,
                fpsTrend: number[] }
GameSummary = { app: string, sessions: number, totalSeconds: number, lastPlayedUtc,
                fpsAvg, fpsBest, fps1PctLow, cpuTempMaxC: number | null }
```

Every metric is nullable because every sensor behind it is optional on this hardware: `null` means
*not measured* and is never written as a `0`. `samplesWithoutFps` counts the ticks where the app was
presenting but the probe produced no aggregate, so an average built on partial coverage can be
qualified instead of implying full coverage. `onBattery` is true only when the session ran
*entirely* on battery — a session that saw the charger has no meaningful drain figure, so its
battery fields are `null`. `fpsTrend` is downsampled to at most 120 points at close time (a 3-hour
session at 1 Hz would otherwise put megabytes of JSON on the system drive for a 120 px graph).

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
`core/Fan/GpdFanController.cs`) — with both set and a matched board, `ForgeWorker` drives the EC every
tick: `Auto` restores automatic (once, on the transition), `Quiet`/`Balanced`/`Aggressive` compute a
duty from a temp→duty curve with hysteresis (`core/Fan/FanCurve.cs`) via `FanMath`'s PWM-scale cast
  (`core/Fan/FanMath.cs`), and `Manual` holds `manualDuty`. A safety floor (`GpdFanController.MinManualDuty`,
  40/255) means GPD Forge never commands a near-stopped fan, and AUTOMATIC is always restored on
  service shutdown. If CPU temperature telemetry is absent/non-finite, curve modes fail safe to
  firmware AUTOMATIC instead of interpreting the missing `0` reading as a cold CPU. With either gate
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
across `issues` (`ok` when empty). Rules today: fan reads 0 rpm while `cpuTempC` is above 70 °C → warn
(this literally catches a parked-fan-while-warm state); `cpuTempC >= 95` → critical; `!tdpVerified` →
warn (firmware silently reverting TDP); on battery with `dischargeW > 30` → warn (high discharge). The
System page's health card polls this and shows a green "All good" when `issues` is empty, or the
issue list colored by severity otherwise.

### `POST /jobs`  ·  `GET /jobs/:id`  ·  `GET /jobs`  (Agents / AI mode)
- `POST { cmd: string, constraints?: { requireAC?: boolean, maxTempC?: number, window?: string } }`
  `→ { id: string, status: 'queued' | 'running' | 'done' | 'blocked' }`
  The scheduler runs the job only while its constraints hold (AC connected, under temp, inside the time
  window); otherwise `blocked`. This is how an external agent says "run this batch only on AC, under 80 °C,
  between 02:00–07:00".
- `GET /jobs/:id → { id, status, cmd, startedAt?, finishedAt?, log: string[] }`
- `GET /jobs → Array<...>`

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

Anti-Lag, Chill, Boost, Image Sharpening and the driver's own frame-rate cap (FRTC).

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
- `GET /gpu/desired` is what the agent reconciles towards; only it reads this. `requested: false`
  means nobody has asked for anything and the GPU must be left alone — starting the daemon is not a
  reason to change someone's Adrenalin settings. Desired state rather than a command queue, so an
  agent that restarts or misses ticks converges instead of replaying.

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
`GET /health.version` carries the contract version. Breaking changes bump the major and are noted here.
