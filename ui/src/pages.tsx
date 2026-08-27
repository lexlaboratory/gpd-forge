// GPD Forge UI — pages. GPL-3.0-or-later.
import { useEffect, useRef, useState, type ChangeEvent } from 'react'
import type {
  Mode, ModeId, Telemetry, Preset, BatteryBudget, AutoFps, Guardian, AiInfo, ImportResult, PowerSourceConfig,
  RefreshRateInfo, NightMode, TabletModeInfo, KeyboardBacklightInfo, TuneGoal, TunerInfo, UpdateCheck,
  LedMode, LedInfo, ChargeLimitInfo, UndervoltInfo, HealthReport, FanInfo,
} from './types'
import {
  setTdp as apiSetTdp, getProfiles, setProfile, getBrightness, setBrightness, setFan, getFanInfo, setFanManualDuty,
  getBudget, getFrozen, freeze, thaw, getAutoFps, setAutoFps, getGuardian, setGuardian,
  getAi, setAntiStandby, getHistory, historyExportUrl, importMotionAssistant,
  getPowerSource, setPowerSource, settingsExportUrl, importSettings, type TdpResult,
  getRefreshRate, setRefreshRate, getNightMode, setNightMode, getTabletMode, getKeyboardBacklight,
  getTuner, startTuner, checkUpdate,
  getLed, setLed, getChargeLimit, setChargeLimit, getUndervolt, setUndervolt,
  getHealthCheck, panicCool,
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

// --- Auto-tuner (TDP sweep) -----------------------------------------------------
const TUNE_GOALS: { id: TuneGoal; label: string }[] = [
  { id: 'MaxFps', label: 'Max FPS' },
  { id: 'BestEfficiency', label: 'Best efficiency' },
  { id: 'HoldTarget', label: 'Hold target FPS' },
]

function TunerCard() {
  const toast = useToast()
  const [goal, setGoal] = useState<TuneGoal>('MaxFps')
  const [targetFps, setTargetFps] = useState(60)
  const [info, setInfo] = useState<TunerInfo | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => { getTuner().then(setInfo).catch(() => {}) }, [])

  const start = async () => {
    setBusy(true)
    const r = await startTuner({ goal, targetFps: goal === 'HoldTarget' ? targetFps : undefined }).catch(() => null)
    setBusy(false)
    if (!r) { toast.push({ kind: 'error', message: 'Could not start the tuner sweep' }); return }
    setInfo(r)
    toast.push({
      kind: r.best ? 'success' : 'info',
      message: r.best ? `Best: ${r.best.stapmW} W → ${r.best.fps} fps` : (r.note ?? 'Sweep finished with no usable points'),
    })
  }

  const status = !info
    ? 'Loading…'
    : info.running
      ? `Sweeping… ${info.currentStapmW} W now.`
      : info.best
        ? `Best: ${info.best.stapmW} W → ${info.best.fps} fps @ ${info.best.tempC}°C — ${info.best.note}`
        : (info.note ?? 'No result yet — start a sweep.')

  return (
    <Card title="Auto-tuner" hint="Sweeps TDP and picks the best point for your goal">
      <div className="chips" data-testid="tuner-goals">
        {TUNE_GOALS.map((g) => (
          <button key={g.id} className={`chip-btn ${goal === g.id ? 'on' : ''}`} onClick={() => setGoal(g.id)} data-testid={`tuner-goal-${g.id}`}>{g.label}</button>
        ))}
      </div>
      {goal === 'HoldTarget' && (
        <Slider label="Target FPS" testid="tuner-target" value={targetFps} min={30} max={144} unit=" fps" onChange={(v) => setTargetFps(v)} />
      )}
      <div className="row-end">
        <button className="btn btn-accent" data-testid="tuner-start" onClick={start} disabled={busy}>{busy ? 'Sweeping…' : 'Start sweep'}</button>
      </div>
      <p className="muted" data-testid="tuner-status">{status}</p>
      {info?.best && (
        <div className="stats" data-testid="tuner-best">
          <Tile label="Best STAPM" value={`${info.best.stapmW}`} unit=" W" />
          <Tile label="FPS" value={`${info.best.fps}`} />
          <Tile label="Temp" value={`${info.best.tempC}`} unit="°C" />
        </div>
      )}
      <p className="muted">Honesty note: this HX370 has no FPS telemetry yet (PresentMon isn't wired), so a real sweep records nothing useful and honestly reports no result rather than a faked one. The mock daemon simulates FPS so this card is fully exercisable in dev/E2E.</p>
    </Card>
  )
}

// --- Advanced (hardware-gated): LED/RGB, battery charge limit, undervolt/Curve Optimizer --------
// All three are ADVISORY: GPD Forge validates and stores the request for real, but only ever
// attempts a write when the daemon's hardware gate is open — and on this HX370 unit, even then,
// there is no working write path yet (HID config, EC/BIOS, and RyzenAdj all lack one). The mock
// daemon plays along as controllable/available so this round-trips in dev/E2E; the real daemon
// stays honest (see docs/api.md).
const LED_MODES: LedMode[] = ['Off', 'Solid', 'Breathe', 'Rotate']

function LedCard() {
  const toast = useToast()
  const [info, setInfo] = useState<LedInfo | null>(null)
  const [color, setColor] = useState('#00c8ff')

  useEffect(() => { getLed().then((s) => { setInfo(s); setColor(s.color) }).catch(() => {}) }, [])

  const pick = async (mode: LedMode) => {
    const r = await setLed(mode, color).catch(() => null)
    if (!r) return
    setInfo(r)
    toast.push({ kind: r.applied ? 'success' : 'info', message: r.applied ? `LED set to ${mode}` : r.advisory })
  }
  const onColor = (e: ChangeEvent<HTMLInputElement>) => {
    const next = e.target.value
    setColor(next)
    if (info) void setLed(info.mode, next).then(setInfo).catch(() => {})
  }

  return (
    <Card title="LED / RGB" hint={<Soon>{info?.applied ? 'writable' : 'gated'}</Soon>}>
      <div className="chips" data-testid="led-modes">
        {LED_MODES.map((m) => (
          <button key={m} className={`chip-btn ${info?.mode === m ? 'on' : ''}`}
            onClick={() => pick(m)} data-testid={`led-${m.toLowerCase()}`}>{m}</button>
        ))}
      </div>
      <div className="row">
        <span>Color</span>
        <input type="color" className="led-color" value={color} onChange={onColor} data-testid="led-color" aria-label="LED color" />
      </div>
      <p className="muted" data-testid="led-advisory">{info?.advisory ?? 'Loading…'}</p>
    </Card>
  )
}

function ChargeLimitRow() {
  const toast = useToast()
  const [info, setInfo] = useState<ChargeLimitInfo | null>(null)
  useEffect(() => { getChargeLimit().then(setInfo).catch(() => {}) }, [])

  const commit = (v: number) => {
    void setChargeLimit(v).then((r) => {
      setInfo(r)
      toast.push({ kind: r.applied ? 'success' : 'info', message: r.applied ? `Charge limit set to ${r.percent}%` : r.advisory })
    }).catch(() => {})
  }

  return (
    <Card title="Battery charge limit" hint={<Soon>{info?.available ? 'readable' : 'gated'}</Soon>}>
      <Slider label="Stop charging at" testid="charge-limit" value={info?.percent ?? 100} min={50} max={100} unit=" %"
        onChange={(v) => setInfo((s) => (s ? { ...s, percent: v } : s))} onCommit={commit} />
      <p className="muted" data-testid="charge-limit-advisory">{info?.advisory ?? 'Loading…'}</p>
    </Card>
  )
}

function UndervoltRow() {
  const toast = useToast()
  const [info, setInfo] = useState<UndervoltInfo | null>(null)
  useEffect(() => { getUndervolt().then(setInfo).catch(() => {}) }, [])

  const commit = (coCount: number, offsetMv: number) => {
    void setUndervolt(coCount, offsetMv).then((r) => { setInfo(r); toast.push({ kind: 'info', message: r.advisory }) }).catch(() => {})
  }

  return (
    <Card title="Undervolt / Curve Optimizer" hint={<Soon>advisory</Soon>}>
      <div className="grid2">
        <Slider label="CO count (all-core)" testid="undervolt-co" value={info?.coCount ?? 0} min={-30} max={30}
          onChange={(v) => setInfo((s) => (s ? { ...s, coCount: v } : s))} onCommit={(v) => commit(v, info?.offsetMv ?? 0)} />
        <Slider label="Offset" testid="undervolt-mv" value={info?.offsetMv ?? 0} min={-100} max={100} unit=" mV"
          onChange={(v) => setInfo((s) => (s ? { ...s, offsetMv: v } : s))} onCommit={(v) => commit(info?.coCount ?? 0, v)} />
      </div>
      <p className="muted" data-testid="undervolt-advisory">{info?.advisory ?? 'Loading…'}</p>
    </Card>
  )
}

function AdvancedHardwarePanel() {
  return (
    <section className="panel" data-testid="advanced-hardware-panel" aria-label="Advanced hardware-gated controls">
      <h2 className="section-title">Advanced (hardware-gated)</h2>
      <p className="panel-note">
        Real validators, real stored state — but a write is only ever attempted behind
        <code> GPDFORGE_ENABLE_HARDWARE=1</code>, and honestly reported when there's still no
        verified path to reach the hardware. GPD Forge never fakes a successful write.
      </p>
      <LedCard />
      <ChargeLimitRow />
      <UndervoltRow />
    </section>
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
      <TunerCard />
      <Card title="GPU" hint={<Soon />}>
        <p className="muted">iGPU clock cap and UMA/VRAM assignment (for the Agents/AI mode) — via the broker, gated behind hardware approval.</p>
      </Card>
      <AdvancedHardwarePanel />
    </>
  )
}

// --- Display (brightness, refresh rate, night mode: real; tablet mode, keyboard backlight: advisory) ---
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
      <RefreshRateCard />
      <NightModeCard />
      <ScreenAdvisoryCard />
    </>
  )
}

