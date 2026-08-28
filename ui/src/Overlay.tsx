// GPD Forge — Quick Access Menu (the "Home button" overlay). GPL-3.0-or-later.
//
// A compact, gamepad-first panel meant to live in a borderless always-on-top window
// (or a browser app-window) launched by the user's chosen Home button / hotkey. It reuses
// the app's design system and talks to the same local daemon. Every control is a <button>
// so D-pad focus + A-to-activate works with zero range-input fiddling.
import { useCallback, useEffect, useRef, useState } from 'react'
import type { ModeId, Telemetry, BatteryBudget } from './types'
import {
  getTelemetry, getMode, setMode, setTdp, getProfiles, getFan, setFan,
  getBrightness, setBrightness, getAutoFps, setAutoFps, getBudget, restoreStandby,
} from './api'
import { useToast } from './Toast'
import { useDensity } from './hooks/useDensity'
import { useSpatialNav } from './hooks/useSpatialNav'

const QMODES: { id: ModeId; icon: string; label: string }[] = [
  { id: 'gaming', icon: '🎮', label: 'Gaming' },
  { id: 'ai', icon: '🤖', label: 'AI' },
  { id: 'windows', icon: '🪟', label: 'Windows' },
  { id: 'battery', icon: '🔋', label: 'Battery' },
  { id: 'standby', icon: '🩺', label: 'Standby' },
]
const FAN_MODES = ['Auto', 'Quiet', 'Balanced', 'Aggressive']
const FPS_TARGETS = [{ label: 'Off', v: 0 }, { label: '30', v: 30 }, { label: '60', v: 60 }, { label: '90', v: 90 }, { label: '120', v: 120 }]
const clamp = (v: number, lo: number, hi: number) => Math.min(Math.max(v, lo), hi)

/** Close the overlay: hide the native window if we're in Tauri, else close the browser app-window. */
function closeOverlay() {
  const w = window as unknown as { __TAURI__?: { window?: { getCurrent: () => { hide: () => void } } } }
  try { if (w.__TAURI__?.window) { w.__TAURI__.window.getCurrent().hide(); return } } catch { /* fall through */ }
  window.close()
}

function fmtBudget(b: BatteryBudget | null): string {
  if (!b) return '—'
  if (b.minutesRemaining == null) return `On AC · ${b.remainingWh.toFixed(0)} Wh`
  const h = Math.floor(b.minutesRemaining / 60), m = b.minutesRemaining % 60
  return `~${h}h ${String(m).padStart(2, '0')}m @ ${b.dischargeW.toFixed(0)} W`
}

