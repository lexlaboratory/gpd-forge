#!/usr/bin/env node
// GPD Forge — MCP server (stdio). GPL-3.0-or-later.
//
// Exposes the local daemon's telemetry + control as Model Context Protocol tools so an
// agent (Claude Code, agy, KRÓNOS, CYBERLEX…) can drive the handheld: read thermals/power,
// switch modes, set TDP, arbitrate the fan, and queue constraint-gated batch jobs
// ("run this only on AC, under 80 °C"). Zero dependencies — speaks MCP's newline-delimited
// JSON-RPC 2.0 over stdio directly and calls the daemon over HTTP (127.0.0.1:8787).
//
// Register (Claude Code):  claude mcp add gpd-forge -- node C:\Users\Alex\gpd-forge\mcp\server.mjs
// Override the daemon URL with GPDFORGE_API.

import { createInterface } from 'node:readline'

const BASE = process.env.GPDFORGE_API || 'http://127.0.0.1:8787'
const NAME = 'gpd-forge'
const VERSION = '0.1.0'
const MODES = ['gaming', 'ai', 'windows', 'battery', 'standby']

// --- tiny HTTP client against the daemon ---
async function api(path, method = 'GET', body) {
  const res = await fetch(`${BASE}${path}`, {
    method,
    headers: body === undefined ? undefined : { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(8000),
  })
  const text = await res.text()
  let data
  try { data = text ? JSON.parse(text) : null } catch { data = text }
  if (!res.ok) throw new Error(`${method} ${path} → ${res.status}: ${typeof data === 'string' ? data : JSON.stringify(data)}`)
  return data
}

const num = (v) => (typeof v === 'number' && Number.isFinite(v))

// --- tool registry ---------------------------------------------------------------
const tools = [
  {
    name: 'get_telemetry',
    description: 'Live handheld telemetry: CPU/GPU °C, package watts, CPU clock, fan RPM, FPS, battery %, discharge W, AC state, and whether the current TDP is verified.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: () => api('/telemetry'),
  },
  {
    name: 'get_mode',
    description: 'The active power mode (gaming | ai | windows | battery | standby).',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: () => api('/mode'),
  },
  {
    name: 'set_mode',
    description: 'Switch the active power mode. Applies that mode\'s TDP preset + fan curve through the closed loop. WRITES to the device.',
    inputSchema: { type: 'object', properties: { name: { type: 'string', enum: MODES } }, required: ['name'], additionalProperties: false },
    call: (a) => {
      if (!MODES.includes(a?.name)) throw new Error(`name must be one of ${MODES.join(', ')}`)
      return api('/mode', 'POST', { name: a.name })
    },
  },
  {
    name: 'set_tdp',
    description: 'Set the sustained TDP (STAPM watts) through the closed loop. Returns {requested, observed, verified}; verified=false means the firmware reverted it. WRITES to the device.',
    inputSchema: { type: 'object', properties: { stapmW: { type: 'number', minimum: 5, maximum: 40 } }, required: ['stapmW'], additionalProperties: false },
    call: (a) => {
      if (!num(a?.stapmW) || a.stapmW < 5 || a.stapmW > 40) throw new Error('stapmW must be a number 5..40')
      return api('/tdp', 'POST', { stapmW: a.stapmW })
    },
  },
  {
    name: 'get_battery_budget',
    description: 'Battery runtime estimate from the live discharge rate, plus what-if runtimes at 8/12/15/20/25 W. minutesRemaining is null on AC.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: () => api('/battery/budget'),
  },
  {
    name: 'get_profiles',
    description: 'The saved per-mode TDP presets (stapmW/fastW/slowW/tctlC), keyed by mode.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: () => api('/profiles'),
  },
  {
    name: 'set_fan',
    description: 'Set the fan preference (Auto | Quiet | Balanced | Aggressive | Manual). WRITES to the device.',
    inputSchema: { type: 'object', properties: { mode: { type: 'string' } }, required: ['mode'], additionalProperties: false },
    call: (a) => { if (!a?.mode) throw new Error('mode required'); return api('/fan', 'POST', { mode: a.mode }) },
  },
  {
    name: 'set_auto_fps',
    description: 'Enable/disable the Auto-TDP-to-FPS controller and set its target FPS. It steers TDP to hold the target at the least power (gaming mode, once FPS telemetry is live). WRITES to the device.',
    inputSchema: { type: 'object', properties: { targetFps: { type: 'number', minimum: 20, maximum: 240 }, enable: { type: 'boolean' } }, required: ['targetFps', 'enable'], additionalProperties: false },
    call: (a) => {
      if (!num(a?.targetFps)) throw new Error('targetFps must be a number')
      return api('/auto-fps', 'POST', { targetFps: a.targetFps, enable: !!a.enable })
    },
  },
  {
    name: 'freeze_process',
    description: 'Suspend every process matching a name, to free CPU/RAM (critical system processes are protected and never suspended). WRITES to the device. Use thaw_process to undo.',
    inputSchema: { type: 'object', properties: { name: { type: 'string' } }, required: ['name'], additionalProperties: false },
    call: (a) => { if (!a?.name) throw new Error('name required'); return api('/freezer/freeze', 'POST', { name: a.name }) },
  },
  {
    name: 'thaw_process',
    description: 'Resume a previously frozen process by name.',
    inputSchema: { type: 'object', properties: { name: { type: 'string' } }, required: ['name'], additionalProperties: false },
    call: (a) => { if (!a?.name) throw new Error('name required'); return api('/freezer/thaw', 'POST', { name: a.name }) },
  },
  {
    name: 'get_frozen',
    description: 'List the process names currently suspended by the freezer.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: () => api('/freezer'),
  },
  {
    name: 'submit_job',
    description: 'Queue a command that runs only while its constraints hold — requireAC, maxTempC, and/or a time window. The scheduler reports queued|running|blocked. This is how an agent says "run this batch only on AC, under 80 °C, 02:00–07:00". WRITES to the device.',
    inputSchema: {
      type: 'object',
      properties: {
        cmd: { type: 'string' },
        constraints: {
          type: 'object',
          properties: { requireAC: { type: 'boolean' }, maxTempC: { type: 'number' }, window: { type: 'string' } },
          additionalProperties: false,
        },
      },
      required: ['cmd'], additionalProperties: false,
    },
    call: (a) => { if (!a?.cmd) throw new Error('cmd required'); return api('/jobs', 'POST', { cmd: a.cmd, constraints: a.constraints }) },
  },
  {
    name: 'get_jobs',
    description: 'List queued/running/blocked/done jobs.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: () => api('/jobs'),
  },
  {
    name: 'get_standby',
    description: 'Standby Doctor diagnostics: overnight drain %/h, top wake reason, sleep blockers, last restore.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: () => api('/standby'),
  },
  {
    name: 'restore_standby',
    description: 'Re-apply TDP + fan + HID state (what the daemon does automatically on resume). WRITES to the device.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: () => api('/standby/restore', 'POST'),
  },
  {
    name: 'get_history',
    description: 'Recorded telemetry samples from the last N minutes (in-memory ring buffer, ~1/s). Defaults to 5 minutes, clamped to 1..60.',
    inputSchema: { type: 'object', properties: { minutes: { type: 'number', minimum: 1, maximum: 60 } }, additionalProperties: false },
    call: (a) => api(`/history${num(a?.minutes) ? `?minutes=${a.minutes}` : ''}`),
  },
  {
    name: 'get_guardian',
    description: 'Thermal/battery guardian config + live state: thresholds, whether it is currently throttling, and the last alert.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: () => api('/guardian'),
  },
  {
    name: 'set_guardian',
    description: 'Update the thermal/battery guardian config (partial — only sent fields change): enabled, autoThrottle, tempThrottleC, tempCriticalC, throttleFloorW, batteryLowPct, batteryCriticalPct. WRITES to the device (changes auto-throttle behavior).',
    inputSchema: {
      type: 'object',
      properties: {
        enabled: { type: 'boolean' },
        autoThrottle: { type: 'boolean' },
        tempThrottleC: { type: 'number' },
        tempCriticalC: { type: 'number' },
        throttleFloorW: { type: 'number' },
        batteryLowPct: { type: 'number' },
        batteryCriticalPct: { type: 'number' },
      },
      additionalProperties: false,
    },
    call: (a) => api('/guardian', 'POST', a || {}),
  },
  {
    name: 'get_ai',
    description: 'Agents/AI mode state: anti-standby (keep-awake) status + holder count, the sustained flat power profile, and the iGPU VRAM/UMA advisory.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: () => api('/ai'),
  },
  {
    name: 'set_anti_standby',
    description: 'Manually hold (or release) the keep-Windows-awake request, independent of any running job. Idempotent per edge (re-posting the same value is a no-op). WRITES to the device — a real, unprivileged, fully reversible Win32 power request (SetThreadExecutionState), not a hardware/BIOS write.',
    inputSchema: { type: 'object', properties: { enable: { type: 'boolean' } }, required: ['enable'], additionalProperties: false },
    call: (a) => api('/ai/anti-standby', 'POST', { enable: !!a?.enable }),
  },
  {
    name: 'import_motionassistant',
    description: 'Read-only: parses every MotionAssistant .ini profile found on disk and returns them (name + stapmW/fastW/slowW/tctlC). Does not apply anything — use set_mode or the profile endpoints to apply one.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: () => api('/import/motionassistant', 'POST'),
  },
  {
    name: 'get_power_source',
    description: 'Per-power-source auto mode-switch config: whether it is enabled, and which mode to switch to on battery vs. AC.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: () => api('/power-source'),
  },
  {
    name: 'set_power_source',
    description: 'Update the per-power-source auto mode-switch config (partial — only sent fields change). When enabled, the daemon switches the active mode the instant AC connects or disconnects. WRITES to the device.',
    inputSchema: {
      type: 'object',
      properties: {
        enabled: { type: 'boolean' },
        onBatteryMode: { type: 'string', enum: MODES },
        onAcMode: { type: 'string', enum: MODES },
      },
      additionalProperties: false,
    },
    call: (a) => api('/power-source', 'POST', a || {}),
  },
  {
    name: 'export_settings',
    description: 'Full settings snapshot — per-mode TDP presets, guardian config, fan mode, brightness, power-source config, auto-FPS — the same JSON the UI\'s "Export settings" button downloads.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: () => api('/settings/export'),
  },
  {
    name: 'get_display',
    description: 'Display refresh-rate + night-mode (gamma warmth) state in one call: current/supported Hz, and whether night mode is on with its warmth level.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: async () => {
      const [refresh, night] = await Promise.all([api('/display/refresh'), api('/display/night')])
      return { refresh, night }
    },
  },
  {
    name: 'start_tuner',
    description: 'Start an auto-tuner TDP sweep: steps STAPM from minW to maxW, dwells at each step, and records (stapmW, fps, tempC) once FPS telemetry is actually available. goal picks how the best point is chosen: MaxFps, BestEfficiency (highest fps-per-watt), or HoldTarget (lowest watts still holding >= targetFps), among points at or under tempCapC. Honesty note: on hardware without FPS telemetry wired (e.g. this HX370 today — PresentMon not yet integrated), the sweep runs but records nothing useful, so best comes back null with a note rather than a faked reading. WRITES to the device (steers real TDP through the same closed loop set_tdp uses).',
    inputSchema: {
      type: 'object',
      properties: {
        goal: { type: 'string', enum: ['MaxFps', 'BestEfficiency', 'HoldTarget'] },
        targetFps: { type: 'number', minimum: 20, maximum: 240 },
        minW: { type: 'number', minimum: 5, maximum: 40 },
        maxW: { type: 'number', minimum: 5, maximum: 40 },
        tempCapC: { type: 'number', minimum: 60, maximum: 100 },
      },
      required: ['goal'], additionalProperties: false,
    },
    call: (a) => {
      if (!['MaxFps', 'BestEfficiency', 'HoldTarget'].includes(a?.goal)) throw new Error('goal must be one of MaxFps, BestEfficiency, HoldTarget')
      return api('/tuner/start', 'POST', a)
    },
  },
  {
    name: 'get_tuner',
    description: 'Current auto-tuner sweep state: whether it is running, the goal, the recorded sweep points, and the best pick (null if nothing usable has been recorded yet).',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: () => api('/tuner'),
  },
  {
    name: 'check_update',
    description: 'Checks GitHub for the latest gpd-forge release and compares it to the running version. Degrades to updateAvailable:false on any network failure (offline, rate-limited) — never throws.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    call: () => api('/update/check'),
  },
]
const toolMap = new Map(tools.map((t) => [t.name, t]))

