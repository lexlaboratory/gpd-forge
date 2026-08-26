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
```

## Endpoints

### `GET /health`
`200 → { ok: true, version: string, model: string }`

### `GET /telemetry`
`200 → Telemetry` — the latest snapshot.

### `GET /telemetry/stream` (SSE in mock, WS in prod)
Server pushes `Telemetry` JSON events ~4 Hz.

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

### `GET /standby`  ·  `POST /standby/restore`  (Standby Doctor)
- `GET → { lastDrainPctPerHour: number, topWakeReason: string, blockers: string[], lastRestore: string[] | null }`
- `POST /standby/restore → { restored: string[] }` — re-applies TDP + fan + HID state (what the daemon does
  automatically on a resume event; this endpoint triggers it on demand).

## Error shape
`{ error: { code: string, message: string } }` with the appropriate HTTP status.

## Versioning
`GET /health.version` carries the contract version. Breaking changes bump the major and are noted here.
