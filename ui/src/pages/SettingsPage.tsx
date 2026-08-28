// GPD Forge UI — Settings page. GPL-3.0-or-later.
import { Frame, Toggle, Segmented } from '../components'
import { useDensity } from '../hooks/useDensity'
import { PowerSourceCard, GuardianCard, BackupRestoreCard, UpdateNote } from './SystemPage'

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
        <UpdateNote />
      </Frame>
    </>
  )
}
