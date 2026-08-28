// GPD Forge UI — Power page (editable per-mode TDP presets). GPL-3.0-or-later.
import { useEffect, useState } from 'react'
import type { Preset, AutoFps } from '../types'
import { getProfiles, setProfile, getAutoFps, setAutoFps } from '../api'
import { Badge, Button, Frame, Segmented, Slider, Toggle } from '../components'
import { useToast } from '../Toast'
import { PRESET_LABEL } from './shared'
import { TunerCard } from './DashboardPage'

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
      <Frame title="Power presets" hint="Tune each mode's TDP — GPD Forge applies it through the closed loop.">
        <Segmented
          label="Preset mode"
          testid="preset-modes"
          value={mode}
          onChange={setMode}
          options={Object.keys(presets).map((k) => ({ id: k, label: PRESET_LABEL[k] ?? k, testid: `preset-${k}` }))}
        />
        {draft ? (
          <div className="grid2">
            <Slider label="STAPM (sustained)" testid="p-stapm" value={draft.stapmW} min={5} max={40} unit=" W" onChange={(v) => edit('stapmW', v)} />
            <Slider label="Fast (boost)"       testid="p-fast"  value={draft.fastW}  min={5} max={45} unit=" W" onChange={(v) => edit('fastW', v)} />
            <Slider label="Slow"               testid="p-slow"  value={draft.slowW}  min={5} max={45} unit=" W" onChange={(v) => edit('slowW', v)} />
            <Slider label="Thermal limit"      testid="p-tctl"  value={draft.tctlC}  min={60} max={95} unit=" °C" onChange={(v) => edit('tctlC', v)} />
          </div>
        ) : <p className="muted">Loading presets…</p>}
        <div className="row-end">
          {saved && <Badge tone="ok" testid="preset-saved">saved</Badge>}
          <Button variant="accent" testid="preset-apply" onClick={apply} disabled={!draft}>Save preset</Button>
        </div>
      </Frame>
      <Frame title="Auto-TDP to FPS" hint="Gaming — hold a target FPS at the least power">
        <div className="row">
          <Toggle on={afps.enabled} onClick={toggleFps} label={afps.enabled ? 'Enabled' : 'Disabled'} testid="autofps-toggle" />
        </div>
        <Slider label="Target FPS" testid="autofps-target" value={afps.targetFps} min={30} max={120} unit=" fps"
          onChange={(v) => setAfps((s) => ({ ...s, targetFps: v }))} onCommit={commitFps} />
        <p className="muted">Steers TDP with a PID to keep your FPS at target. Activates in gaming mode once FPS telemetry is available (PresentMon).</p>
      </Frame>
      <TunerCard />
      {/* The GPU placeholder and the LED / charge-limit / undervolt panel moved to the Hardware
          page. Power now holds only controls that actually reach the hardware. */}
    </>
  )
}
