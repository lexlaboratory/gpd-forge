# GPD Forge — Local API contract (v0)

The daemon (`core/Api/`) exposes this local API. The UI, the overlay, and **external agents** are all
clients. The Node mock daemon in `tools/mock-daemon/` implements this exact contract so the UI and the
tests can run before the C# service exists — it is the reference the C# `Api/` must match.

## Transport & security
- Bind **localhost only** by default: `http://127.0.0.1:8787`. Remote access (over the tailnet) is opt-in.
- Auth: bearer token for HTTP; ACL for the named pipe. The mock skips auth (localhost, dev).
- Live telemetry: production uses a WebSocket at `/telemetry/stream`; the mock uses SSE at the same path
  (browser-native `EventSource`, zero-dependency). Clients that don't stream may poll `GET /telemetry`.
- CORS: the mock allows the dev origin so the browser UI can call it.

## Types (mirror of `ui/src/types.ts` and `core/Telemetry/TelemetrySnapshot`)
```ts
type ModeId = 'gaming' | 'ai' | 'windows' | 'battery' | 'standby'

interface Telemetry {
  cpuTempC: number; gpuTempC: number; packageW: number; cpuClockMhz: number
  fanRpm: number; fanDutyPct: number; fps: number
  batteryPct: number; dischargeW: number; acConnected: boolean; tdpVerified: boolean
}

interface ImportedProfile { name: string; stapmW: number; fastW: number; slowW: number; tctlC: number }
```

## Endpoints

### `GET /health`
`200 → { ok: true, version: string, model: string }`

### `GET /telemetry`
`200 → Telemetry` — the latest snapshot.

### `GET /telemetry/stream` (SSE in mock, WS in prod)
Server pushes `Telemetry` JSON events ~4 Hz.

### `GET /history`  ·  `GET /history/export.csv`  (telemetry history + CSV export)
- `GET /history?minutes=N → { samples: Array<{ unixMs: number, snap: Telemetry }> }` — samples from the
  last `N` minutes, oldest first. `minutes` defaults to 5, clamped to 1..60. Backed by an in-memory ring
  buffer the worker fills once per tick (capacity 3600 = 1h at 1Hz) — a freshly (re)started daemon holds
  less history than that until the buffer fills.
- `GET /history/export.csv` → `text/csv`, `Content-Disposition: attachment;
  filename="gpd-forge-telemetry.csv"` — every currently-held sample as CSV, one row each: `unixMs,
  isoTime, cpuTempC, gpuTempC, packageW, cpuClockMhz, fanRpm, fps, batteryPct, dischargeW, acConnected,
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

### `GET /profiles`  ·  `POST /profiles/:mode`  (editable per-mode TDP presets)
- `GET → Record<ModeId, { stapmW: number, fastW: number, slowW: number, tctlC: number }>` — the saved
  preset for every mode, keyed by mode id.
- `POST /profiles/:mode { stapmW, fastW, slowW, tctlC } → { mode: ModeId, stapmW, fastW, slowW, tctlC }` —
  persists that mode's preset (what the Power page's "Save preset" writes).

### `POST /import/motionassistant`  (MotionAssistant `.ini` profile importer)
`200 → { found: number, profiles: ImportedProfile[], path: string }` — reads every `*.ini` file
under MotionAssistant's saved-profiles directory (default `C:\Program Files\Motion
Assistant\Profiles`) and parses each `[ProfileName]` section into an `ImportedProfile`. Read-only
and tolerant: an absent directory or a malformed file never throws — worst case is `found: 0` with
`profiles: []` and `path` set to where GPD Forge looked. This endpoint only *returns* the parsed
profiles; to apply one, POST its numbers to the existing `POST /profiles/:mode`.

### `GET /power-source`  ·  `POST /power-source`  (per-power-source auto mode-switch)
- `GET → { enabled: boolean, onBatteryMode: ModeId, onAcMode: ModeId }`
- `POST { enabled?, onBatteryMode?, onAcMode? } → { …config }` — partial update (only sent fields
  change; a blank/whitespace mode string is ignored rather than clearing the field).

When enabled, the daemon switches the active mode the instant AC connects or disconnects (edge-
triggered, not every tick) — e.g. auto-drop to Battery mode on unplug, back to Windows mode on
plug-in. Applied the same way `POST /mode` is: through `ProfileApplier`, which yields if another
power controller (MotionAssistant/GPD Tool) is running.

### `GET /fan`  ·  `POST /fan`
- `GET → { mode: 'Auto' | 'Quiet' | 'Balanced' | 'Aggressive' | 'Manual' }`
- `POST { mode } → { mode }` — sets the fan preference. The curve is applied once the fan driver lands
  (EC access is gated behind a stable kernel helper); until then the preference is stored.

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

**Honesty note:** this HX370 has no FPS telemetry yet (`Fps` is 0 — PresentMon isn't wired). A sweep
run today therefore records nothing useful (a non-positive `fps` reading is never recorded — see
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

### `POST /jobs`  ·  `GET /jobs/:id`  ·  `GET /jobs`  (Agents / AI mode)
- `POST { cmd: string, constraints?: { requireAC?: boolean, maxTempC?: number, window?: string } }`
  `→ { id: string, status: 'queued' | 'running' | 'done' | 'blocked' }`
  The scheduler runs the job only while its constraints hold (AC connected, under temp, inside the time
  window); otherwise `blocked`. This is how an external agent says "run this batch only on AC, under 80 °C,
  between 02:00–07:00".
- `GET /jobs/:id → { id, status, cmd, startedAt?, finishedAt?, log: string[] }`
- `GET /jobs → Array<...>`

### `GET /ai`  ·  `POST /ai/anti-standby`  ·  `POST /ai/vram`  (Agents / AI mode — anti-standby, sustained profile, VRAM/UMA)
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
- `POST /ai/anti-standby { enable: boolean } → { active, holders, manual }` — manual override. Only the
  `false→true` / `true→false` edge touches the ref count, so re-posting the same value is a no-op (never
  double-acquires or double-releases the hold).
- `POST /ai/vram { requestedMb?: number } → { reportedMb, adapterName, available, applied: false,
  requiresBiosReboot: true, advisory }` — always `applied:false`: GPD Forge does not perform a blind UMA
  write (see `vram` above). Honest by construction rather than faking success.

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

### `GET /standby`  ·  `POST /standby/restore`  (Standby Doctor)
- `GET → { lastDrainPctPerHour: number, topWakeReason: string, blockers: string[], lastRestore: string[] | null }`
- `POST /standby/restore → { restored: string[] }` — re-applies TDP + fan + HID state (what the daemon does
  automatically on a resume event; this endpoint triggers it on demand).

### `GET /update/check`  (update checker)
`200 → { current: string, latest: string | null, updateAvailable: boolean, url: string | null }` —
`current` is this build's version; `latest`/`url` come from GitHub's
`repos/lexlaboratory/gpd-forge/releases/latest` (short-timeout HTTP, explicit User-Agent);
`updateAvailable` is `GpdForge.Update.VersionCompare.IsNewer(latest, current)`. Degrades honestly to
`{ latest: null, updateAvailable: false, url: null }` on any failure (offline, rate-limited,
malformed response) — never throws, never guesses. Not gated behind `GPDFORGE_ENABLE_HARDWARE` (a
read-only HTTP call, not a hardware/BIOS write).

## Error shape
`{ error: { code: string, message: string } }` with the appropriate HTTP status.

## Versioning
`GET /health.version` carries the contract version. Breaking changes bump the major and are noted here.
