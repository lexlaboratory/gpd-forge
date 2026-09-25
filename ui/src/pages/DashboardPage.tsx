// GPD Forge UI — Dashboard page (telemetry, modes, TDP, AI card, auto-tuner). GPL-3.0-or-later.
import { useEffect, useRef, useState } from 'react'
import type { AiInfo, InferenceHold, ModeId, TuneGoal, TunerInfo } from '../types'
import {
  setTdp as apiSetTdp, getTdp, getProfiles, getMode, getAi, setAntiStandby, getTuner, startTuner, type TdpResult,
} from '../api'
import { Badge, Button, Frame, Icon, Readout, Segmented, Slider, Toggle, type Tone } from '../components'
import { useToast } from '../Toast'
import { JobsPanel } from '../JobsPanel'
import { StandbyPanel } from '../StandbyPanel'
import { MODES, reading, fractionOf, tdpInForce, tdpVerifiedNow, TDP_SEED_RETRY_MS, type Shared, type TdpWrite } from './shared'
import { BatteryBudgetCard } from './SystemPage'

// Ceilings the fill bars are read against. The TDP one is the slider's own maximum, so the bar and
// the control can never disagree about what "full" means. 40 W is the daemon's manual band
// (TdpIntent.ManualMaxW) and the overlay stepper's maximum. It was 35 until audit round 3
// (2026-09-24): a 36–40 W override in force was seeded through a clamp and shown as "35 W" — a value
// not in force, disagreeing with the overlay, and the one a refused write then rolled back to.
const MAX_TDP_W = 40
const MAX_CPU_C = 100

// Undefined tone for an absent reading: a tile with no data must not be coloured as if it were
// healthy. 'ok' green on a sensor nobody read is the same lie as printing 0.
const tempTone = (c: number | null): Tone | undefined =>
  c == null ? undefined : c > 85 ? 'danger' : c > 75 ? 'warn' : 'ok'
const battTone = (p: number | null): Tone | undefined =>
  p == null ? undefined : p < 15 ? 'danger' : p < 30 ? 'warn' : 'ok'

// --- Dashboard -----------------------------------------------------------------

// The badge's three states. Null — nothing written or verified yet — is its own grey state: it was
// defaulted to 'verified', which claimed a confirmation nobody had given.
const tdpBadge = (v: boolean | null): { tone: Tone; label: string } =>
  v === true ? { tone: 'ok', label: 'verified' } : v === false ? { tone: 'warn', label: 'unverified' } : { tone: 'muted', label: 'unknown' }

