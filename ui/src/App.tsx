// GPD Forge UI — app shell (multi-page). GPL-3.0-or-later.
import { useEffect, useState } from 'react'
import type { ModeId, Telemetry } from './types'
import { getTelemetry, getMode, setMode as apiSetMode } from './api'
import { Toggle } from './ui'
import {
  MODES, DashboardPage, PowerPage, FanPage, ControllerPage, DisplayPage,
  ProfilesPage, MonitorPage, SystemPage, SettingsPage, type Shared,
} from './pages'

const NAV = [
  { id: 'dashboard',  label: 'Dashboard',  icon: '📊' },
  { id: 'power',      label: 'Power',      icon: '⚡' },
  { id: 'fan',        label: 'Fan',        icon: '🌀' },
  { id: 'controller', label: 'Controller', icon: '🎮' },
  { id: 'display',    label: 'Display',    icon: '🔆' },
  { id: 'profiles',   label: 'Profiles',   icon: '🗂️' },
  { id: 'monitor',    label: 'Monitor',    icon: '📈' },
  { id: 'system',     label: 'System',     icon: '🩺' },
  { id: 'settings',   label: 'Settings',   icon: '⚙️' },
] as const
type PageId = typeof NAV[number]['id']

export function App() {
  const [page, setPage] = useState<PageId>('dashboard')
  const [tele, setTele] = useState<Telemetry | null>(null)
  const [active, setActive] = useState<ModeId>('windows')
  const [auto, setAuto] = useState(true)
  const [connected, setConnected] = useState(false)

  useEffect(() => {
    let alive = true
    const tick = () =>
      getTelemetry().then((t) => { if (alive) { setTele(t); setConnected(true) } }).catch(() => { if (alive) setConnected(false) })
    tick()
    const id = setInterval(tick, 1000)
    return () => { alive = false; clearInterval(id) }
  }, [])

  useEffect(() => {
    let alive = true
    getMode().then((m) => alive && setActive(m)).catch(() => {})
    if (!auto) return
    const id = setInterval(() => getMode().then((m) => alive && setActive(m)).catch(() => {}), 2000)
    return () => { alive = false; clearInterval(id) }
  }, [auto])

  const pickMode = (id: ModeId) => { setAuto(false); setActive(id); apiSetMode(id).catch(() => {}) }
  const shared: Shared = { tele, active, auto, setAuto, pickMode }
  const connLabel = connected ? 'Live' : 'Offline'
  const activeMode = MODES.find((m) => m.id === active)

  return (
    <div className="shell">
      <aside className="nav">
        <div className="nav-brand">
          <img className="brand-logo" src="/logo.png" alt="" aria-hidden width={34} height={34} />
          <span className="nav-name">GPD Forge</span>
        </div>
        <nav className="nav-list">
          {NAV.map((n) => (
            <button key={n.id} className={`nav-item ${page === n.id ? 'active' : ''}`} data-testid={`nav-${n.id}`} onClick={() => setPage(n.id)}>
              <span className="nav-icon" aria-hidden>{n.icon}</span><span className="nav-label">{n.label}</span>
            </button>
          ))}
        </nav>
        <div className="nav-foot" data-testid="active-mode">{auto ? 'Auto' : 'Manual'}: {activeMode?.label}</div>
      </aside>

      <main className="main">
        <header className="topbar">
          <div>
            <h1 className="page-title">{NAV.find((n) => n.id === page)?.label}</h1>
            <p className="page-sub" data-testid="device">GPD Win 4 · Ryzen AI 9 HX 370</p>
          </div>
          <div className="topbar-right">
            <Toggle on={auto} onClick={() => setAuto(!auto)} label="Auto" testid="auto-toggle" />
            <span className={`conn conn-${connLabel.toLowerCase()}`} data-testid="conn">{connLabel}</span>
            <span className={`power-pill ${tele?.acConnected ? 'ac' : 'dc'}`} data-testid="power-source">
              {tele?.acConnected ? 'AC' : `Battery ${tele?.batteryPct ?? '--'}%`}
            </span>
          </div>
        </header>

        <div className="page" data-testid={`page-${page}`}>
          {page === 'dashboard'  && <DashboardPage {...shared} />}
          {page === 'power'      && <PowerPage />}
          {page === 'fan'        && <FanPage tele={tele} />}
          {page === 'controller' && <ControllerPage />}
          {page === 'display'    && <DisplayPage />}
          {page === 'profiles'   && <ProfilesPage />}
          {page === 'monitor'    && <MonitorPage tele={tele} />}
          {page === 'system'     && <SystemPage tele={tele} />}
          {page === 'settings'   && <SettingsPage auto={auto} setAuto={setAuto} />}
        </div>
      </main>
    </div>
  )
}
