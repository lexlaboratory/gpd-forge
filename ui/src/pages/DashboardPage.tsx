// GPD Forge UI — Dashboard page (telemetry, modes, TDP, AI card, auto-tuner). GPL-3.0-or-later.
import { useEffect, useRef, useState } from 'react'
import type { AiInfo, InferenceHold, TuneGoal, TunerInfo } from '../types'
import {
  setTdp as apiSetTdp, getAi, setAntiStandby, getTuner, startTuner, type TdpResult,
} from '../api'
import { Badge, Button, Frame, Readout, Segmented, Slider, Toggle, type Tone } from '../components'
import { useToast } from '../Toast'
import { JobsPanel } from '../JobsPanel'
import { StandbyPanel } from '../StandbyPanel'
import { MODES, type Shared } from './shared'
import { BatteryBudgetCard } from './SystemPage'

// Ceilings the fill bars are read against. The TDP one is the slider's own maximum, so the bar and
// the control can never disagree about what "full" means.
const MAX_TDP_W = 35
const MAX_CPU_C = 100

const tempTone = (c: number): Tone => (c > 85 ? 'danger' : c > 75 ? 'warn' : 'ok')
const battTone = (p: number): Tone => (p < 15 ? 'danger' : p < 30 ? 'warn' : 'ok')

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
        {/* Fan rpm and FPS get no bar: neither has a ceiling this app can state honestly. */}
        <Readout testid="stat-cpu"  label="CPU"     value={tele ? `${Math.round(tele.cpuTempC)}` : '--'} unit="°C"
          fraction={tele ? tele.cpuTempC / MAX_CPU_C : undefined} tone={tele ? tempTone(tele.cpuTempC) : undefined} />
        <Readout testid="stat-pkg"  label="Power"   value={tele ? `${Math.round(tele.packageW)}` : '--'} unit="W"
          fraction={tele ? tele.packageW / MAX_TDP_W : undefined} tone="info" />
        <Readout testid="stat-fan"  label="Fan"     value={tele ? `${tele.fanRpm}` : '--'} unit="rpm" />
        <Readout testid="stat-fps"  label="FPS"     value={tele ? `${Math.round(tele.fps)}` : '--'} />
        <Readout testid="stat-batt" label="Battery" value={tele ? `${tele.batteryPct}` : '--'} unit="%"
          fraction={tele ? tele.batteryPct / 100 : undefined} tone={tele ? battTone(tele.batteryPct) : undefined} />
      </section>

      <Frame title="Modes" hint={<span data-testid="modes-hint">{auto ? 'Auto — optimizing for the app in focus' : 'Manual — you chose the mode'}</span>}>
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
      </Frame>

      <Frame title="Sustained TDP" hint={<Badge tone={verified ? 'ok' : 'warn'} testid="tdp-badge">{verified ? 'verified' : 'unverified'}</Badge>}>
        <div className="tdp-row">
          <input type="range" min={5} max={MAX_TDP_W} step={1} value={tdp} data-testid="tdp-slider" aria-label="Sustained TDP in watts" onChange={(e) => onTdp(Number(e.target.value))} />
          <output className="tdp-value" data-testid="tdp-value">{tdp} W</output>
        </div>
        <p className="muted">Applied with a closed loop — GPD Forge re-reads the PM table and warns if the firmware reverts it.</p>
      </Frame>

      {active === 'ai' && <JobsPanel />}
      {active === 'ai' && <AiCard />}
      {active === 'standby' && <StandbyPanel />}
      <BatteryBudgetCard />
    </>
  )
}

// --- Agents / AI (anti-standby + sustained profile + VRAM/UMA advisory) ------
export function AiCard() {
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
    <Frame title="Anti-standby & sustained power" hint="Keeps Windows awake while an AI job runs">
      <div className="row">
        <Toggle on={a.manual} onClick={toggle} label={a.manual ? 'Manual hold on' : 'Manual hold off'} testid="ai-antistandby-toggle" />
      </div>
      <p className="muted" data-testid="ai-antistandby-status">
        {a.active
          ? `Holding Windows awake — ${a.holders} active hold${a.holders === 1 ? '' : 's'}.`
          : 'Not holding — Windows may enter Modern Standby normally.'}
      </p>
      <div className="stats">
        <Readout testid="ai-sustained-stapm" label="Sustained" value={`${p.stapmW}`} unit=" W"
          fraction={p.stapmW / MAX_TDP_W} tone="info" />
        <Readout label="Thermal limit" value={`${p.tctlC}`} unit="°C" fraction={p.tctlC / MAX_CPU_C} tone={tempTone(p.tctlC)} />
        <Readout testid="ai-vram" label="iGPU VRAM/UMA" value={vram.available ? `${vram.reportedMb}` : '--'} unit={vram.available ? ' MB' : ''} />
      </div>
      <p className="muted" data-testid="ai-vram-advisory">{vram.advisory}</p>
      {vram.history && (
        <p className="muted" data-testid="ai-vram-history">{vram.history.summary}</p>
      )}
      <InferenceHoldReadout hold={info.inferenceHold} />
    </Frame>
  )
}

