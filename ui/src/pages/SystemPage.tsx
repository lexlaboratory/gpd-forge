// GPD Forge UI — System page plus battery budget, power source, backup/restore, guardian, update note. GPL-3.0-or-later.
import { useEffect, useState, type ChangeEvent } from 'react'
import type { Telemetry, BatteryBudget, Guardian, PowerSourceConfig, UpdateCheck } from '../types'
import {
  getBudget, getGuardian, setGuardian, getPowerSource, setPowerSource,
  settingsExportUrl, importSettings, checkUpdate,
} from '../api'
import { Frame, Readout, Toggle, Button, Segmented, Badge } from '../components'
import { useToast } from '../Toast'
import { StandbyPanel } from '../StandbyPanel'
import { MODES } from './shared'
import { HealthCard, PanicCoolButton, FreezerCard } from './MonitorPage'

export function SystemPage({ tele }: { tele: Telemetry | null }) {
  return (
    <>
      <HealthCard />
      <PanicCoolButton />
      <Frame title="Power controller" hint={<Badge tone={tele?.tdpVerified ? 'ok' : 'muted'}>{tele?.tdpVerified ? 'TDP verified' : 'TDP unverified'}</Badge>}>
        <p className="muted">GPD Forge yields while another controller runs: it takes over TDP only when it is the sole owner. Use the installer's <code>-Substitute</code> to stop + disable MotionAssistant / GPD Tool.</p>
      </Frame>
      <FreezerCard />
      <StandbyPanel />
      {/* GET /standby currently answers with hardcoded literals, so the numbers above the button are
          not a measurement. Said here rather than in StandbyPanel, which this redesign does not own. */}
      <p className="muted" data-testid="standby-unverified">
        <Badge tone="warn">not measured</Badge> The drain, wake reason and blocker figures above are
        placeholders the daemon does not yet collect. "Run resume restore" is real; the readings are not.
      </p>
    </>
  )
}

export function BatteryBudgetCard() {
  const [b, setB] = useState<BatteryBudget | null>(null)
  useEffect(() => {
    const t = () => getBudget().then(setB).catch(() => {})
    t(); const id = setInterval(t, 5000); return () => clearInterval(id)
  }, [])
  if (!b || b.minutesRemaining == null) return null
  return (
    <Frame title="Battery budget" hint={`${b.dischargeW.toFixed(1)} W now`}>
      <div className="battery-budget" data-testid="battery-budget">
        <div className="bb-main">{b.minutesRemaining}<span className="tile-unit"> min left</span></div>
        <div className="bb-proj">{b.projections.map((p) => <span key={p.watts} className="bb-chip">{p.watts}W → {p.minutes}m</span>)}</div>
      </div>
    </Frame>
  )
}

// --- Settings -----------------------------------------------------------------
export function PowerSourceCard() {
  const toast = useToast()
  const [cfg, setCfg] = useState<PowerSourceConfig | null>(null)
  useEffect(() => { getPowerSource().then(setCfg).catch(() => {}) }, [])

  const patch = async (p: Partial<PowerSourceConfig>) => {
    const n = await setPowerSource(p).catch(() => null)
    if (n) { setCfg(n); toast.push({ kind: 'info', message: 'Power source config updated' }) }
  }

  if (!cfg) return null
  const options = (prefix: string) => MODES.map((m) => ({ id: m.id, label: m.label, testid: `${prefix}-${m.id}` }))
  return (
    <Frame title="Power source" hint="Switch mode automatically when AC connects or disconnects">
      <div className="row">
        <Toggle on={cfg.enabled} onClick={() => patch({ enabled: !cfg.enabled })} label={cfg.enabled ? 'Enabled' : 'Disabled'} testid="powersource-enabled" />
      </div>
      <p className="muted">On battery</p>
      <Segmented label="Mode on battery" testid="powersource-battery-modes"
        options={options('powersource-battery')} value={cfg.onBatteryMode}
        onChange={(id) => patch({ onBatteryMode: id })} />
      <p className="muted">On AC</p>
      <Segmented label="Mode on AC" testid="powersource-ac-modes"
        options={options('powersource-ac')} value={cfg.onAcMode}
        onChange={(id) => patch({ onAcMode: id })} />
    </Frame>
  )
}

