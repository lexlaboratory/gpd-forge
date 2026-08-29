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

  // Mirrors ModeProfiles.SustainedMode in the daemon: the mode whose profile is a flat ceiling
  // rather than a burst budget, so fast/slow are not the user's to set.
  const isSustained = mode === 'ai'

  const edit = (k: keyof Preset, v: number) => draft && setDraft({ ...draft, [k]: v })
  const apply = () => {
    if (!draft) return
    // Re-seed the draft from the daemon's reply rather than from what was posted: the sustained mode
    // collapses fast/slow onto STAPM, and showing the numbers we sent would leave the sliders lying
    // about what is on the silicon.
    setProfile(mode, draft).then((stored) => {
      setDraft({ stapmW: stored.stapmW, fastW: stored.fastW, slowW: stored.slowW, tctlC: stored.tctlC })
      setSaved(true)
      toast.push({ kind: 'success', message: `${PRESET_LABEL[mode] ?? mode} preset saved` })
    }).catch(() => {})
  }
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
          <>
            <div className="grid2">
              <Slider label="STAPM (sustained)" testid="p-stapm" value={draft.stapmW} min={5} max={40} unit=" W" onChange={(v) => edit('stapmW', v)} />
              {!isSustained && (
                <>
                  <Slider label="Fast (boost)" testid="p-fast" value={draft.fastW} min={5} max={45} unit=" W" onChange={(v) => edit('fastW', v)} />
                  <Slider label="Slow"         testid="p-slow" value={draft.slowW} min={5} max={45} unit=" W" onChange={(v) => edit('slowW', v)} />
                </>
              )}
              <Slider label="Thermal limit" testid="p-tctl" value={draft.tctlC} min={60} max={95} unit=" °C" onChange={(v) => edit('tctlC', v)} />
            </div>
            {isSustained && (
              // Not rendered as disabled sliders: a control you can see and cannot move invites the
              // question this sentence answers, and the daemon would discard the value anyway.
              <p className="muted" data-testid="preset-sustained-note">
                No boost sliders here on purpose. This mode runs at one flat ceiling —
                fast and slow are pinned to STAPM ({draft.stapmW} W). Boosting above the sustained
                limit buys no throughput once a job is continuously CPU-bound; it only adds heat,
                fan noise and thermal cycling.
              </p>
            )}
          </>
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
