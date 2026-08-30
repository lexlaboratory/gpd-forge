// GPD Forge — Quick Access Menu (the "Home button" overlay). GPL-3.0-or-later.
//
// A compact, gamepad-first panel meant to live in a borderless always-on-top window
// (or a browser app-window) launched by the user's chosen Home button / hotkey. It reuses
// the app's design system and talks to the same local daemon. Every control is a <button>
// so D-pad focus + A-to-activate works with zero range-input fiddling: a d-pad can reach a
// button but cannot meaningfully drag an input[type=range], which is why TDP and brightness
// are steppers and never sliders.
import { useCallback, useEffect, useRef, useState } from 'react'
import type { ModeId, Telemetry, BatteryBudget } from './types'
import {
  getTelemetry, getMode, setMode, setTdp, getProfiles, getFan, setFan,
  getBrightness, setBrightness, getAutoFps, setAutoFps, getBudget, restoreStandby, getGpu, setFrameCap,
} from './api'
import { Segmented, Stepper } from './components'
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
// Two different things, and the overlay used to show only the first under the second's name.
//   FPS_TARGETS -> auto-FPS: steers TDP to REACH this rate. Does not stop the GPU exceeding it.
//   CAP_OPTIONS -> FRTC: the driver refusing to EXCEED this rate. An actual cap.
// Labelling auto-FPS as "FPS cap" promised a ceiling and delivered a goal — the exact class of
// mislabelled control this project has spent releases removing.
const FPS_TARGETS = [{ label: 'Off', v: 0 }, { label: '30', v: 30 }, { label: '60', v: 60 }, { label: '90', v: 90 }, { label: '120', v: 120 }]
const CAP_TARGETS = [{ label: 'Off', v: 0 }, { label: '30', v: 30 }, { label: '45', v: 45 }, { label: '60', v: 60 }, { label: '90', v: 90 }]

