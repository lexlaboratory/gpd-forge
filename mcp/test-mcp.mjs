// GPD Forge — MCP server test harness. GPL-3.0-or-later.
// Spawns the stdio MCP server, does the handshake, lists tools, and exercises a few
// (read + write). Point GPDFORGE_API at a live daemon (mock on 8799, or the service on 8787).
import { spawn } from 'node:child_process'
import { createInterface } from 'node:readline'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

const here = dirname(fileURLToPath(import.meta.url))
const server = spawn(process.execPath, [join(here, 'server.mjs')], {
  stdio: ['pipe', 'pipe', 'inherit'],
  env: { ...process.env, GPDFORGE_API: process.env.GPDFORGE_API || 'http://127.0.0.1:8787' },
})

const pending = new Map()
const rl = createInterface({ input: server.stdout })
rl.on('line', (line) => {
  let msg; try { msg = JSON.parse(line) } catch { return }
  if (msg.id != null && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id) }
})
function req(id, method, params) {
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error(`timeout on ${method}`)), 10000)
    pending.set(id, (m) => { clearTimeout(t); resolve(m) })
    server.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n')
  })
}
function notify(method, params) { server.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n') }

const results = []
const check = (name, cond, detail) => { results.push({ name, ok: !!cond }); console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}${detail ? '  ' + detail : ''}`) }
const callTool = async (id, name, args) => {
  const r = await req(id, 'tools/call', { name, arguments: args || {} })
  const text = r.result?.content?.[0]?.text ?? ''
  let data; try { data = JSON.parse(text) } catch { data = text }
  return { isError: r.result?.isError, data, text }
}

try {
  const init = await req(1, 'initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'test', version: '0' } })
  check('initialize returns serverInfo', init.result?.serverInfo?.name === 'gpd-forge', JSON.stringify(init.result?.serverInfo))
  notify('notifications/initialized')

  const list = await req(2, 'tools/list')
  const names = (list.result?.tools || []).map((t) => t.name)
  check('tools/list has >= 28 tools', names.length >= 28, `(${names.length}: ${names.slice(0, 6).join(',')}…)`)
  check('every tool has an inputSchema', (list.result?.tools || []).every((t) => t.inputSchema?.type === 'object'))

  const tele = await callTool(3, 'get_telemetry')
  check('get_telemetry returns cpuTempC', !tele.isError && typeof tele.data?.cpuTempC === 'number', `cpu=${tele.data?.cpuTempC}`)

  const budget = await callTool(4, 'get_battery_budget')
  check('get_battery_budget returns projections', !budget.isError && Array.isArray(budget.data?.projections))

  const mode = await callTool(5, 'set_mode', { name: 'windows' })
  check('set_mode(windows) returns active=windows', !mode.isError && mode.data?.active === 'windows', mode.text.slice(0, 60))

  const badmode = await callTool(6, 'set_mode', { name: 'nope' })
  check('set_mode(invalid) is reported as error', badmode.isError === true)

  const job = await callTool(7, 'submit_job', { cmd: 'echo hi', constraints: { requireAC: true, maxTempC: 80 } })
  check('submit_job returns an id+status', !job.isError && !!job.data?.id && !!job.data?.status, job.text.slice(0, 60))

  const tuner = await callTool(8, 'get_tuner')
  check('get_tuner returns running + points', !tuner.isError && typeof tuner.data?.running === 'boolean' && Array.isArray(tuner.data?.points), tuner.text.slice(0, 60))

  const unknown = await req(9, 'tools/call', { name: 'does_not_exist', arguments: {} })
  check('unknown tool -> JSON-RPC error', !!unknown.error)
} catch (e) {
  check(`harness completed without throwing`, false, e.message)
}

server.stdin.end()
server.kill()
const failed = results.filter((r) => !r.ok)
console.log(`\n${results.length - failed.length}/${results.length} checks passed`)
process.exit(failed.length ? 1 : 0)
