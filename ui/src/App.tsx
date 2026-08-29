// GPD Forge UI — app shell (multi-page). GPL-3.0-or-later.
import { useEffect, useRef, useState } from 'react'
import type { ModeId, Telemetry } from './types'
import { getTelemetry, getMode, setMode as apiSetMode, getAlertSummary } from './api'
import { Toggle } from './ui'
import { useDensity } from './hooks/useDensity'
import { useHashRoute } from './hooks/useHashRoute'
import { useSpatialNav } from './hooks/useSpatialNav'
import { DaemonOfflineBanner } from './DaemonOfflineBanner'
import {
  MODES, DashboardPage, PowerPage, FanPage, HardwarePage, DisplayPage,
  ProfilesPage, MonitorPage, SystemPage, SettingsPage, AlertsPage, type Shared,
} from './pages'
import { Wizard, isSetupDone } from './Wizard'
import { ErrorBoundary } from './ErrorBoundary'
import { CommandPalette } from './CommandPalette'

const NAV = [
  { id: 'dashboard',  label: 'Dashboard',  icon: '📊' },
  { id: 'power',      label: 'Power',      icon: '⚡' },
  { id: 'fan',        label: 'Fan',        icon: '🌀' },
  // Replaces the old "Controller" entry, which was a whole top-level section of disabled sliders
  // advertising a feature that does not exist. Hardware reports what this board can and cannot do.
  { id: 'hardware',   label: 'Hardware',   icon: '🔩' },
  { id: 'display',    label: 'Display',    icon: '🔆' },
  { id: 'profiles',   label: 'Profiles',   icon: '🗂️' },
  { id: 'monitor',    label: 'Monitor',    icon: '📈' },
  { id: 'system',     label: 'System',     icon: '🩺' },
  { id: 'settings',   label: 'Settings',   icon: '⚙️' },
  { id: 'alerts',     label: 'Alerts',     icon: '🔔' },
] as const
type PageId = typeof NAV[number]['id']
const PAGE_IDS = NAV.map((n) => n.id)