export function OverlayApp() {
  const toast = useToast()
  const rootRef = useRef<HTMLDivElement | null>(null)
  // The overlay is the surface most likely to be driven by a thumb or a pad, so it wants the same
  // density detection as the main window rather than a hardcoded size.
  useDensity()
  const [tele, setTele] = useState<Telemetry | null>(null)
  const [mode, setModeS] = useState<ModeId>('windows')
  const [presets, setPresets] = useState<Record<string, { stapmW: number }>>({})
  const [tdp, setTdp_] = useState(20)
  const [verified, setVerified] = useState(true)
  const [fan, setFanS] = useState('Auto')
  const [fpsTarget, setFpsTarget] = useState(0)
  const [bright, setBright] = useState(70)
  const [budget, setBudget] = useState<BatteryBudget | null>(null)

  useEffect(() => {
    let alive = true
    const tick = () => getTelemetry().then((t) => alive && setTele(t)).catch(() => {})
    tick(); const id = setInterval(tick, 1000)
    getMode().then((m) => alive && setModeS(m)).catch(() => {})
    getProfiles().then((p) => { if (alive) { setPresets(p); if (p[mode]) setTdp_(p[mode].stapmW) } }).catch(() => {})
    getFan().then((f) => alive && setFanS(f)).catch(() => {})
    getBrightness().then((b) => alive && b != null && setBright(b)).catch(() => {})
    getAutoFps().then((a) => alive && setFpsTarget(a.enabled ? a.targetFps : 0)).catch(() => {})
    const bt = () => getBudget().then((b) => alive && setBudget(b)).catch(() => {})
    bt(); const bid = setInterval(bt, 5000)
    return () => { alive = false; clearInterval(id); clearInterval(bid) }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Same 2-D walk as the main window. The old linear version made Left and Down do the same thing,
  // which on a grid of five mode squares is close to unusable.
  useSpatialNav(rootRef, { onCancel: closeOverlay })

  const pickMode = async (m: ModeId) => {
    setModeS(m)
    try { await setMode(m) } catch { /* ignore */ }
    if (presets[m]) setTdp_(presets[m].stapmW)
    toast.push({ kind: 'info', message: `Mode: ${QMODES.find((x) => x.id === m)?.label ?? m}` })
  }
  const nudgeTdp = async (d: number) => {
    const next = clamp(tdp + d, 5, 40)
    setTdp_(next)
    try { const r = await setTdp(next); setTdp_(r.observed); setVerified(r.verified) } catch { /* ignore */ }
  }
  const pickFan = async (f: string) => { setFanS(f); try { await setFan(f) } catch { /* ignore */ } }
  const pickFps = async (v: number) => { setFpsTarget(v); try { await setAutoFps(v || 60, v > 0) } catch { /* ignore */ } }
  const nudgeBright = async (d: number) => {
    const next = clamp(bright + d, 0, 100); setBright(next)
    try { const b = await setBrightness(next); setBright(b) } catch { /* ignore */ }
  }
  const doRestore = async () => { try { await restoreStandby(); toast.push({ kind: 'success', message: 'Standby state restored' }) } catch { /* ignore */ } }
  const openFull = useCallback(() => { window.location.assign('/') }, [])

  return (
    <div className="qam" ref={rootRef} data-testid="qam">
      <header className="qam-head">
        <div className="qam-brand"><span className="qam-logo" aria-hidden>⚡</span> GPD Forge</div>
        <div className="qam-live">
          <span className="qam-stat">{tele ? Math.round(tele.cpuTempC) : '--'}<i>°C</i></span>
          <span className="qam-stat">{tele ? Math.round(tele.packageW) : '--'}<i>W</i></span>
          <span className="qam-stat">{tele ? Math.round(tele.fps) : '--'}<i>fps</i></span>
          <span className={`qam-dot ${tele ? 'on' : ''}`} title="live" />
        </div>
      </header>

      <div className="qam-modes" role="group" aria-label="Mode">
        {QMODES.map((m) => (
          <button key={m.id} className={`qam-mode ${mode === m.id ? 'on' : ''}`} data-testid={`qam-mode-${m.id}`}
            onClick={() => pickMode(m.id)} title={m.label} aria-pressed={mode === m.id}>
            <span aria-hidden>{m.icon}</span>
          </button>
        ))}
      </div>

      <div className="qam-line">
        <span className="qam-label">TDP {verified && <em className="qam-ok" data-testid="qam-verified">verified</em>}</span>
        <div className="qam-stepper">
          <button className="qam-step" data-testid="qam-tdp-dec" onClick={() => nudgeTdp(-1)} aria-label="TDP down">−</button>
          <span className="qam-val" data-testid="qam-tdp">{tdp}<i>W</i></span>
          <button className="qam-step" data-testid="qam-tdp-inc" onClick={() => nudgeTdp(1)} aria-label="TDP up">+</button>
        </div>
      </div>

      <div className="qam-line stack">
        <span className="qam-label">Fan</span>
        <div className="qam-chips">
          {FAN_MODES.map((f) => (
            <button key={f} className={`qam-chip ${fan === f ? 'on' : ''}`} data-testid={`qam-fan-${f}`} onClick={() => pickFan(f)}>{f}</button>
          ))}
        </div>
      </div>

      <div className="qam-line stack">
        <span className="qam-label">FPS cap</span>
        <div className="qam-chips">
          {FPS_TARGETS.map((t) => (
            <button key={t.v} className={`qam-chip ${fpsTarget === t.v ? 'on' : ''}`} data-testid={`qam-fps-${t.v}`} onClick={() => pickFps(t.v)}>{t.label}</button>
          ))}
        </div>
      </div>

      <div className="qam-line">
        <span className="qam-label">Brightness</span>
        <div className="qam-stepper">
          <button className="qam-step" data-testid="qam-bright-dec" onClick={() => nudgeBright(-10)} aria-label="Brightness down">−</button>
          <span className="qam-val" data-testid="qam-bright">{bright}<i>%</i></span>
          <button className="qam-step" data-testid="qam-bright-inc" onClick={() => nudgeBright(10)} aria-label="Brightness up">+</button>
        </div>
      </div>

      <div className="qam-batt" data-testid="qam-budget">🔋 {fmtBudget(budget)}</div>

      <footer className="qam-foot">
        <button className="qam-action" data-testid="qam-restore" onClick={doRestore}>🩺 Restore standby</button>
        <button className="qam-action" data-testid="qam-full" onClick={openFull}>⧉ Full UI</button>
        <button className="qam-action qam-close" data-testid="qam-close" onClick={closeOverlay}>✕ Close</button>
      </footer>
    </div>
  )
}
