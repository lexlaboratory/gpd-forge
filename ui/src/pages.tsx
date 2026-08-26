// GPD Forge UI — pages. GPL-3.0-or-later.
import { useEffect, useRef, useState, type ChangeEvent } from 'react'
import type { Mode, ModeId, Telemetry, Preset, BatteryBudget, AutoFps, Guardian, AiInfo, ImportResult, PowerSourceConfig } from './types'
import {
  setTdp as apiSetTdp, getProfiles, setProfile, getBrightness, setBrightness, getFan, setFan,
  getBudget, getFrozen, freeze, thaw, getAutoFps, setAutoFps, getGuardian, setGuardian,
  getAi, setAntiStandby, getHistory, historyExportUrl, importMotionAssistant,
  getPowerSource, setPowerSource, settingsExportUrl, importSettings, type TdpResult,
} from './api'
import { Tile, Card, Slider, Toggle, Soon } from './ui'
import { Sparkline, useHistory } from './Chart'
import { useToast } from './Toast'
import { JobsPanel } from './JobsPanel'
import { StandbyPanel } from './StandbyPanel'

export const MODES: Mode[] = [
  { id: 'gaming',  label: 'Gaming',        icon: '🎮', blurb: 'Auto-TDP to target FPS, reactive fan, OSD.' },
  { id: 'ai',      label: 'Agents / AI',   icon: '🤖', blurb: 'Sustained CPU, VRAM/UMA, anti-standby, local API.' },
  { id: 'windows', label: 'Windows',       icon: '🪟', blurb: 'Balanced power, quiet fan, hotkeys.' },
  { id: 'battery', label: 'Battery',       icon: '🔋', blurb: 'Low TDP floor, longest runtime.' },
  { id: 'standby', label: 'Standby Doctor',icon: '🩺', blurb: 'Restore TDP+fan+HID on resume, fix drain.' },
]

// Short, correctly-cased chip labels for the preset keys (so 'ai' shows as 'AI', not 'Ai').
const PRESET_LABEL: Record<string, string> = {
  battery: 'Battery', windows: 'Windows', gaming: 'Gaming', ai: 'AI', standby: 'Standby',
}

export interface Shared {
  tele: Telemetry | null
  active: ModeId
  auto: boolean
  setAuto: (v: boolean) => void
  pickMode: (id: ModeId) => void
}

// --- Dashboard -----------------------------------------------------------------
export function DashboardPage({ tele, active, auto, pickMode }: Shared) {
  const [tdp, setTdp] = useState(20)
  const [tdpResult, setTdpResult] = useState<TdpResult | null>(null)
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)

  const onTdp = (v: number) => {
    setTdp(v)
    if (timer.current) clearTimeout(timer.current)
    timer.current = setTimeout(() => { apiSetTdp(v).then(setTdpResult).catch(() => {}) }, 120)
  }
  const verified = tdpResult ? tdpResult.verified : (tele?.tdpVerified ?? true)

  return (
    <>
      <section className="stats" aria-label="Live telemetry">
        <Tile testid="stat-cpu"  label="CPU"     value={tele ? `${Math.round(tele.cpuTempC)}` : '--'} unit="°C" />
        <Tile testid="stat-pkg"  label="Power"   value={tele ? `${Math.round(tele.packageW)}` : '--'} unit="W" />
        <Tile testid="stat-fan"  label="Fan"     value={tele ? `${tele.fanRpm}` : '--'} unit="rpm" />
        <Tile testid="stat-fps"  label="FPS"     value={tele ? `${Math.round(tele.fps)}` : '--'} />
        <Tile testid="stat-batt" label="Battery" value={tele ? `${tele.batteryPct}` : '--'} unit="%" />
      </section>

      <Card title="Modes" hint={<span data-testid="modes-hint">{auto ? 'Auto — optimizing for the app in focus' : 'Manual — you chose the mode'}</span>}>
        <div className="mode-grid" role="listbox" aria-label="Usage mode">
          {MODES.map((m) => (
            <button key={m.id} role="option" aria-selected={active === m.id} data-testid={`mode-${m.id}`}
              className={`mode-card ${active === m.id ? 'active' : ''}`} onClick={() => pickMode(m.id)}>
              {auto && active === m.id && <span className="mode-auto" data-testid="mode-auto">AUTO</span>}
              <span className="mode-icon" aria-hidden>{m.icon}</span>
              <span className="mode-label">{m.label}</span>
              <span className="mode-blurb">{m.blurb}</span>
            </button>
          ))}
        </div>
      </Card>

      <Card title="Sustained TDP" hint={<span className={`badge badge-${verified ? 'verified' : 'unverified'}`} data-testid="tdp-badge">{verified ? 'verified' : 'unverified'}</span>}>
        <div className="tdp-row">
          <input type="range" min={5} max={35} step={1} value={tdp} data-testid="tdp-slider" aria-label="Sustained TDP in watts" onChange={(e) => onTdp(Number(e.target.value))} />
          <output className="tdp-value" data-testid="tdp-value">{tdp} W</output>
        </div>
        <p className="muted">Applied with a closed loop — GPD Forge re-reads the PM table and warns if the firmware reverts it.</p>
      </Card>

      {active === 'ai' && <JobsPanel />}
      {active === 'ai' && <AiCard />}
      {active === 'standby' && <StandbyPanel />}
      <BatteryBudgetCard />
    </>
  )
}