export function App() {
  // Hash routing: deep-linkable, browser back/forward works, and a notification or hotkey can open
  // a specific section. Previously the hash was read once at boot and never written.
  const [page, setPage] = useHashRoute<PageId>(PAGE_IDS, 'dashboard')
  const shellRef = useRef<HTMLDivElement>(null)
  const pageRef = useRef<HTMLDivElement>(null)
  // Sets data-density on <html>; the tokens do the rest. Settings gets a manual override in the
  // page redesign.
  useDensity()
  // Gamepad navigation now covers the whole app, not just the overlay. No cancel action: there is
  // nothing to close in the main window, and B should not quit it by accident.
  useSpatialNav(shellRef)
  const [tele, setTele] = useState<Telemetry | null>(null)
  const [active, setActive] = useState<ModeId>('windows')
  const [auto, setAuto] = useState(true)
  const [connected, setConnected] = useState(false)
  const [lastError, setLastError] = useState<string | null>(null)
  const [retryBusy, setRetryBusy] = useState(false)
  const [theme, setTheme] = useState<'dark' | 'light'>(() => (localStorage.getItem('forge-theme') as 'dark' | 'light') || 'dark')
  const [textScale, setTextScale] = useState<'normal' | 'large'>(() => (localStorage.getItem('forge-textscale') as 'normal' | 'large') || 'normal')
  const [showWizard, setShowWizard] = useState(() => !isSetupDone())
  const [unreadAlerts, setUnreadAlerts] = useState(0)

  const retryConnection = async () => {
    setRetryBusy(true)
    try {
      const t = await getTelemetry()
      setTele(t); setConnected(true); setLastError(null)
    } catch (e) {
      setConnected(false)
      setLastError(e instanceof Error ? e.message : String(e))
    } finally {
      setRetryBusy(false)
    }
  }

  useEffect(() => {
    document.documentElement.dataset.theme = theme
    localStorage.setItem('forge-theme', theme)
  }, [theme])

  useEffect(() => {
    document.documentElement.dataset.textscale = textScale
    localStorage.setItem('forge-textscale', textScale)
  }, [textScale])

  // Move focus into the new section on navigation. Without this the focus stays on the sidebar
  // button, so a screen reader announces nothing and a d-pad user has to walk back down the nav
  // every single time.
  useEffect(() => {
    pageRef.current?.focus({ preventScroll: true })
  }, [page])

  useEffect(() => {
    let alive = true
    const tick = () =>
      getTelemetry()
        .then((t) => { if (alive) { setTele(t); setConnected(true); setLastError(null) } })
        .catch((e: unknown) => {
          if (!alive) return
          setConnected(false)
          // `fetch` errors typically land here with a TypeError "Failed to fetch" / "NetworkError";
          // surface the message verbatim — enough to tell a port-down from a CORS preflight failure.
          setLastError(e instanceof Error ? e.message : String(e))
        })
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

  useEffect(() => {
    let alive = true
    const tick = () => getAlertSummary().then((s) => alive && setUnreadAlerts(s.unread)).catch(() => {})
    tick(); const id = setInterval(tick, 5000)
    return () => { alive = false; clearInterval(id) }
  }, [])

  const pickMode = (id: ModeId) => { setAuto(false); setActive(id); apiSetMode(id).catch(() => {}) }
  const shared: Shared = { tele, active, auto, setAuto, pickMode }
  const connLabel = connected ? 'Live' : 'Offline'
  const activeMode = MODES.find((m) => m.id === active)

  return (
    <div className="shell" ref={shellRef}>
      <a className="skip-link" href="#main-content">Skip to content</a>
      <aside className="nav">
        <div className="nav-brand">
          <img className="brand-logo" src="/logo.png" alt="" aria-hidden width={34} height={34} />
          <span className="nav-name">GPD Forge</span>
        </div>
        <nav className="nav-list" aria-label="Sections">
          {NAV.map((n) => (
            <button key={n.id} type="button" className={`nav-item ${page === n.id ? 'active' : ''}`}
                    aria-current={page === n.id ? 'page' : undefined}
                    data-testid={`nav-${n.id}`} onClick={() => setPage(n.id)}>
              <span className="nav-icon" aria-hidden>{n.icon}</span><span className="nav-label">{n.label}</span>
              {n.id === 'alerts' && unreadAlerts > 0 && <span className="nav-badge" aria-label={`${unreadAlerts} unread alerts`}>{unreadAlerts > 99 ? '99+' : unreadAlerts}</span>}
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

        <div className="page" id="main-content" data-testid={`page-${page}`}
             ref={pageRef} tabIndex={-1} aria-label={NAV.find((n) => n.id === page)?.label}>
          {!connected && (
            <DaemonOfflineBanner
              reason={lastError}
              onRetry={retryConnection}
              busy={retryBusy}
            />
          )}
          {/* Scoped to the page body on purpose: the sidebar, the live telemetry header and the
              offline banner must survive a panel blowing up, so the app degrades to "this section
              failed" instead of an empty window. */}
          <ErrorBoundary resetKey={page}>
          {page === 'dashboard'  && <DashboardPage {...shared} />}
          {page === 'power'      && <PowerPage />}
          {page === 'fan'        && <FanPage tele={tele} />}
          {page === 'hardware'   && <HardwarePage />}
          {page === 'display'    && <DisplayPage />}
          {page === 'profiles'   && <ProfilesPage />}
          {page === 'monitor'    && <MonitorPage tele={tele} />}
          {page === 'system'     && <SystemPage tele={tele} />}
          {page === 'settings'   && <SettingsPage auto={auto} setAuto={setAuto} theme={theme} setTheme={setTheme} textScale={textScale} setTextScale={setTextScale} />}
          {page === 'alerts'     && <AlertsPage onChanged={() => getAlertSummary().then((s) => setUnreadAlerts(s.unread)).catch(() => {})} />}
          </ErrorBoundary>
        </div>
      </main>

      {/* Outside the ErrorBoundary on purpose: the palette is the escape hatch when a panel has
          failed, so it must not be able to go down with one. */}
      <CommandPalette navigate={(p) => setPage(p as PageId)} pages={PAGE_IDS} />

      {showWizard && <Wizard onClose={() => setShowWizard(false)} />}
    </div>
  )
}
