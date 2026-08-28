// GPD Forge UI — Monitor page (charts, history) plus freezer/health/panic cards. GPL-3.0-or-later.
import { useEffect, useState } from 'react'
import type { Telemetry, HealthReport } from '../types'
import {
  getFrozen, freeze, thaw, getHistory, historyExportUrl, getHealthCheck, panicCool,
} from '../api'
import { Frame, Badge, Button, Chip, Readout } from '../components'
import { Sparkline, useHistory } from '../Chart'
import { useToast } from '../Toast'

// --- Monitor ------------------------------------------------------------------
export function MonitorPage({ tele }: { tele: Telemetry | null }) {
  const cpu = useHistory(tele?.cpuTempC ?? NaN)
  const watt = useHistory(tele?.packageW ?? NaN)
  const fps = useHistory(tele?.fps ?? NaN)
  const [sampleCount, setSampleCount] = useState<number | null>(null)

  useEffect(() => {
    const t = () => getHistory(5).then((h) => setSampleCount(h.samples.length)).catch(() => {})
    t(); const id = setInterval(t, 5000); return () => clearInterval(id)
  }, [])

  return (
    <>
      <Frame title="Live telemetry" hint={<Badge tone={tele ? 'ok' : 'muted'}>{tele ? 'streaming · 1 Hz' : 'no signal'}</Badge>}>
        <div className="charts" data-testid="charts">
          <Sparkline data={cpu} label="CPU" unit="°C" color="var(--accent)" width={360} height={92} surface="var(--bg-elev)" testid="chart-cpu" />
          <Sparkline data={watt} label="Power" unit="W" color="var(--accent-2)" width={360} height={92} surface="var(--bg-elev)" testid="chart-watt" />
          <Sparkline data={fps} label="FPS" color="var(--good)" width={360} height={92} surface="var(--bg-elev)" testid="chart-fps" />
        </div>
        {/* No fill bars under these: package watts and FPS have no ceiling this build can honestly
            claim, and a meter drawn against a guessed maximum would be a fabricated reading. */}
        <p className="muted">The last 60 samples of each series, straight from the telemetry poll — CPU package temperature, package power draw, and frame rate from PresentMon. Hover or focus a trace to read any individual sample.</p>
      </Frame>
      <Frame title="History" hint={<Badge tone="ok">recorded by the daemon</Badge>}>
        <p className="muted" data-testid="history-count">
          {sampleCount === null ? 'Loading…' : `${sampleCount} sample${sampleCount === 1 ? '' : 's'} in the last 5 minutes`}
        </p>
        <div className="row-end">
          <Button variant="accent" testid="history-export" href={historyExportUrl()} download="gpd-forge-telemetry.csv">Export CSV</Button>
        </div>
      </Frame>
      {/* The on-screen-display placeholder moved to the Hardware page, with the rest of what this
          build cannot do. A card that only describes an unbuilt feature does not belong among the
          working monitors. */}
    </>
  )
}

// --- System -------------------------------------------------------------------
export function FreezerCard() {
  const toast = useToast()
  const [name, setName] = useState('')
  const [frozen, setFrozen] = useState<string[]>([])
  useEffect(() => { getFrozen().then(setFrozen).catch(() => {}) }, [])
  const doFreeze = async () => {
    if (!name.trim()) return
    const r = await freeze(name.trim()).catch(() => null)
    if (r) {
      setFrozen(r.frozen)
      toast.push({ kind: r.suspended > 0 ? 'success' : 'warn', message: r.suspended > 0 ? `Froze ${r.suspended} process(es): ${name}` : `No process "${name}" (or protected)` })
    }
  }
  const doThaw = async (n: string) => { const r = await thaw(n).catch(() => null); if (r) { setFrozen(r.frozen); toast.push({ kind: 'info', message: `Thawed ${n}` }) } }
  return (
    <Frame title="Freezer" hint={<Badge tone={frozen.length > 0 ? 'warn' : 'muted'}>{frozen.length} suspended</Badge>}>
      <div className="job-form">
        <input className="job-input" data-testid="freezer-name" value={name} onChange={(e) => setName(e.target.value)} placeholder="process name (e.g. chrome)" aria-label="process to freeze" />
        <Button testid="freezer-freeze" onClick={() => { void doFreeze() }}>Freeze</Button>
      </div>
      <ul className="job-list" data-testid="frozen-list">
        {frozen.length === 0 && <li className="job-empty">Nothing frozen.</li>}
        {frozen.map((n) => (
          <li key={n} className="job-row">
            {/* .job-row is a 3-column grid; the state badge fills the leading column so the
                Thaw action still lands hard right instead of beside the name. */}
            <Badge tone="warn">frozen</Badge>
            <span className="job-cmd-text">{n}</span>
            <Chip onClick={() => { void doThaw(n) }}>Thaw</Chip>
          </li>
        ))}
      </ul>
      <p className="muted">Suspends every thread of the named process to free CPU and RAM. Critical system processes are protected and never suspended. Affecting other apps needs the elevated service.</p>
    </Frame>
  )
}

