// GPD Forge — mock daemon request log. GPL-3.0-or-later.
//
// Opt-in (MOCK_LOG=<file>): one line per request with its start, status and duration, every socket
// the server accepts or loses, requests still unanswered after a few seconds, and event-loop stalls.
// playwright.config.ts turns it on for every E2E run, because the harness otherwise discards the
// mock's output — and an intermittent failure that leaves no record is one that stays "cause
// unknown" for weeks (docs/ROADMAP.md, 2026-09: "reproduced, not diagnosed"). A few hundred KB per
// run is cheap next to that.

import { appendFileSync, mkdirSync, writeFileSync } from 'node:fs'
import { dirname } from 'node:path'
import { monitorEventLoopDelay } from 'node:perf_hooks'

const SLOW_MS = 3_000 // a mock answers in < 5 ms; anything this old is hung, not slow
const LAG_WARN_MS = 200

/** Wires the log onto `server`. A no-op unless `file` is set, so a hand-started mock stays quiet. */
export function attachDiagnostics(server, file) {
  if (!file) return
  mkdirSync(dirname(file), { recursive: true })
  writeFileSync(file, '')
  const t0 = Date.now()
  const log = (line) => {
    try { appendFileSync(file, `${new Date().toISOString()} +${Date.now() - t0}ms ${line}\n`) } catch { /* log is best-effort */ }
  }
  log(`start pid=${process.pid} node=${process.version}`)

  let sockets = 0
  let socketSeq = 0
  server.on('connection', (socket) => {
    const id = ++socketSeq
    sockets++
    log(`sock+ #${id} from ${socket.remoteAddress}:${socket.remotePort} open=${sockets}`)
    socket.on('close', () => { sockets--; log(`sock- #${id} open=${sockets}`) })
  })

  const pending = new Map()
  let reqSeq = 0
  server.on('request', (req, res) => {
    const id = ++reqSeq
    const started = Date.now()
    pending.set(id, { started, what: `${req.method} ${req.url}` })
    res.on('finish', () => {
      pending.delete(id)
      log(`req #${id} ${req.method} ${req.url} -> ${res.statusCode} ${Date.now() - started}ms`)
    })
    res.on('close', () => {
      if (pending.delete(id)) log(`req #${id} ${req.method} ${req.url} closed by peer after ${Date.now() - started}ms`)
    })
  })

  const lag = monitorEventLoopDelay({ resolution: 20 })
  lag.enable()
  const timer = setInterval(() => {
    const maxMs = lag.max / 1e6
    if (maxMs > LAG_WARN_MS) log(`event-loop stall max=${Math.round(maxMs)}ms`)
    lag.reset()
    const now = Date.now()
    for (const [id, p] of pending) {
      if (now - p.started > SLOW_MS) log(`hung #${id} ${p.what} for ${now - p.started}ms`)
    }
  }, 1_000)
  timer.unref()
  server.on('listening', () => log(`listening ${JSON.stringify(server.address())}`))
  server.on('error', (e) => log(`server error ${e?.code ?? ''} ${e?.message ?? e}`))
  process.on('exit', (code) => log(`exit code=${code}`))
}
