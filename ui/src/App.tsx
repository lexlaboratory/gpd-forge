// GPD Forge UI — dashboard shell. GPL-3.0-or-later.
import { useEffect, useRef, useState } from 'react'
import type { Mode, ModeId, Telemetry } from './types'
import { getTelemetry, getMode, setMode as apiSetMode, setTdp as apiSetTdp, HAS_API, type TdpResult } from './api'
import { JobsPanel } from './JobsPanel'
import { StandbyPanel } from './StandbyPanel'

const MODES: Mode[] = [
  { id: 'gaming',  label: 'Gaming',        icon: '🎮', blurb: 'Auto-TDP to target FPS, reactive fan, OSD.' },
  { id: 'ai',      label: 'Agents / AI',   icon: '🤖', blurb: 'Sustained CPU, VRAM/UMA, anti-standby, local API.' },
  { id: 'windows', label: 'Windows',       icon: '🪟', blurb: 'Balanced power, quiet fan, hotkeys.' },
  { id: 'battery', label: 'Battery',       icon: '🔋', blurb: 'Low TDP floor, longest runtime.' },
  { id: 'standby', label: 'Standby Doctor',icon: '🩺', blurb: 'Restore TDP+fan+HID on resume, fix drain.' },
]

function StatTile({ label, value, unit, testid }: { label: string; value: string; unit?: string; testid: string }) {
  return (
    <div className="tile" data-testid={testid}>
      <span className="tile-label">{label}</span>
      <span className="tile-value">
        {value}{unit && <span className="tile-unit">{unit}</span>}
      </span>
    </div>
  )
}

export function App() {
  const [tele, setTele] = useState<Telemetry | null>(null)
  const [active, setActive] = useState<ModeId>('windows')
  const [auto, setAuto] = useState(true)          // automatic: pick the mode from the app in focus
  const [tdp, setTdp] = useState(20)
  const [tdpResult, setTdpResult] = useState<TdpResult | null>(null)
  const [connected, setConnected] = useState(!HAS_API)
  const commitTimer = useRef<ReturnType<typeof setTimeout> | null>(null)

  // Telemetry poll — drives the tiles and the connection state.
  useEffect(() => {
    let alive = true
    const tick = () =>
      getTelemetry()
        .then((t) => { if (alive) { setTele(t); setConnected(true) } })
        .catch(() => { if (alive) setConnected(false) })
    tick()
    const id = setInterval(tick, 1000)
    return () => { alive = false; clearInterval(id) }
  }, [])

  // While Auto is on, the daemon chooses the mode from the foreground app — reflect it live.
  useEffect(() => {
    let alive = true
    getMode().then((m) => alive && setActive(m)).catch(() => {})
    if (!auto) return
    const id = setInterval(() => { getMode().then((m) => alive && setActive(m)).catch(() => {}) }, 2000)
    return () => { alive = false; clearInterval(id) }
  }, [auto])

  const onPickMode = (id: ModeId) => {
    setAuto(false)                 // manual selection overrides automatic
    setActive(id)
    setTdpResult(null)
    apiSetMode(id).catch(() => {})
  }

  const onTdpInput = (value: number) => {
    setTdp(value)
    if (commitTimer.current) clearTimeout(commitTimer.current)
    commitTimer.current = setTimeout(() => {
      apiSetTdp(value).then(setTdpResult).catch(() => {})
    }, 120)
  }

  const verified = tdpResult ? tdpResult.verified : (tele?.tdpVerified ?? true)
  const tdpBadge = verified ? 'verified' : 'unverified'
  const connLabel = HAS_API ? (connected ? 'Live' : 'Offline') : 'Demo'
  const activeMode = MODES.find((m) => m.id === active)

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <img className="brand-logo" src="/logo.png" alt="" aria-hidden width={44} height={44} />
          <div>
            <h1 className="brand-name">GPD Forge</h1>
            <p className="brand-sub" data-testid="device">GPD Win 4 · Ryzen AI 9 HX 370</p>
          </div>
        </div>
        <div className="topbar-right">
          <button
            type="button"
            className={`auto-toggle ${auto ? 'on' : ''}`}
            data-testid="auto-toggle"
            aria-pressed={auto}
            onClick={() => setAuto((v) => !v)}
            title="Automatically pick the best mode for the app in focus"
          >
            <span className="auto-dot" aria-hidden />Auto
          </button>
          <span className={`conn conn-${connLabel.toLowerCase()}`} data-testid="conn">{connLabel}</span>
          <span className={`power-pill ${tele?.acConnected ? 'ac' : 'dc'}`} data-testid="power-source">
            {tele?.acConnected ? 'AC' : `Battery ${tele?.batteryPct ?? '--'}%`}
          </span>
        </div>
      </header>

      <section className="stats" aria-label="Live telemetry">
        <StatTile testid="stat-cpu"  label="CPU"     value={tele ? `${Math.round(tele.cpuTempC)}` : '--'} unit="°C" />
        <StatTile testid="stat-pkg"  label="Power"   value={tele ? `${Math.round(tele.packageW)}` : '--'} unit="W" />
        <StatTile testid="stat-fan"  label="Fan"     value={tele ? `${tele.fanRpm}` : '--'} unit="rpm" />
        <StatTile testid="stat-fps"  label="FPS"     value={tele ? `${Math.round(tele.fps)}` : '--'} />
        <StatTile testid="stat-batt" label="Battery" value={tele ? `${tele.batteryPct}` : '--'} unit="%" />
      </section>

      <section className="modes" aria-label="Usage modes">
        <div className="modes-head">
          <h2 className="section-title">Modes</h2>
          <span className="modes-hint" data-testid="modes-hint">
            {auto ? 'Auto — optimizing for the app in focus' : 'Manual — you chose the mode'}
          </span>
        </div>
        <div className="mode-grid" role="listbox" aria-label="Usage mode">
          {MODES.map((m) => (
            <button
              key={m.id}
              role="option"
              aria-selected={active === m.id}
              data-testid={`mode-${m.id}`}
              className={`mode-card ${active === m.id ? 'active' : ''}`}
              onClick={() => onPickMode(m.id)}
            >
              {auto && active === m.id && <span className="mode-auto" data-testid="mode-auto">AUTO</span>}
              <span className="mode-icon" aria-hidden>{m.icon}</span>
              <span className="mode-label">{m.label}</span>
              <span className="mode-blurb">{m.blurb}</span>
            </button>
          ))}
        </div>
      </section>

      <section className="tdp" aria-label="TDP control">
        <div className="tdp-head">
          <h2 className="section-title">Sustained TDP</h2>
          <span className={`badge badge-${tdpBadge}`} data-testid="tdp-badge">{tdpBadge}</span>
        </div>
        <div className="tdp-row">
          <input
            type="range" min={5} max={35} step={1} value={tdp}
            data-testid="tdp-slider"
            aria-label="Sustained TDP in watts"
            onChange={(e) => onTdpInput(Number(e.target.value))}
          />
          <output className="tdp-value" data-testid="tdp-value">{tdp} W</output>
        </div>
        <p className="tdp-note">
          Applied with a closed loop — GPD Forge re-reads the PM table and warns if the firmware reverts it.
        </p>
      </section>

      {active === 'ai' && <JobsPanel />}
      {active === 'standby' && <StandbyPanel />}

      <footer className="foot">
        <span>GPL-3.0 · lexlaboratory</span>
        <span data-testid="active-mode">{auto ? 'Auto' : 'Manual'}: {activeMode?.label}</span>
      </footer>
    </div>
  )
}