// System health check / anomaly detection — pure rules on the daemon (core/Health/HealthCheck.cs)
// evaluated against a real telemetry snapshot. Polls slowly; this is diagnostic, not live telemetry.
export const HEALTH_LEVEL_LABEL: Record<string, string> = { warn: 'Warning', critical: 'Critical' }
const HEALTH_TONE = { ok: 'ok', warn: 'warn', critical: 'danger' } as const

export function HealthCard() {
  const [report, setReport] = useState<HealthReport | null>(null)
  useEffect(() => {
    const t = () => getHealthCheck().then(setReport).catch(() => {})
    t(); const id = setInterval(t, 5000); return () => clearInterval(id)
  }, [])

  return (
    <Frame title="System health" testid="health-card"
      hint={report && <Badge tone={HEALTH_TONE[report.status]} testid="health-status">{report.status}</Badge>}>
      {!report ? (
        <p className="muted">Evaluating the daemon's rule set…</p>
      ) : report.issues.length === 0 ? (
        <p className="health-ok-msg" data-testid="health-ok">✓ All good — no anomalies detected.</p>
      ) : (
        <ul className="rules" data-testid="health-issues">
          {report.issues.map((i) => (
            <li key={i.code} className={`rule cap-row health-issue-${i.level}`} data-testid={`health-issue-${i.code}`}>
              <span className="rule-app">{i.message}</span>
              <Badge tone={i.level === 'critical' ? 'danger' : 'warn'}>{HEALTH_LEVEL_LABEL[i.level] ?? i.level}</Badge>
              <span className="cap-reason">Rule {i.code}</span>
            </li>
          ))}
        </ul>
      )}
      <p className="muted">Checked every 5s from live telemetry — fan state, thermal ceiling, TDP verification, and battery discharge.</p>
    </Frame>
  )
}

// Panic cool — a dead-simple, always-available safety action: floor TDP + max fan, right now.
export const PANIC_STAPM_W = 8
export function PanicCoolButton() {
  const toast = useToast()
  const [busy, setBusy] = useState(false)

  const go = async () => {
    setBusy(true)
    const r = await panicCool().catch(() => null)
    setBusy(false)
    if (!r) { toast.push({ kind: 'error', message: 'Panic cool failed — could not reach the daemon' }); return }
    toast.push({
      kind: r.applied ? 'success' : 'warn',
      message: r.applied
        ? `Panic cool applied — floored to ${r.stapmW} W, fan Aggressive.`
        : `Panic cool requested (fan set to Aggressive), but the ${r.stapmW} W floor was not verified.`,
    })
  }

  return (
    <Frame title="Panic cool" testid="panic-card" hint={<Badge tone="danger">immediate</Badge>}>
      <div className="grid2">
        <Readout label="Sustained floor" value={String(PANIC_STAPM_W)} unit="W" />
        <Readout label="Fan" value="Aggressive" />
      </div>
      <p className="muted">Too hot, right now? Drop straight to an {PANIC_STAPM_W} W sustained floor and max out the fan — no waiting, no menus.</p>
      <div className="row-end">
        <Button variant="danger" testid="panic-cool" onClick={() => { void go() }} disabled={busy}>
          {busy ? 'Cooling…' : 'Panic cool'}
        </Button>
      </div>
    </Frame>
  )
}