export function DashboardPage({ tele, active, auto, pickMode }: Shared) {
  const toast = useToast()
  // Null until something says what is in force — never a placeholder number. It was a hardcoded 20
  // that looked like the value: first a remembered 12 W override opened as 20 W (2026-09-24), then,
  // with that fixed, a GET /tdp with nothing written yet (the startup apply yielded to a rival) or a
  // failed request still left 20 on screen while the overlay showed the mode preset (audit round 2).
  const [tdp, setTdp] = useState<number | null>(null)
  // This window's last write: the badge shows it only until a newer telemetry sample (tdpVerifiedNow).
  const [tdpWrite, setTdpWrite] = useState<TdpWrite | null>(null)
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)
  // Set once the user moves the control, so a GET /tdp that answers late cannot yank it back.
  const touched = useRef(false)
  // The last value the daemon accepted: where the control returns to when a write is refused.
  const applied = useRef<number | null>(null)
  // Bumped to read the TDP in force again: after a mode change, and while a seed found nothing.
  const [seedRound, setSeedRound] = useState(0)
  const [seedFailed, setSeedFailed] = useState(false)

  // Seeded exactly as the overlay seeds its stepper, so the two controls cannot disagree: the manual
  // override, else the last write, else the ACTIVE mode's preset. The mode is asked for here rather
  // than taken from Shared.active, which reads 'windows' until the shell's own GET /mode answers.
  //
  // Not once per mount (audit round 3, 2026-09-24). A seed that found nothing — the daemon briefly
  // unreachable at mount — left the control disabled at '--' for the life of the page with nothing
  // saying why; it now says so and asks again every TDP_SEED_RETRY_MS. And a mode change ends a manual
  // override in the daemon, but the slider kept showing it: it is re-read once the mode is applied.
  // The value is shown as the daemon states it, never through the slider's clamp.
  useEffect(() => {
    let alive = true
    let retry: ReturnType<typeof setTimeout> | null = null
    Promise.allSettled([getTdp(), getProfiles(), getMode()]).then(([t, p, m]) => {
      if (!alive) return
      const preset = p.status === 'fulfilled' && m.status === 'fulfilled' ? p.value[m.value]?.stapmW : undefined
      const w = tdpInForce(t.status === 'fulfilled' ? t.value : null) ?? preset
      if (w == null) {
        setSeedFailed(true)
        retry = setTimeout(() => setSeedRound((r) => r + 1), TDP_SEED_RETRY_MS)
        return
      }
      setSeedFailed(false)
      applied.current = Math.round(w)
      if (!touched.current) setTdp(Math.round(w))
    })
    return () => { alive = false; if (retry) clearTimeout(retry) }
  }, [seedRound])
  useEffect(() => () => { if (timer.current) clearTimeout(timer.current) }, [])

  const onPickMode = (id: ModeId) => {
    pickMode(id).then((ok) => {
      if (!ok) return
      // The mode's preset is in force now, and this window's write result describes the old value.
      touched.current = false
      setTdpWrite(null)
      setSeedRound((r) => r + 1)
    })
  }

  const onTdp = (v: number) => {
    touched.current = true
    setTdp(v)
    if (timer.current) clearTimeout(timer.current)
    timer.current = setTimeout(() => {
      apiSetTdp(v)
        .then((r: TdpResult) => { applied.current = v; setTdpWrite({ verified: r.verified, atMs: Date.now() }) })
        .catch((e: unknown) => {
          // Said, not swallowed: a 400 bad_tdp or a daemon that went away left the slider showing a
          // value that was never applied, with nothing on screen saying so.
          toast.push({ kind: 'error', message: `TDP ${v} W was not applied — ${e instanceof Error ? e.message : String(e)}` })
          if (applied.current != null) setTdp(applied.current)
        })
    }, 120)
  }
  const badge = tdpBadge(tdpVerifiedNow(tele, tdpWrite))

  return (
    <>
      <section className="stats" aria-label="Live telemetry">
        {/* Fan rpm and FPS get no bar: neither has a ceiling this app can state honestly. */}
        <Readout testid="stat-cpu"  label="CPU"     value={reading(tele?.cpuTempC)} unit="°C"
          fraction={fractionOf(tele?.cpuTempC, MAX_CPU_C)} tone={tempTone(tele?.cpuTempC ?? null)} />
        <Readout testid="stat-pkg"  label="Power"   value={reading(tele?.packageW)} unit="W"
          fraction={fractionOf(tele?.packageW, MAX_TDP_W)} tone={tele?.packageW == null ? undefined : 'info'} />
        <Readout testid="stat-fan"  label="Fan"     value={reading(tele?.fanRpm)} unit="rpm" />
        <Readout testid="stat-fps"  label="FPS"     value={reading(tele?.fps)} />
        <Readout testid="stat-batt" label="Battery" value={reading(tele?.batteryPct)} unit="%"
          fraction={fractionOf(tele?.batteryPct, 100)} tone={battTone(tele?.batteryPct ?? null)} />
      </section>

      <Frame title="Modes" hint={<span data-testid="modes-hint">{auto ? 'Auto — optimizing for the app in focus' : 'Manual — you chose the mode'}</span>}>
        <div className="mode-grid" role="listbox" aria-label="Usage mode">
          {MODES.map((m) => (
            <button key={m.id} role="option" aria-selected={active === m.id} data-testid={`mode-${m.id}`}
              className={`mode-card ${active === m.id ? 'active' : ''}`} onClick={() => onPickMode(m.id)}>
              {auto && active === m.id && <span className="mode-auto" data-testid="mode-auto">AUTO</span>}
              <span className="mode-icon"><Icon name={m.id} size={22} /></span>
              <span className="mode-label">{m.label}</span>
              <span className="mode-blurb">{m.blurb}</span>
            </button>
          ))}
        </div>
      </Frame>

      <Frame title="Sustained TDP" hint={<Badge tone={badge.tone} testid="tdp-badge">{badge.label}</Badge>}>
        <div className="tdp-row">
          {/* Unknown is disabled, not parked on a number: a range input always shows SOME position,
              and any position would read as the value in force. */}
          <input type="range" min={5} max={MAX_TDP_W} step={1} value={tdp ?? 5} disabled={tdp == null}
            data-testid="tdp-slider" aria-label="Sustained TDP in watts" aria-valuetext={tdp == null ? 'unknown' : `${tdp} W`}
            onChange={(e) => onTdp(Number(e.target.value))} />
          {/* ±1 W buttons beside the slider: a d-pad can press a button but cannot drag a range. */}
          <div className="stepper">
            <button type="button" className="stepper-btn" aria-label="Sustained TDP down" data-testid="tdp-dec"
              disabled={tdp == null || tdp <= 5} onClick={() => tdp != null && onTdp(Math.max(5, tdp - 1))}>&minus;</button>
            <output className="tdp-value" data-testid="tdp-value">{tdp == null ? '--' : `${tdp} W`}</output>
            <button type="button" className="stepper-btn" aria-label="Sustained TDP up" data-testid="tdp-inc"
              disabled={tdp == null || tdp >= MAX_TDP_W} onClick={() => tdp != null && onTdp(Math.min(MAX_TDP_W, tdp + 1))}>+</button>
          </div>
        </div>
        {tdp == null && seedFailed && (
          <p className="muted" data-testid="tdp-unavailable" role="status">
            Could not read the TDP in force — retrying.
          </p>
        )}
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