// Refresh-rate switching — REAL (EnumDisplaySettingsEx / ChangeDisplaySettingsEx).
function RefreshRateCard() {
  const toast = useToast()
  const [info, setInfo] = useState<RefreshRateInfo | null>(null)
  useEffect(() => { getRefreshRate().then(setInfo).catch(() => {}) }, [])

  const pick = async (hz: number) => {
    const r = await setRefreshRate(hz).catch(() => null)
    if (!r) return
    setInfo(r)
    toast.push(r.error ? { kind: 'warn', message: r.error } : { kind: 'success', message: `Refresh rate set to ${r.current} Hz` })
  }

  return (
    <Card title="Refresh rate" hint="Live — EnumDisplaySettingsEx / ChangeDisplaySettingsEx">
      {info ? (
        <div className="chips" data-testid="refresh-modes">
          {info.supported.map((hz) => (
            <button key={hz} className={`chip-btn ${info.current === hz ? 'on' : ''}`}
              onClick={() => pick(hz)} data-testid={`refresh-${hz}`}>{hz} Hz</button>
          ))}
        </div>
      ) : <p className="muted">Loading…</p>}
      <p className="muted">Applied for this session only — not written to the registry, so a bad pick never survives a reboot.</p>
    </Card>
  )
}

// Night mode — REAL (GDI gamma ramp). Deliberately NOT Windows Night Light.
function NightModeCard() {
  const [night, setNight] = useState<NightMode>({ on: false, warmth: 0 })
  useEffect(() => { getNightMode().then(setNight).catch(() => {}) }, [])

  const toggle = () => { void setNightMode(!night.on, night.warmth || 50).then(setNight).catch(() => {}) }
  const onWarmth = (v: number) => {
    setNight((s) => ({ ...s, warmth: v }))
    if (night.on) void setNightMode(true, v).then(setNight).catch(() => {})
  }

  return (
    <Card title="Night mode" hint="Gamma ramp — not Windows Night Light">
      <div className="row">
        <Toggle on={night.on} onClick={toggle} label={night.on ? 'On' : 'Off'} testid="night-toggle" />
      </div>
      <Slider label="Warmth" testid="night-warmth" value={night.warmth} min={0} max={100} unit="%" disabled={!night.on} onChange={onWarmth} />
      <p className="muted">Warms the screen by reducing blue in the GDI gamma ramp. Independent of Windows Night Light, which GPD Forge deliberately leaves untouched.</p>
    </Card>
  )
}

