// GPD Forge UI — pages. GPL-3.0-or-later.
import { useEffect, useRef, useState } from 'react'
import type { Mode, ModeId, Telemetry, Preset, BatteryBudget, AutoFps } from './types'
import {
  setTdp as apiSetTdp, getProfiles, setProfile, getBrightness, setBrightness, getFan, setFan,
  getBudget, getFrozen, freeze, thaw, getAutoFps, setAutoFps, type TdpResult,
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
      {active === 'standby' && <StandbyPanel />}
      <BatteryBudgetCard />
    </>
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
export function ProfilesPage() {
  return (
    <Card title="Per-app profiles" hint="Foreground app → mode (with anti-flapping)">
      <ul className="rules">
        {RULES.map((r) => (
          <li key={r.app} className="rule"><span className="rule-app">{r.app}</span><span className="rule-arrow">→</span><span className="rule-mode">{r.mode}</span></li>
        ))}
      </ul>
      <p className="muted">Custom, versioned, shareable per-game profiles (import from the community) — <Soon />.</p>
    </Card>
  )
}

// --- Monitor ------------------------------------------------------------------
export function MonitorPage({ tele }: { tele: Telemetry | null }) {
  const cpu = useHistory(tele?.cpuTempC ?? NaN)
  const watt = useHistory(tele?.packageW ?? NaN)
  const fps = useHistory(tele?.fps ?? NaN)
  return (
    <>
      <Card title="Live telemetry" hint="Last 60 seconds">
        <div className="charts" data-testid="charts">
          <Sparkline data={cpu} label="CPU" unit="°C" color="var(--accent)" width={360} height={92} surface="var(--bg-elev)" testid="chart-cpu" />
          <Sparkline data={watt} label="Power" unit="W" color="var(--accent-2)" width={360} height={92} surface="var(--bg-elev)" testid="chart-watt" />
          <Sparkline data={fps} label="FPS" color="var(--good)" width={360} height={92} surface="var(--bg-elev)" testid="chart-fps" />
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
      <Card title="About">
        <p className="muted">GPD Forge — the definitive open-source tuning tool for GPD handhelds. GPL-3.0 · lexlaboratory · github.com/lexlaboratory/gpd-forge</p>
      </Card>
    </>
  )
}