// --- Agents / AI (anti-standby + sustained profile + VRAM/UMA advisory) ------
function AiCard() {
  const toast = useToast()
  const [info, setInfo] = useState<AiInfo | null>(null)

  useEffect(() => {
    const t = () => getAi().then(setInfo).catch(() => {})
    t(); const id = setInterval(t, 2000); return () => clearInterval(id)
  }, [])

  const toggle = async () => {
    if (!info) return
    const r = await setAntiStandby(!info.antiStandby.manual).catch(() => null)
    if (r) {
      setInfo((s) => (s ? { ...s, antiStandby: r } : s))
      toast.push({ kind: 'info', message: r.manual ? 'Anti-standby held (manual)' : 'Manual hold released' })
    }
  }

  if (!info) return null
  const { antiStandby: a, sustainedProfile: p, vram } = info
  return (
    <Card title="Anti-standby & sustained power" hint="Keeps Windows awake while an AI job runs">
      <div className="row">
        <Toggle on={a.manual} onClick={toggle} label={a.manual ? 'Manual hold on' : 'Manual hold off'} testid="ai-antistandby-toggle" />
      </div>
      <p className="muted" data-testid="ai-antistandby-status">
        {a.active
          ? `Holding Windows awake — ${a.holders} active hold${a.holders === 1 ? '' : 's'}.`
          : 'Not holding — Windows may enter Modern Standby normally.'}
      </p>
      <div className="stats">
        <Tile testid="ai-sustained-stapm" label="Sustained" value={`${p.stapmW}`} unit=" W" />
        <Tile label="Thermal limit" value={`${p.tctlC}`} unit="°C" />
        <Tile testid="ai-vram" label="iGPU VRAM/UMA" value={vram.available ? `${vram.reportedMb}` : '--'} unit={vram.available ? ' MB' : ''} />
      </div>
      <p className="muted" data-testid="ai-vram-advisory">{vram.advisory}</p>
    </Card>
  )
}