// Tablet mode + keyboard backlight — ADVISORY. Tablet mode's WRITE is gated behind
// GPDFORGE_ENABLE_HARDWARE=1; keyboard backlight has no known safe write path at all (EC-owned).
function ScreenAdvisoryCard() {
  const [tablet, setTablet] = useState<TabletModeInfo | null>(null)
  const [kb, setKb] = useState<KeyboardBacklightInfo | null>(null)
  useEffect(() => {
    getTabletMode().then(setTablet).catch(() => {})
    getKeyboardBacklight().then(setKb).catch(() => {})
  }, [])

  return (
    <Card title="Screen" hint={<Soon>advisory</Soon>}>
      <div className="row" data-testid="tablet-row">
        <span>Tablet mode</span>
        <Soon>{tablet?.applied ? 'writable' : 'gated'}</Soon>
      </div>
      <p className="muted" data-testid="tablet-advisory">{tablet?.advisory ?? 'Loading…'}</p>
      <div className="row" data-testid="keyboard-backlight-row">
        <span>Keyboard backlight</span>
        <Soon>EC-only</Soon>
      </div>
      <p className="muted" data-testid="keyboard-backlight-advisory">{kb?.advisory ?? 'Loading…'}</p>
    </Card>
  )
}

// --- Fan ----------------------------------------------------------------------
const FAN_MODES = ['Auto', 'Quiet', 'Balanced', 'Aggressive', 'Manual']
const FAN_GATE_CLOSED_ADVISORY =
  'Curve editor with hysteresis + EC re-init on boot/resume lands with the fan driver (EC access pending PawnIO-stable).'