export function BackupRestoreCard() {
  const toast = useToast()
  const [pending, setPending] = useState<unknown>(null)
  const [fileName, setFileName] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const onFile = (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file) return
    setFileName(file.name)
    setPending(null)
    file.text().then((text) => {
      try { setPending(JSON.parse(text)) }
      catch { toast.push({ kind: 'error', message: `${file.name} is not valid JSON` }) }
    }).catch(() => toast.push({ kind: 'error', message: `Could not read ${file.name}` }))
  }

  const onApply = async () => {
    if (!pending) return
    setBusy(true)
    const r = await importSettings(pending).catch(() => null)
    setBusy(false)
    if (!r) { toast.push({ kind: 'error', message: 'Restore failed' }); return }
    toast.push({ kind: 'success', message: r.applied.length ? `Restored: ${r.applied.join(', ')}` : 'Nothing recognized in that file' })
    setPending(null)
    setFileName(null)
  }

  return (
    <Frame title="Backup / restore" hint="Export a full settings snapshot, or apply one back">
      <div className="row">
        <Button variant="accent" testid="settings-export" href={settingsExportUrl()} download="gpd-forge-settings.json">Export settings</Button>
      </div>
      <div className="row">
        <input type="file" accept="application/json" data-testid="settings-import-file" aria-label="Settings backup file" onChange={onFile} />
        <Button testid="settings-import-apply" onClick={onApply} disabled={!pending || busy}>
          {busy ? 'Restoring…' : 'Restore backup'}
        </Button>
      </div>
      {fileName && <p className="muted" data-testid="settings-import-filename">Loaded: {fileName}</p>}
    </Frame>
  )
}

export function GuardianCard() {
  const toast = useToast()
  const [g, setG] = useState<Guardian | null>(null)
  useEffect(() => { getGuardian().then(setG).catch(() => {}) }, [])
  const patch = async (p: Partial<Guardian>) => {
    const n = await setGuardian(p).catch(() => null)
    if (n) { setG((prev) => ({ ...(prev as Guardian), ...n })); toast.push({ kind: 'info', message: 'Guardian updated' }) }
  }
  if (!g) return null
  return (
    <Frame title="Guardian" hint={<Badge tone={g.throttling ? 'warn' : 'ok'}>{g.throttling ? 'throttling' : 'idle'}</Badge>}>
      <p className="muted">Auto-throttle plus alerts on overheat or low battery.</p>
      <div className="row">
        <Toggle on={g.enabled} onClick={() => patch({ enabled: !g.enabled })} label="Enabled" testid="guardian-enabled" />
      </div>
      <div className="row">
        <Toggle on={g.autoThrottle} onClick={() => patch({ autoThrottle: !g.autoThrottle })} label="Auto-throttle TDP on overheat" testid="guardian-autothrottle" />
      </div>
      <div className="stats">
        <Readout label="Throttle at" value={`${g.tempThrottleC}`} unit="°C" />
        <Readout label="Critical" value={`${g.tempCriticalC}`} unit="°C" />
        <Readout label="Floor" value={`${g.throttleFloorW}`} unit="W" />
        <Readout label="Battery low" value={`${g.batteryLowPct}`} unit="%" />
      </div>
      <p className="muted" data-testid="guardian-status">
        {g.throttling ? `Throttling to ${g.throttledToW} W. ` : 'Not throttling. '}
        {g.lastAlert ? `Last alert: ${g.lastAlert}` : 'No alerts.'}
      </p>
    </Frame>
  )
}

// Update note — only rendered once an update is actually confirmed available.
export function UpdateNote() {
  const [info, setInfo] = useState<UpdateCheck | null>(null)
  useEffect(() => { checkUpdate().then(setInfo).catch(() => {}) }, [])
  if (!info?.updateAvailable) return null
  return (
    <p className="muted" data-testid="update-available">
      Update available → {info.latest}
      {info.url && <> · <a href={info.url} target="_blank" rel="noreferrer">Release notes</a></>}
    </p>
  )
}
