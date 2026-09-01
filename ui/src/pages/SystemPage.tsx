// GPD Forge UI — System page plus battery budget, power source, backup/restore, guardian, update note. GPL-3.0-or-later.
import { useEffect, useState, type ChangeEvent } from 'react'
import type { Telemetry, BatteryBudget, BatteryHealth, ChargeGuard, Guardian, PowerSourceConfig, UpdateCheck } from '../types'
import {
  getBudget, getBatteryHealth, getChargeGuard, setChargeGuard, getGuardian, setGuardian, getPowerSource, setPowerSource,
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
      {/* Here rather than on the Dashboard next to the budget: the budget answers "how long until
          this dies", which changes minute to minute, while health answers "how much pack is left
          after two years", which changes over months. Putting a figure that barely moves in a live
          panel teaches people to stop reading the panel. */}
      <BatteryHealthCard />
      <ChargeGuardCard />
      <Frame title="Power controller" hint={<Badge tone={tele?.tdpVerified ? 'ok' : 'muted'}>{tele?.tdpVerified ? 'TDP verified' : 'TDP unverified'}</Badge>}>
        <p className="muted">GPD Forge yields while another controller runs: it takes over TDP only when it is the sole owner. Use the installer's <code>-Substitute</code> to stop + disable MotionAssistant / GPD Tool.</p>
      </Frame>
      <FreezerCard />
      {/* No "not measured" caveat here any more: the daemon actually measures now, and the panel
          reports per-field whether it has a reading. A blanket warning next to real data would be
          its own kind of dishonesty. */}
      <StandbyPanel />
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

/**
 * How much of the pack's factory capacity survives, and how fast it is going.
 *
 * Written around what this board WILL NOT tell us. Cycle count and cell temperature are null here
 * and the card says so with the daemon's own reason rather than hiding the rows — a missing row
 * looks like a bug, and an invented number is worse than either. The trend is likewise absent until
 * there are samples from two different days, and it explains itself while it waits.
 */
export function BatteryHealthCard() {
  const [h, setH] = useState<BatteryHealth | null>(null)
  // Fetched once per mount: capacity moves over months, so polling it would be noise.
  useEffect(() => { getBatteryHealth().then(setH).catch(() => {}) }, [])

  if (!h) return null

  if (h.unavailable) {
    return (
      <Frame title="Battery health">
        <p className="muted" data-testid="battery-health-unavailable">{h.unavailable}</p>
      </Frame>
    )
  }

  // No health figure means a capacity read failed. Showing the card with a dash is right; showing
  // "0 %" would announce a dead battery because a WMI query came back empty.
  const pct = h.healthPercent

  return (
    <Frame
      title="Battery health"
      hint={h.chemistry ? <Badge tone="muted">{h.chemistry}</Badge> : undefined}
    >
      <div className="battery-budget" data-testid="battery-health">
        <div className="bb-main" data-testid="battery-health-pct">
          {pct == null ? '--' : pct.toFixed(1)}<span className="tile-unit"> % of design</span>
        </div>
        <div className="bb-proj">
          {h.fullChargeMwh != null && h.designedMwh != null && (
            <span className="bb-chip" data-testid="battery-health-capacity">
              {(h.fullChargeMwh / 1000).toFixed(1)} of {(h.designedMwh / 1000).toFixed(1)} Wh
            </span>
          )}
          {h.degradationPoints != null && (
            <span className="bb-chip" data-testid="battery-health-trend">
              {h.degradationPoints >= 0 ? '−' : '+'}{Math.abs(h.degradationPoints).toFixed(1)} pts
              {' '}over {h.samples.length} samples
            </span>
          )}
        </div>
      </div>

      {h.trendUnavailable && (
        <p className="muted" data-testid="battery-health-trend-pending">{h.trendUnavailable}</p>
      )}
      {h.cycleCountUnavailable && (
        <p className="muted" data-testid="battery-health-cycles-unavailable">
          Cycle count: not reported. {h.cycleCountUnavailable}
        </p>
      )}
      {h.cycleCount != null && (
        <p className="muted" data-testid="battery-health-cycles">Cycle count: {h.cycleCount}</p>
      )}
    </Frame>
  )
}

/**
 * The charge guard.
 *
 * The card leads with what it CANNOT do, because the obvious expectation of anything called a charge
 * guard is "stop at 80 %", and this board has no path to that. Burying the refusal under a row of
 * toggles would let someone assume the feature is doing something it is not — which is worse than
 * not shipping it, since they would stop worrying about a pack that is still ageing.
 */
export function ChargeGuardCard() {
  const toast = useToast()
  const [g, setG] = useState<ChargeGuard | null>(null)
  useEffect(() => { getChargeGuard().then(setG).catch(() => {}) }, [])

  const patch = async (p: Parameters<typeof setChargeGuard>[0]) => {
    const next = await setChargeGuard(p).catch(() => null)
    if (!next) return
    // The POST returns only the settings, so merge rather than replace: assigning the response
    // wholesale would blank the counters until the next mount.
    setG((prev) => (prev ? { ...prev, ...next } : prev))
    toast.push({ kind: 'info', message: 'Charge guard updated' })
  }

  if (!g) return null

  return (
    <Frame
      title="Charge guard"
      hint={<Badge tone={g.enabled ? 'ok' : 'muted'}>{g.enabled ? 'Watching' : 'Off'}</Badge>}
    >
      <div className="battery-budget" data-testid="charge-guard">
        <div className="bb-main" data-testid="charge-guard-hours">
          {g.totalHoursAtHighSoc.toFixed(1)}<span className="tile-unit"> h at high charge</span>
        </div>
        <div className="bb-proj">
          <span className="bb-chip">{g.episodes} episode{g.episodes === 1 ? '' : 's'}</span>
          {g.episodeHours != null && (
            <span className="bb-chip" data-testid="charge-guard-episode">
              plugged in {g.episodeHours.toFixed(1)} h now
            </span>
          )}
        </div>
      </div>

      <p className="muted" data-testid="charge-guard-advisory">{g.advisory}</p>

      <Toggle
        on={g.enabled}
        onClick={() => patch({ enabled: !g.enabled })}
        label="Count hours spent plugged in and full"
        testid="charge-guard-enabled"
      />
      <Toggle
        on={g.coolWhileCharging}
        onClick={() => patch({ coolWhileCharging: !g.coolWhileCharging })}
        label={`Hold ${g.coolToW} W while the pack sits above ${g.highSocPct}%`}
        testid="charge-guard-cool"
      />
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
