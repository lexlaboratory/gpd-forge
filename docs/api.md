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

### `GET /profiles`
`200 → Array<{ id: ModeId, label: string, stapmW: number, fastW: number, slowW: number, tctlC: number }>`

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