// Attribution for the keep-awake we take on behalf of inference GPD Forge did not start. A machine
// that will not sleep and will not say why is the complaint this feature otherwise creates, so the
// holding process and its start time are shown, not just a boolean.
//
// `cpuFraction: null` renders as "—", never as 0%: null means the last tick produced no usable
// measurement (new PID, recycled PID, stepped clock, or CPU time we were refused), and showing that
// as 0% would read as "idle" when the truth is "unknown".
export function InferenceHoldReadout({ hold }: { hold: InferenceHold | undefined }) {
  if (!hold) return null
  const pct = (f: number | null) => (f === null || f === undefined ? '—' : `${Math.round(f * 100)}%`)
  return (
    <div data-testid="ai-inference-hold">
      <p className="muted">
        {hold.holding
          ? `Held awake for inference since ${new Date(hold.holdingSince!).toLocaleTimeString()}.`
          : hold.enforcing
            ? 'No inference work detected — Windows may sleep normally.'
            : 'Observing only. Detected inference work is reported here but does not hold the machine awake (set GPDFORGE_INFERENCE_HOLD=1 to enforce).'}
      </p>
      {hold.unmeasured && hold.unmeasured.length > 0 && (
        <p className="muted" data-testid="ai-inference-unmeasured">
          Could not read {hold.unmeasured.map((u) => `${u.name} (${u.why})`).join(', ')} — this is not the
          same as "not working", and no hold is taken on a guess.
        </p>
      )}
      {hold.workers.length > 0 && (
        <ul className="muted" data-testid="ai-inference-workers">
          {hold.workers.map((w) => (
            <li key={w.pid}>
              {w.name} (pid {w.pid}) — {pct(w.cpuFraction)} of total CPU, busy since{' '}
              {new Date(w.busySince).toLocaleTimeString()}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

// --- Auto-tuner (TDP sweep) -----------------------------------------------------
export const TUNE_GOALS: { id: TuneGoal; label: string }[] = [
  { id: 'MaxFps', label: 'Max FPS' },
  { id: 'BestEfficiency', label: 'Best efficiency' },
  { id: 'HoldTarget', label: 'Hold target FPS' },
]

export function TunerCard() {
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
    <Frame title="Auto-tuner" hint="Sweeps TDP and picks the best point for your goal">
      <Segmented
        label="Tuner goal"
        testid="tuner-goals"
        value={goal}
        onChange={setGoal}
        options={TUNE_GOALS.map((g) => ({ id: g.id, label: g.label, testid: `tuner-goal-${g.id}` }))}
      />
      {goal === 'HoldTarget' && (
        <Slider label="Target FPS" testid="tuner-target" value={targetFps} min={30} max={144} unit=" fps" onChange={(v) => setTargetFps(v)} />
      )}
      <div className="row-end">
        <Button variant="accent" testid="tuner-start" onClick={start} disabled={busy}>{busy ? 'Sweeping…' : 'Start sweep'}</Button>
      </div>
      <p className="muted" data-testid="tuner-status">{status}</p>
      {info?.best && (
        <div className="stats" data-testid="tuner-best">
          <Readout label="Best STAPM" value={`${info.best.stapmW}`} unit=" W" fraction={info.best.stapmW / MAX_TDP_W} tone="info" />
          <Readout label="FPS" value={`${info.best.fps}`} />
          <Readout label="Temp" value={`${info.best.tempC}`} unit="°C" fraction={info.best.tempC / MAX_CPU_C} tone={tempTone(info.best.tempC)} />
        </div>
      )}
      <p className="muted">Honesty note: this HX370 has no FPS telemetry yet (PresentMon isn't wired), so a real sweep records nothing useful and honestly reports no result rather than a faked one. The mock daemon simulates FPS so this card is fully exercisable in dev/E2E.</p>
    </Frame>
  )
}
