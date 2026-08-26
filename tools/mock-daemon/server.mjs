#!/usr/bin/env node
// GPD Forge — mock daemon. GPL-3.0-or-later.
//
// Zero-dependency Node HTTP server implementing docs/api.md. It exists so the UI and the
// E2E tests can run against the REAL API contract before the C# service exists. The C#
// `core/Api/` must match this behavior. Not for production — no auth, in-memory state.
//
// Run: node tools/mock-daemon/server.mjs   (PORT env, default 8787)

import http from 'node:http'

const PORT = Number(process.env.PORT ?? 8787)
const VERSION = '0.0.0-mock'
const MODEL = 'GPD Win 4 (G1618-04) · Ryzen AI 9 HX 370'

const MODES = new Set(['gaming', 'ai', 'windows', 'battery', 'standby'])
const PROFILES = [
  { id: 'battery', label: 'Battery', stapmW: 8, fastW: 12, slowW: 10, tctlC: 90 },
  { id: 'windows', label: 'Windows', stapmW: 15, fastW: 20, slowW: 17, tctlC: 92 },
  { id: 'gaming', label: 'Gaming', stapmW: 25, fastW: 33, slowW: 28, tctlC: 95 },
  { id: 'ai', label: 'Agents / AI', stapmW: 25, fastW: 25, slowW: 25, tctlC: 90 },
  { id: 'standby', label: 'Standby Doctor', stapmW: 15, fastW: 20, slowW: 17, tctlC: 92 },
]

const TDP_MIN = 5
const TDP_MAX = 35
const TDP_FIRMWARE_CAP = 30 // above this the "firmware" reverts → verified:false

const state = {
  activeMode: 'windows',
  stapmW: 20,
  tdpVerified: true,
  acConnected: false,
  batteryPct: 78,
  jobs: new Map(),
  jobSeq: 0,
  brightness: 70,
  fanMode: 'Auto',
  presets: {
    battery: { stapmW: 8, fastW: 12, slowW: 10, tctlC: 90 },
    windows: { stapmW: 15, fastW: 20, slowW: 17, tctlC: 92 },
    gaming:  { stapmW: 25, fastW: 33, slowW: 28, tctlC: 95 },
    ai:      { stapmW: 25, fastW: 25, slowW: 25, tctlC: 90 },
    standby: { stapmW: 15, fastW: 20, slowW: 17, tctlC: 92 },
  },
  standby: {
    lastDrainPctPerHour: 6.2,
    topWakeReason: 'Fingerprint device (Win 4)',
    blockers: ['GPDKeyboard.exe'],
    lastRestore: null,
  },
}

function telemetry() {
  const jitter = (base, amp) => Math.round((base + (Math.random() - 0.5) * amp) * 10) / 10
  return {
    cpuTempC: jitter(61, 4),
    gpuTempC: jitter(58, 4),
    packageW: jitter(state.stapmW, 2),
    cpuClockMhz: Math.round(jitter(3300, 200)),
    fanRpm: Math.round(jitter(3600, 300)),
    fanDutyPct: 45,
    fps: Math.round(jitter(60, 3)),
    batteryPct: state.batteryPct,
    dischargeW: jitter(18, 2),
    acConnected: state.acConnected,
    tdpVerified: state.tdpVerified,
  }
}

/** Simulate the closed loop: high requests get reverted by "firmware". */
function applyTdp(stapmW) {
  const requested = Math.round(stapmW)
  const observed = requested > TDP_FIRMWARE_CAP ? TDP_FIRMWARE_CAP : requested
  const verified = observed === requested
  state.stapmW = observed
  state.tdpVerified = verified
  return { requested, observed, verified }
}

function evalJob(job) {
  const t = telemetry()
  if (job.constraints?.requireAC && !t.acConnected) return 'blocked'
  if (job.constraints?.maxTempC != null && t.cpuTempC > job.constraints.maxTempC) return 'blocked'
  return 'running'
}

// --- tiny HTTP helpers ---
const CORS = {
  'access-control-allow-origin': '*',
  'access-control-allow-methods': 'GET,POST,OPTIONS',
  'access-control-allow-headers': 'content-type,authorization',
}
function send(res, status, body) {
  res.writeHead(status, { 'content-type': 'application/json', ...CORS })
  res.end(JSON.stringify(body))
}
function err(res, status, code, message) {
  send(res, status, { error: { code, message } })
}
function readBody(req) {
  return new Promise((resolve) => {
    let data = ''
    req.on('data', (c) => (data += c))
    req.on('end', () => {
      try { resolve(data ? JSON.parse(data) : {}) } catch { resolve(null) }
    })
  })
}