export function FanPage({ tele }: { tele: Telemetry | null }) {
  const [fan, setFanInfo] = useState<FanInfo>({ mode: 'Auto', manualDuty: 128, controllable: false })
  useEffect(() => { getFanInfo().then(setFanInfo).catch(() => {}) }, [])
  const pick = (f: string) => { setFanInfo((s) => ({ ...s, mode: f })); setFan(f).catch(() => {}) }
  const commitDuty = (v: number) => { setFanManualDuty(v).catch(() => {}) }
  return (
    <>
      <Card title="Fan" hint={fan.controllable ? 'Live — writes the EC.' : 'Preference saved now; curve applied when the fan-control gate is open.'}>
        <div className="stats">
          <Tile label="Fan" value={tele ? `${tele.fanRpm}` : '--'} unit="rpm" />
          <Tile label="CPU" value={tele ? `${Math.round(tele.cpuTempC)}` : '--'} unit="°C" />
          <Tile label="GPU" value={tele ? `${Math.round(tele.gpuTempC)}` : '--'} unit="°C" />
        </div>
        <div className="chips">
          {FAN_MODES.map((f) => (
            <button key={f} className={`chip-btn ${fan.mode === f ? 'on' : ''}`} onClick={() => pick(f)} data-testid={`fan-${f.toLowerCase()}`}>{f}</button>
          ))}
        </div>
        {fan.controllable ? (
          fan.mode === 'Manual' ? (
            <Slider label="Manual duty" testid="fan-manual-duty" value={fan.manualDuty} min={0} max={255} unit=" /255"
              onChange={(v) => setFanInfo((s) => ({ ...s, manualDuty: v }))} onCommit={commitDuty} />
          ) : (
            <p className="muted">Switch to Manual to set a fixed duty; Quiet/Balanced/Aggressive drive a temp curve automatically.</p>
          )
        ) : (
          <p className="muted">{FAN_GATE_CLOSED_ADVISORY}</p>
        )}
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

// System health check / anomaly detection — pure rules on the daemon (core/Health/HealthCheck.cs)
// evaluated against a real telemetry snapshot. Polls slowly; this is diagnostic, not live telemetry.
const HEALTH_LEVEL_LABEL: Record<string, string> = { warn: 'Warning', critical: 'Critical' }
function HealthCard() {
  const [report, setReport] = useState<HealthReport | null>(null)
  useEffect(() => {
    const t = () => getHealthCheck().then(setReport).catch(() => {})
    t(); const id = setInterval(t, 5000); return () => clearInterval(id)
  }, [])

  return (
    <Card title="System health" testid="health-card"
      hint={report && <span className={`badge badge-health-${report.status}`} data-testid="health-status">{report.status}</span>}>
      {!report ? (
        <p className="muted">Loading…</p>
      ) : report.issues.length === 0 ? (
        <p className="health-ok-msg" data-testid="health-ok">✓ All good — no anomalies detected.</p>
      ) : (
        <ul className="rules" data-testid="health-issues">
          {report.issues.map((i) => (
            <li key={i.code} className={`rule health-issue-${i.level}`} data-testid={`health-issue-${i.code}`}>
              <span className="rule-app">{HEALTH_LEVEL_LABEL[i.level] ?? i.level}</span>
              <span className="rule-arrow">→</span>
              <span className="rule-mode">{i.message}</span>
            </li>
          ))}
        </ul>
      )}
      <p className="muted">Checked every 5s from live telemetry — fan state, thermal ceiling, TDP verification, and battery discharge.</p>
    </Card>
  )
}

// Panic cool — a dead-simple, always-available safety action: floor TDP + max fan, right now.
const PANIC_STAPM_W = 8
function PanicCoolButton() {
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
    <Card title="Panic cool" testid="panic-card" hint="Immediate safety floor">
      <p className="muted">Too hot, right now? Drop straight to an {PANIC_STAPM_W} W sustained floor and max out the fan — no waiting, no menus.</p>
      <div className="row-end">
        <button className="btn btn-danger" data-testid="panic-cool" onClick={go} disabled={busy}>
          {busy ? 'Cooling…' : '🧊 Panic cool'}
        </button>
      </div>
    </Card>
  )
}

export function SystemPage({ tele }: { tele: Telemetry | null }) {
  return (
    <>
      <HealthCard />
      <PanicCoolButton />
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

// Update note — only rendered once an update is actually confirmed available.
function UpdateNote() {
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

export function SettingsPage({ auto, setAuto, theme, setTheme, textScale, setTextScale }: {
  auto: boolean; setAuto: (v: boolean) => void; theme: 'dark' | 'light'; setTheme: (t: 'dark' | 'light') => void
  textScale: 'normal' | 'large'; setTextScale: (t: 'normal' | 'large') => void
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
        <div className="row">
          <Toggle on={textScale === 'large'} onClick={() => setTextScale(textScale === 'large' ? 'normal' : 'large')} label="Large text" testid="settings-textscale" />
        </div>
        <p className="muted">Scales up text size across the UI — for readability on the Win 4's small screen.</p>
      </Card>
      <PowerSourceCard />
      <GuardianCard />
      <BackupRestoreCard />
      <Card title="About">
        <p className="muted">GPD Forge — the definitive open-source tuning tool for GPD handhelds. GPL-3.0 · lexlaboratory · github.com/lexlaboratory/gpd-forge</p>
        <UpdateNote />
      </Card>
    </>
  )
}