const FAN_OPTIONS = FAN_MODES.map((f) => ({ id: f, label: f, testid: `qam-fan-${f}` }))
const FPS_OPTIONS = FPS_TARGETS.map((t) => ({ id: String(t.v), label: t.label, testid: `qam-fps-${t.v}` }))
const CAP_OPTIONS = CAP_TARGETS.map((t) => ({ id: String(t.v), label: t.label, testid: `qam-cap-${t.v}` }))

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
  // Null while unknown. The cap row stays hidden until the daemon says the GPU can do it — a control
  // that cannot work is worse than an absent one.
  const [frameCap, setFrameCapS] = useState<number | null>(null)
  const [capSupported, setCapSupported] = useState(false)
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
    // The cap row only appears when the driver actually offers one. Hidden rather than disabled: on a
    // gamepad-first overlay an unusable row is one more thing to skip past with the D-pad.
    getGpu().then((g) => {
      if (!alive) return
      const frtc = g.available ? g.settings?.frameRateCap : null
      setCapSupported(Boolean(frtc?.supported))
      setFrameCapS(frtc?.enabled ? frtc.value : null)
    }).catch(() => {})
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
  const applyTdp = async (next: number) => {
    setTdp_(next)
    try { const r = await setTdp(next); setTdp_(r.observed); setVerified(r.verified) } catch { /* ignore */ }
  }
  const pickFan = async (f: string) => { setFanS(f); try { await setFan(f) } catch { /* ignore */ } }
  const pickFps = async (v: number) => { setFpsTarget(v); try { await setAutoFps(v || 60, v > 0) } catch { /* ignore */ } }
  const pickCap = async (v: number) => {
    const previous = frameCap
    setFrameCapS(v || null)
    try {
      await setFrameCap(v || null)
    } catch {
      // Put the control back where it was. The daemon refuses a cap below an active auto-FPS target
      // — leaving the switch showing a value that was rejected is how a UI starts lying.
      setFrameCapS(previous)
    }
  }
  const applyBright = async (next: number) => {
    setBright(next)
    try { const b = await setBrightness(next); setBright(b) } catch { /* ignore */ }
  }
  const doRestore = async () => { try { await restoreStandby(); toast.push({ kind: 'success', message: 'Standby state restored' }) } catch { /* ignore */ } }
  const openFull = useCallback(() => { window.location.assign('/') }, [])

  return (
    <div className="qam" ref={rootRef} data-testid="qam">
      <header className="qam-head">
        <div className="qam-brand">
          <span className="qam-logo" aria-hidden>⚡</span>
          <span>GPD Forge</span>
          <span className={`qam-dot ${tele ? 'on' : ''}`} title="live" />
        </div>
        {/* The live triple is the first thing a player looks at, so it gets the largest type in the
            panel and its own bracketed frame. */}
        <div className="qam-live">
          <div className="qam-stat">
            <span className="qam-stat-v">{tele ? Math.round(tele.cpuTempC) : '--'}<i>°C</i></span>
            <span className="qam-stat-k">CPU</span>
          </div>
          <div className="qam-stat">
            <span className="qam-stat-v">{tele ? Math.round(tele.packageW) : '--'}<i>W</i></span>
            <span className="qam-stat-k">Pkg</span>
          </div>
          <div className="qam-stat">
            <span className="qam-stat-v">{tele ? Math.round(tele.fps) : '--'}<i>fps</i></span>
            <span className="qam-stat-k">Frame</span>
          </div>
        </div>
      </header>

      <div className="qam-modes" role="group" aria-label="Mode">
        {QMODES.map((m) => (
          <button key={m.id} className={`qam-mode ${mode === m.id ? 'on' : ''}`} data-testid={`qam-mode-${m.id}`}
            onClick={() => pickMode(m.id)} title={m.label} aria-pressed={mode === m.id}>
            <span className="qam-mode-i" aria-hidden>{m.icon}</span>
            <span className="qam-mode-k">{m.label}</span>
          </button>
        ))}
      </div>

      <div className="qam-line">
        <span className="qam-label">TDP {verified && <em className="qam-ok" data-testid="qam-verified">verified</em>}</span>
        <Stepper
          label="TDP" value={tdp} unit="W" min={5} max={40} onChange={applyTdp}
          testid="qam-tdp" decTestid="qam-tdp-dec" incTestid="qam-tdp-inc"
        />
      </div>

      <div className="qam-line stack">
        <span className="qam-label">Fan</span>
        <Segmented flavour="qam" label="Fan" options={FAN_OPTIONS} value={fan} onChange={pickFan} />
      </div>

      <div className="qam-line stack">
        <span className="qam-label">FPS target</span>
        <Segmented flavour="qam" label="FPS target" options={FPS_OPTIONS} value={String(fpsTarget)}
          onChange={(id) => pickFps(Number(id))} />
      </div>

      {capSupported && (
        <div className="qam-line stack">
          <span className="qam-label">FPS cap</span>
          <Segmented flavour="qam" label="FPS cap" options={CAP_OPTIONS} value={String(frameCap ?? 0)}
            onChange={(id) => pickCap(Number(id))} />
        </div>
      )}

      <div className="qam-line">
        <span className="qam-label">Brightness</span>
        <Stepper
          label="Brightness" value={bright} unit="%" min={0} max={100} step={10} onChange={applyBright}
          testid="qam-bright" decTestid="qam-bright-dec" incTestid="qam-bright-inc"
        />
      </div>

      <div className="qam-batt" data-testid="qam-budget">
        <span className="qam-label">Battery</span>
        <span className="qam-batt-v">{fmtBudget(budget)}</span>
      </div>

      <footer className="qam-foot">
        <button className="qam-action" data-testid="qam-restore" onClick={doRestore}>🩺 Restore standby</button>
        <button className="qam-action" data-testid="qam-full" onClick={openFull}>⧉ Full UI</button>
        <button className="qam-action qam-close" data-testid="qam-close" onClick={closeOverlay}>✕ Close</button>
      </footer>
    </div>
  )
}