const server = http.createServer(async (req, res) => {
  const { method } = req
  const url = new URL(req.url, `http://localhost:${PORT}`)
  const path = url.pathname

  if (method === 'OPTIONS') { res.writeHead(204, CORS); return res.end() }

  if (method === 'GET' && path === '/health') return send(res, 200, { ok: true, version: VERSION, model: MODEL })
  if (method === 'GET' && path === '/telemetry') return send(res, 200, telemetry())
  if (method === 'GET' && path === '/mode') return send(res, 200, { active: state.activeMode })

  if (method === 'GET' && path === '/telemetry/stream') {
    res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache', connection: 'keep-alive', ...CORS })
    const id = setInterval(() => res.write(`data: ${JSON.stringify(telemetry())}\n\n`), 250)
    req.on('close', () => clearInterval(id))
    return
  }

  if (method === 'POST' && path === '/mode') {
    const body = await readBody(req)
    if (!body || !MODES.has(body.name)) return err(res, 400, 'bad_mode', 'unknown mode')
    state.activeMode = body.name
    const p = PROFILES.find((x) => x.id === body.name)
    if (p) applyTdp(p.stapmW)
    return send(res, 200, { active: state.activeMode })
  }

  if (method === 'POST' && path === '/tdp') {
    const body = await readBody(req)
    const w = Number(body?.stapmW)
    if (!Number.isFinite(w) || w < TDP_MIN || w > TDP_MAX) return err(res, 400, 'bad_tdp', `stapmW must be ${TDP_MIN}..${TDP_MAX}`)
    return send(res, 200, applyTdp(w))
  }

  if (method === 'POST' && path === '/jobs') {
    const body = await readBody(req)
    if (!body?.cmd) return err(res, 400, 'bad_job', 'cmd required')
    const id = `job-${++state.jobSeq}`
    const job = { id, cmd: body.cmd, constraints: body.constraints ?? {}, log: [], status: 'queued' }
    job.status = evalJob(job)
    state.jobs.set(id, job)
    return send(res, 200, { id, status: job.status })
  }

  if (method === 'GET' && path === '/jobs') return send(res, 200, [...state.jobs.values()])
  if (method === 'GET' && path.startsWith('/jobs/')) {
    const job = state.jobs.get(path.slice('/jobs/'.length))
    return job ? send(res, 200, job) : err(res, 404, 'no_job', 'not found')
  }

  if (method === 'GET' && path === '/profiles') return send(res, 200, state.presets)
  if (method === 'POST' && path.startsWith('/profiles/')) {
    const mode = path.slice('/profiles/'.length)
    const body = await readBody(req)
    if (!body) return err(res, 400, 'bad', 'json')
    state.presets[mode] = { stapmW: body.stapmW, fastW: body.fastW, slowW: body.slowW, tctlC: body.tctlC }
    return send(res, 200, { mode, ...state.presets[mode] })
  }
  if (method === 'GET' && path === '/fan') return send(res, 200, { mode: state.fanMode })
  if (method === 'POST' && path === '/fan') {
    const body = await readBody(req)
    if (body?.mode) state.fanMode = body.mode
    return send(res, 200, { mode: state.fanMode })
  }
  if (method === 'GET' && path === '/display') return send(res, 200, { brightness: state.brightness })
  if (method === 'POST' && path === '/display/brightness') {
    const body = await readBody(req)
    state.brightness = Math.max(0, Math.min(100, Number(body?.level ?? state.brightness)))
    return send(res, 200, { brightness: state.brightness })
  }

  if (method === 'GET' && path === '/standby') return send(res, 200, state.standby)
  if (method === 'POST' && path === '/standby/restore') {
    const restored = ['tdp', 'fan', 'hid']
    state.tdpVerified = true
    state.standby = { ...state.standby, lastRestore: restored }
    return send(res, 200, { restored })
  }

  return err(res, 404, 'not_found', `${method} ${path}`)
})

server.listen(PORT, '127.0.0.1', () => console.log(`[gpd-forge mock] http://127.0.0.1:${PORT}`))