// --- JSON-RPC / MCP plumbing -----------------------------------------------------
function send(msg) { process.stdout.write(JSON.stringify(msg) + '\n') }
function reply(id, result) { send({ jsonrpc: '2.0', id, result }) }
function fail(id, code, message) { send({ jsonrpc: '2.0', id, error: { code, message } }) }

async function handle(msg) {
  const { id, method, params } = msg
  const isRequest = id !== undefined && id !== null

  switch (method) {
    case 'initialize':
      return reply(id, {
        protocolVersion: params?.protocolVersion || '2025-06-18',
        capabilities: { tools: {} },
        serverInfo: { name: NAME, version: VERSION },
        instructions: `Controls a GPD Win 4 handheld via the local GPD Forge daemon (${BASE}). Read tools are safe; tools whose description says WRITES change real hardware power/thermal state.`,
      })
    case 'notifications/initialized':
    case 'initialized':
      return // notification, no reply
    case 'ping':
      return isRequest && reply(id, {})
    case 'tools/list':
      return reply(id, { tools: tools.map(({ name, description, inputSchema }) => ({ name, description, inputSchema })) })
    case 'tools/call': {
      const tool = toolMap.get(params?.name)
      if (!tool) return fail(id, -32602, `Unknown tool: ${params?.name}`)
      try {
        const out = await tool.call(params.arguments || {})
        return reply(id, { content: [{ type: 'text', text: JSON.stringify(out, null, 2) }] })
      } catch (e) {
        // Tool errors are reported in-band so the model can react (isError), not as protocol errors.
        return reply(id, { content: [{ type: 'text', text: `Error: ${e.message}` }], isError: true })
      }
    }
    default:
      if (isRequest) return fail(id, -32601, `Method not found: ${method}`)
  }
}

const rl = createInterface({ input: process.stdin })
rl.on('line', (line) => {
  const s = line.trim()
  if (!s) return
  let msg
  try { msg = JSON.parse(s) } catch { return } // ignore non-JSON lines
  Promise.resolve(handle(msg)).catch((e) => { if (msg?.id != null) fail(msg.id, -32603, `Internal error: ${e.message}`) })
})
rl.on('close', () => process.exit(0))

process.stderr.write(`[gpd-forge mcp] ready, daemon=${BASE}, ${tools.length} tools\n`)