// --- Power (editable per-mode TDP presets) ------------------------------------
export function PowerPage() {
  const [presets, setPresets] = useState<Record<string, Preset>>({})
  const [mode, setMode] = useState<string>('gaming')
  const [draft, setDraft] = useState<Preset | null>(null)
  const [saved, setSaved] = useState(false)
  const [afps, setAfps] = useState<AutoFps>({ enabled: false, targetFps: 60 })
  const toast = useToast()

  useEffect(() => {
    getProfiles().then((p) => { setPresets(p); setDraft(p[mode] ?? null) }).catch(() => {})
    getAutoFps().then(setAfps).catch(() => {})
  }, [])
  useEffect(() => { setDraft(presets[mode] ?? null); setSaved(false) }, [mode, presets])

  const edit = (k: keyof Preset, v: number) => draft && setDraft({ ...draft, [k]: v })
  const apply = () => { if (draft) setProfile(mode, draft).then(() => { setSaved(true); toast.push({ kind: 'success', message: `${PRESET_LABEL[mode] ?? mode} preset saved` }) }).catch(() => {}) }
  const toggleFps = () => { const en = !afps.enabled; setAfps((s) => ({ ...s, enabled: en })); setAutoFps(afps.targetFps, en).then(setAfps).catch(() => {}) }
  const commitFps = (v: number) => { void setAutoFps(v, afps.enabled).then(setAfps).catch(() => {}) }

  return (
    <>
      <Card title="Power presets" hint="Tune each mode's TDP — GPD Forge applies it through the closed loop.">
        <div className="chips" data-testid="preset-modes">
          {Object.keys(presets).map((k) => (
            <button key={k} className={`chip-btn ${mode === k ? 'on' : ''}`} onClick={() => setMode(k)} data-testid={`preset-${k}`}>{PRESET_LABEL[k] ?? k}</button>
          ))}
        </div>
        {draft ? (
          <div className="grid2">
            <Slider label="STAPM (sustained)" testid="p-stapm" value={draft.stapmW} min={5} max={40} unit=" W" onChange={(v) => edit('stapmW', v)} />
            <Slider label="Fast (boost)"       testid="p-fast"  value={draft.fastW}  min={5} max={45} unit=" W" onChange={(v) => edit('fastW', v)} />
            <Slider label="Slow"               testid="p-slow"  value={draft.slowW}  min={5} max={45} unit=" W" onChange={(v) => edit('slowW', v)} />
            <Slider label="Thermal limit"      testid="p-tctl"  value={draft.tctlC}  min={60} max={95} unit=" °C" onChange={(v) => edit('tctlC', v)} />
          </div>
        ) : <p className="muted">Loading presets…</p>}
        <div className="row-end">
          {saved && <span className="badge badge-verified" data-testid="preset-saved">saved</span>}
          <button className="btn btn-accent" data-testid="preset-apply" onClick={apply} disabled={!draft}>Save preset</button>
        </div>
      </Card>
      <Card title="Auto-TDP to FPS" hint="Gaming — hold a target FPS at the least power">
        <div className="row">
          <Toggle on={afps.enabled} onClick={toggleFps} label={afps.enabled ? 'Enabled' : 'Disabled'} testid="autofps-toggle" />
        </div>
        <Slider label="Target FPS" testid="autofps-target" value={afps.targetFps} min={30} max={120} unit=" fps"
          onChange={(v) => setAfps((s) => ({ ...s, targetFps: v }))} onCommit={commitFps} />
        <p className="muted">Steers TDP with a PID to keep your FPS at target. Activates in gaming mode once FPS telemetry is available (PresentMon).</p>
      </Card>
      <Card title="GPU" hint={<Soon />}>
        <p className="muted">iGPU clock cap and UMA/VRAM assignment (for the Agents/AI mode) — via the broker, gated behind hardware approval.</p>
      </Card>
    </>
  )
}

// --- Display (brightness real) ------------------------------------------------
export function DisplayPage() {
  const [bri, setBri] = useState<number | null>(null)
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)
  useEffect(() => { getBrightness().then(setBri).catch(() => {}) }, [])
  const onBri = (v: number) => {
    setBri(v)
    if (timer.current) clearTimeout(timer.current)
    timer.current = setTimeout(() => { setBrightness(v).then(setBri).catch(() => {}) }, 150)
  }
  return (
    <>
      <Card title="Brightness" hint="Live (WMI)">
        <Slider label="Screen brightness" testid="brightness" value={bri ?? 0} min={0} max={100} unit=" %" onChange={onBri} />
        {bri === null && <p className="muted">Brightness not available from this context.</p>}
      </Card>
      <Card title="Screen" hint={<Soon />}>
        <p className="muted">Tablet-mode toggle, resolution and refresh-rate switching, night light.</p>
      </Card>
    </>
  )
}

// --- Fan ----------------------------------------------------------------------
const FAN_MODES = ['Auto', 'Quiet', 'Balanced', 'Aggressive', 'Manual']
export function FanPage({ tele }: { tele: Telemetry | null }) {
  const [fanMode, setFanMode] = useState('Auto')
  useEffect(() => { getFan().then(setFanMode).catch(() => {}) }, [])
  const pick = (f: string) => { setFanMode(f); setFan(f).catch(() => {}) }
  return (
    <>
      <Card title="Fan" hint="Preference saved now; curve applied when the fan driver lands.">
        <div className="stats">
          <Tile label="Fan" value={tele ? `${tele.fanRpm}` : '--'} unit="rpm" />
          <Tile label="CPU" value={tele ? `${Math.round(tele.cpuTempC)}` : '--'} unit="°C" />
          <Tile label="GPU" value={tele ? `${Math.round(tele.gpuTempC)}` : '--'} unit="°C" />
        </div>
        <div className="chips">
          {FAN_MODES.map((f) => (
            <button key={f} className={`chip-btn ${fanMode === f ? 'on' : ''}`} onClick={() => pick(f)} data-testid={`fan-${f.toLowerCase()}`}>{f}</button>
          ))}
        </div>
        <p className="muted">Curve editor with hysteresis + EC re-init on boot/resume lands with the fan driver (EC access pending PawnIO-stable).</p>
      </Card>
    </>
  )
}

