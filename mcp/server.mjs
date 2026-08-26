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
