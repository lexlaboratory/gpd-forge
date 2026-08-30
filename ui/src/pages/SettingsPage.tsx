// GPD Forge UI — Settings page. GPL-3.0-or-later.
import { useEffect, useState } from 'react'
import { Frame, Toggle, Segmented, Readout } from '../components'
import { useDensity } from '../hooks/useDensity'
import { PowerSourceCard, GuardianCard, BackupRestoreCard, UpdateNote } from './SystemPage'
import { getVersion } from '../api'
import type { DaemonVersion } from '../types'

const DENSITY_OPTIONS = [
  { id: 'auto', label: 'Auto', testid: 'density-auto' },
  { id: 'pad', label: 'Gamepad', testid: 'density-pad' },
  { id: 'mouse', label: 'Mouse', testid: 'density-mouse' },
] as const
type DensityChoice = typeof DENSITY_OPTIONS[number]['id']

export function SettingsPage({ auto, setAuto, theme, setTheme, textScale, setTextScale }: {
  auto: boolean; setAuto: (v: boolean) => void; theme: 'dark' | 'light'; setTheme: (t: 'dark' | 'light') => void
  textScale: 'normal' | 'large'; setTextScale: (t: 'normal' | 'large') => void
}) {
  const { density, auto: densityAuto, override } = useDensity()
  const densityChoice: DensityChoice = densityAuto ? 'auto' : density
  return (
    <>
      <Frame title="Automation">
        <div className="row">
          <Toggle on={auto} onClick={() => setAuto(!auto)} label="Auto-optimize by app in focus" testid="settings-auto" />
        </div>
        <p className="muted">When on, GPD Forge switches modes automatically from the foreground app.</p>
      </Frame>
      <Frame title="Appearance">
        <div className="row">
          <Toggle on={theme === 'dark'} onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')} label="Dark theme" testid="settings-theme" />
        </div>
        <div className="row">
          <Toggle on={textScale === 'large'} onClick={() => setTextScale(textScale === 'large' ? 'normal' : 'large')} label="Large text" testid="settings-textscale" />
        </div>
        <p className="muted">Scales up text size across the UI — for readability on the Win 4's small screen.</p>
        <p className="muted">Target size</p>
        <Segmented
          label="Target size" testid="density-mode" options={DENSITY_OPTIONS} value={densityChoice}
          // 'auto' is not a density, it is the absence of a pinned one — hence override(null).
          onChange={(id) => override(id === 'auto' ? null : id)}
        />
        <p className="muted">
          Auto follows the input in use: a connected gamepad or a touch means thumb-sized controls,
          a mouse means compact ones. Pin it if you would rather it never changed under you.
        </p>
      </Frame>
      <PowerSourceCard />
      <GuardianCard />
      <BackupRestoreCard />
      <Frame title="About">
        <p className="muted">GPD Forge — the definitive open-source tuning tool for GPD handhelds. GPL-3.0 · lexlaboratory · github.com/lexlaboratory/gpd-forge</p>
        <VersionCard />
        <UpdateNote />
      </Frame>
    </>
  )
}

// What this installation actually is — shell build, daemon build, and whether they agree.
//
// The mismatch line is the reason this card exists. On 2026-08-28 the app showed no telemetry while
// the daemon was healthy the entire time: the shell in Program Files predated the commit that fixed
// it. Nothing on screen could say so, and establishing it meant diffing the installed binary against
// a fresh one hunting for marker strings. A version the user can read turns that into a glance.
//
// Fields the daemon did not record render as "not recorded", never as a blank or a plausible
// substitute: an invented build date is worse than no build date, because it would be believed.
export function VersionCard() {
  const [daemon, setDaemon] = useState<DaemonVersion | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    getVersion().then((v) => { setDaemon(v); setFailed(false) }).catch(() => setFailed(true))
  }, [])

  const shell = __APP_VERSION__
  // Only claim a mismatch once the daemon has actually answered. "Unknown" is not "different".
  const mismatch = daemon !== null && daemon.version !== shell

  return (
    <div data-testid="version-card">
      <div className="stats">
        <Readout testid="version-shell" label="App (shell)" value={shell} />
        <Readout testid="version-daemon" label="Daemon" value={daemon?.version ?? (failed ? 'unreachable' : '--')} />
      </div>
      {mismatch && (
        <p className="muted" data-testid="version-mismatch">
          ⚠️ The app window ({shell}) and the daemon ({daemon!.version}) are from different builds. One of
          them is stale — reinstall so both come from the same source, before trusting either about what
          is fixed.
        </p>
      )}
      {daemon && (
        <p className="muted" data-testid="version-build">
          Built {daemon.builtUtc ? new Date(daemon.builtUtc).toLocaleString() : 'at an unrecorded time'}
          {daemon.commit ? ` from ${daemon.commit.slice(0, 12)}` : ', commit not recorded'} · {daemon.runtime}
        </p>
      )}
    </div>
  )
}