// --- Controller ---------------------------------------------------------------
export function ControllerPage() {
  return (
    <Card title="Controller" hint={<Soon>ViGEmBus + HidHide</Soon>}>
      <p className="muted">Gyro (mouse / X360 / native DS4), back buttons L4/R4 remap, stick deadzones, rumble — with a 1024-byte config backup + read-back verify (anti-brick). Virtual pad + device hiding are gated behind the HID layer.</p>
      <div className="grid2">
        <Slider label="Left deadzone" value={5} min={0} max={20} onChange={() => {}} disabled />
        <Slider label="Right deadzone" value={5} min={0} max={20} onChange={() => {}} disabled />
      </div>
    </Card>
  )
}

// --- Profiles -----------------------------------------------------------------
const RULES = [
  { app: 'ollama / lmstudio / koboldcpp', mode: 'Agents / AI' },
  { app: 'steam / retroarch / emulators', mode: 'Gaming' },
  { app: 'anything else (on AC)', mode: 'Windows' },
  { app: 'anything else (on battery)', mode: 'Battery' },
]
function MotionAssistantImportCard() {
  const toast = useToast()
  const [result, setResult] = useState<ImportResult | null>(null)
  const [busy, setBusy] = useState(false)

  const doImport = async () => {
    setBusy(true)
    const r = await importMotionAssistant().catch(() => null)
    setBusy(false)
    if (!r) { toast.push({ kind: 'error', message: 'MotionAssistant import failed' }); return }
    setResult(r)
    toast.push({
      kind: r.found > 0 ? 'success' : 'info',
      message: r.found > 0 ? `Imported ${r.found} profile${r.found === 1 ? '' : 's'} from MotionAssistant` : `No MotionAssistant profiles found at ${r.path}`,
    })
  }

  return (
    <Card title="Import from MotionAssistant" hint="Reads MotionAssistant's saved per-profile TDP">
      <div className="row">
        <button className="btn btn-accent" data-testid="import-ma" onClick={doImport} disabled={busy}>
          {busy ? 'Importing…' : 'Import from MotionAssistant'}
        </button>
      </div>
      {result && (
        <ul className="rules" data-testid="import-ma-results">
          {result.profiles.length === 0 && <li className="rule">No profiles found at {result.path}</li>}
          {result.profiles.map((p) => (
            <li key={p.name} className="rule">
              <span className="rule-app">{p.name}</span>
              <span className="rule-arrow">→</span>
              <span className="rule-mode">{p.stapmW}/{p.fastW}/{p.slowW} W · {p.tctlC}°C</span>
            </li>
          ))}
        </ul>
      )}
      <p className="muted">Apply an imported profile's numbers on the Power page's presets — this only reads and lists them.</p>
    </Card>
  )
}

