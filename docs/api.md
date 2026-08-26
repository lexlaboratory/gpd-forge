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

## Error shape
`{ error: { code: string, message: string } }` with the appropriate HTTP status.

## Versioning
`GET /health.version` carries the contract version. Breaking changes bump the major and are noted here.