export function ProfilesPage() {
  return (
    <>
      <MotionAssistantImportCard />
      <Card title="Per-app profiles" hint="Foreground app → mode (with anti-flapping)">
        <ul className="rules">
          {RULES.map((r) => (
            <li key={r.app} className="rule"><span className="rule-app">{r.app}</span><span className="rule-arrow">→</span><span className="rule-mode">{r.mode}</span></li>
          ))}
        </ul>
        <p className="muted">Custom, versioned, shareable per-game profiles (import from the community) — <Soon />.</p>
      </Card>
    </>
  )
}

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
      <Card title="Live telemetry" hint="Last 60 seconds">
        <div className="charts" data-testid="charts">
          <Sparkline data={cpu} label="CPU" unit="°C" color="var(--accent)" width={360} height={92} surface="var(--bg-elev)" testid="chart-cpu" />
          <Sparkline data={watt} label="Power" unit="W" color="var(--accent-2)" width={360} height={92} surface="var(--bg-elev)" testid="chart-watt" />
          <Sparkline data={fps} label="FPS" color="var(--good)" width={360} height={92} surface="var(--bg-elev)" testid="chart-fps" />
        </div>
      </Card>
      <Card title="History" hint="Recorded once per second by the daemon">
        <p className="muted" data-testid="history-count">
          {sampleCount === null ? 'Loading…' : `${sampleCount} sample${sampleCount === 1 ? '' : 's'} in the last 5 minutes`}
        </p>
        <div className="row-end">
          <a className="btn btn-accent" data-testid="history-export" href={historyExportUrl()} download="gpd-forge-telemetry.csv">Export CSV</a>
        </div>
      </Card>
      <Card title="On-screen display" hint={<Soon>RTSS single-owner</Soon>}>
        <p className="muted">Overlay via RTSS shared-memory (and an Xbox Game Bar widget) with frame limiter and 1%-low tracking — arbitrated so it never fights MSI Afterburner / GPD Tool.</p>
      </Card>
    </>
  )
}

// --- System -------------------------------------------------------------------
function FreezerCard() {
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
    <Card title="Freezer" hint="Suspend background apps to free CPU/RAM">
      <div className="job-form">
        <input className="job-input" data-testid="freezer-name" value={name} onChange={(e) => setName(e.target.value)} placeholder="process name (e.g. chrome)" aria-label="process to freeze" />
        <button className="btn" data-testid="freezer-freeze" onClick={doFreeze}>Freeze</button>
      </div>
      <ul className="job-list" data-testid="frozen-list">
        {frozen.length === 0 && <li className="job-empty">Nothing frozen.</li>}
        {frozen.map((n) => (
          <li key={n} className="job-row"><span className="job-cmd-text">{n}</span><button className="chip-btn" onClick={() => doThaw(n)}>Thaw</button></li>
        ))}
      </ul>
      <p className="muted">Critical system processes are protected and never suspended. Affecting other apps needs the elevated service.</p>
    </Card>
  )
}

export function SystemPage({ tele }: { tele: Telemetry | null }) {
  return (
    <>
      <Card title="Power controller" hint="GPD Forge yields while another controller runs.">
        <p className="muted">GPD Forge takes over TDP only when it is the sole owner. Use the installer's <code>-Substitute</code> to stop + disable MotionAssistant / GPD Tool. TDP now: <b>{tele?.tdpVerified ? 'verified' : '—'}</b>.</p>
      </Card>
      <FreezerCard />
      <StandbyPanel />
    </>
  )
}

function BatteryBudgetCard() {
  const [b, setB] = useState<BatteryBudget | null>(null)
  useEffect(() => {
    const t = () => getBudget().then(setB).catch(() => {})
    t(); const id = setInterval(t, 5000); return () => clearInterval(id)
  }, [])
  if (!b || b.minutesRemaining == null) return null
  return (
    <Card title="Battery budget" hint={`${b.dischargeW.toFixed(1)} W now`}>
      <div className="battery-budget" data-testid="battery-budget">
        <div className="bb-main">{b.minutesRemaining}<span className="tile-unit"> min left</span></div>
        <div className="bb-proj">{b.projections.map((p) => <span key={p.watts} className="bb-chip">{p.watts}W → {p.minutes}m</span>)}</div>
      </div>
    </Card>
  )
}

// --- Settings -----------------------------------------------------------------
function PowerSourceCard() {
  const toast = useToast()
  const [cfg, setCfg] = useState<PowerSourceConfig | null>(null)
  useEffect(() => { getPowerSource().then(setCfg).catch(() => {}) }, [])

  const patch = async (p: Partial<PowerSourceConfig>) => {
    const n = await setPowerSource(p).catch(() => null)
    if (n) { setCfg(n); toast.push({ kind: 'info', message: 'Power source config updated' }) }
  }

  if (!cfg) return null
  return (
    <Card title="Power source" hint="Switch mode automatically when AC connects or disconnects">
      <div className="row">
        <Toggle on={cfg.enabled} onClick={() => patch({ enabled: !cfg.enabled })} label={cfg.enabled ? 'Enabled' : 'Disabled'} testid="powersource-enabled" />
      </div>
      <p className="muted">On battery</p>
      <div className="chips" data-testid="powersource-battery-modes">
        {MODES.map((m) => (
          <button key={m.id} className={`chip-btn ${cfg.onBatteryMode === m.id ? 'on' : ''}`}
            onClick={() => patch({ onBatteryMode: m.id })} data-testid={`powersource-battery-${m.id}`}>{m.label}</button>
        ))}
      </div>
      <p className="muted">On AC</p>
      <div className="chips" data-testid="powersource-ac-modes">
        {MODES.map((m) => (
          <button key={m.id} className={`chip-btn ${cfg.onAcMode === m.id ? 'on' : ''}`}
            onClick={() => patch({ onAcMode: m.id })} data-testid={`powersource-ac-${m.id}`}>{m.label}</button>
        ))}
      </div>
    </Card>
  )
}

function BackupRestoreCard() {
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
    <Card title="Backup / restore" hint="Export a full settings snapshot, or apply one back">
      <div className="row">
        <a className="btn btn-accent" data-testid="settings-export" href={settingsExportUrl()} download="gpd-forge-settings.json">Export settings</a>
      </div>
      <div className="row">
        <input type="file" accept="application/json" data-testid="settings-import-file" aria-label="Settings backup file" onChange={onFile} />
        <button className="btn" data-testid="settings-import-apply" onClick={onApply} disabled={!pending || busy}>
          {busy ? 'Restoring…' : 'Restore backup'}
        </button>
      </div>
      {fileName && <p className="muted" data-testid="settings-import-filename">Loaded: {fileName}</p>}
    </Card>
  )
}

function GuardianCard() {
  const toast = useToast()
  const [g, setG] = useState<Guardian | null>(null)
  useEffect(() => { getGuardian().then(setG).catch(() => {}) }, [])
  const patch = async (p: Partial<Guardian>) => {
    const n = await setGuardian(p).catch(() => null)
    if (n) { setG((prev) => ({ ...(prev as Guardian), ...n })); toast.push({ kind: 'info', message: 'Guardian updated' }) }
  }
  if (!g) return null
  return (
    <Card title="Guardian" hint="Auto-throttle + alerts on overheat / low battery">
      <div className="row">
        <Toggle on={g.enabled} onClick={() => patch({ enabled: !g.enabled })} label="Enabled" testid="guardian-enabled" />
      </div>
      <div className="row">
        <Toggle on={g.autoThrottle} onClick={() => patch({ autoThrottle: !g.autoThrottle })} label="Auto-throttle TDP on overheat" testid="guardian-autothrottle" />
      </div>
      <div className="stats">
        <Tile label="Throttle at" value={`${g.tempThrottleC}`} unit="°C" />
        <Tile label="Critical" value={`${g.tempCriticalC}`} unit="°C" />
        <Tile label="Floor" value={`${g.throttleFloorW}`} unit="W" />
        <Tile label="Battery low" value={`${g.batteryLowPct}`} unit="%" />
      </div>
      <p className="muted" data-testid="guardian-status">
        {g.throttling ? `Throttling to ${g.throttledToW} W. ` : 'Not throttling. '}
        {g.lastAlert ? `Last alert: ${g.lastAlert}` : 'No alerts.'}
      </p>
    </Card>
  )
}

export function SettingsPage({ auto, setAuto, theme, setTheme }: {
  auto: boolean; setAuto: (v: boolean) => void; theme: 'dark' | 'light'; setTheme: (t: 'dark' | 'light') => void
}) {
  return (
    <>
      <Card title="Automation">
        <div className="row">
          <Toggle on={auto} onClick={() => setAuto(!auto)} label="Auto-optimize by app in focus" testid="settings-auto" />
        </div>
        <p className="muted">When on, GPD Forge switches modes automatically from the foreground app.</p>
      </Card>
      <Card title="Appearance">
        <div className="row">
          <Toggle on={theme === 'dark'} onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')} label="Dark theme" testid="settings-theme" />
        </div>
      </Card>
      <PowerSourceCard />
      <GuardianCard />
      <BackupRestoreCard />
      <Card title="About">
        <p className="muted">GPD Forge — the definitive open-source tuning tool for GPD handhelds. GPL-3.0 · lexlaboratory · github.com/lexlaboratory/gpd-forge</p>
      </Card>
    </>
  )
}
