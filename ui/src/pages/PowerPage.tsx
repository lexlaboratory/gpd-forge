// GPD Forge UI — Power page (editable per-mode TDP presets). GPL-3.0-or-later.
import { useEffect, useState } from 'react'
import type { Preset, AutoFps, TdpInfo, Telemetry, ModeId } from '../types'
import { getProfiles, setProfile, getAutoFps, setAutoFps, getTdp, getTelemetry, getMode } from '../api'
import { Badge, Button, Frame, Segmented, Slider, Toggle } from '../components'
import { useToast } from '../Toast'
import { PRESET_LABEL } from './shared'
import { TunerCard } from './DashboardPage'

export function PowerPage() {
  const [presets, setPresets] = useState<Record<string, Preset>>({})
  const [mode, setMode] = useState<string>('gaming')
  const [draft, setDraft] = useState<Preset | null>(null)
  const [saved, setSaved] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [afps, setAfps] = useState<AutoFps>({ enabled: false, targetFps: 60 })
  const [fpsDraft, setFpsDraft] = useState(60)
  const [fpsError, setFpsError] = useState<string | null>(null)
  const [fpsPending, setFpsPending] = useState(false)
  const [tdp, setTdp] = useState<TdpInfo | null>(null)
  const [tele, setTele] = useState<Telemetry | null>(null)
  const [activeMode, setActiveMode] = useState<ModeId | null>(null)
  const [tdpError, setTdpError] = useState<string | null>(null)
  const toast = useToast()

  useEffect(() => {
    getProfiles().then(setPresets)
      .catch((e) => setSaveError(`Presets could not be loaded: ${e instanceof Error ? e.message : 'unknown error'}`))
    getAutoFps().then((value) => { setAfps(value); setFpsDraft(value.targetFps) })
      .catch((e) => setFpsError(`Auto-FPS settings could not be loaded: ${e instanceof Error ? e.message : 'unknown error'}`))
    getTdp().then(setTdp).catch((e) => setTdpError(`TDP status could not be loaded: ${e instanceof Error ? e.message : 'unknown error'}`))
    getTelemetry().then(setTele).catch(() => {})
    getMode().then(setActiveMode).catch(() => {})
  }, [])
  useEffect(() => { setDraft(presets[mode] ?? null) }, [mode, presets])
  useEffect(() => { setSaved(false); setSaveError(null) }, [mode])

  const isSustained = mode === 'ai'
  const edit = (k: keyof Preset, v: number) => {
    if (!draft) return
    setDraft({ ...draft, [k]: v })
    setSaved(false)
    setSaveError(null)
  }
  const apply = async () => {
    if (!draft || saving) return
    setSaving(true)
    setSaveError(null)
    setSaved(false)
    try {
      const stored = await setProfile(mode, draft)
      const next = { stapmW: stored.stapmW, fastW: stored.fastW, slowW: stored.slowW, tctlC: stored.tctlC }
      setDraft(next)
      setPresets((current) => ({ ...current, [mode]: next }))
      setSaved(true)
      toast.push({ kind: 'success', message: `${PRESET_LABEL[mode] ?? mode} preset saved` })
    } catch (e) {
      setSaveError(`Preset was not saved: ${e instanceof Error ? e.message : 'unknown error'}`)
    } finally { setSaving(false) }
  }
  const toggleFps = async () => {
    if (fpsPending) return
    setFpsPending(true)
    setFpsError(null)
    try {
      const next = await setAutoFps(afps.targetFps, !afps.enabled)
      setAfps(next)
      setFpsDraft(next.targetFps)
    } catch (e) {
      setFpsError(`Auto-FPS was not changed: ${e instanceof Error ? e.message : 'unknown error'}`)
    } finally { setFpsPending(false) }
  }
  const commitFps = async (value: number) => {
    if (fpsPending) return
    setFpsPending(true)
    setFpsError(null)
    try {
      const next = await setAutoFps(value, afps.enabled)
      setAfps(next)
      setFpsDraft(next.targetFps)
    } catch (e) {
      setFpsDraft(afps.targetFps)
      setFpsError(`Auto-FPS settings were not saved: ${e instanceof Error ? e.message : 'unknown error'}`)
    } finally { setFpsPending(false) }
  }

  const desired = tdp?.intentStapmW ?? tdp?.manualStapmW ?? (activeMode ? presets[activeMode]?.stapmW : null)
  const verification = tdp?.backend === 'stub'
    ? 'not verified'
    : tdp?.verificationStatus ?? (tdp?.verified == null ? 'unknown' : tdp.verified ? 'verified' : 'not verified')
  const verifyLabel = verification === 'verified' ? 'Verified' : verification === 'mismatch' || verification === 'not verified'
    ? 'Not verified' : verification === 'unavailable' || verification === 'unknown' ? 'Unknown' : verification
  const lastAttempt = tdp?.atUtc ? new Date(tdp.atUtc).toLocaleString() : 'Unknown'

  return (
    <>
      <Frame title="Power presets" hint="Save each mode's desired TDP preset. Saving a preset does not apply it to the hardware.">
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
              {!isSustained && <>
                <Slider label="Fast (boost)" testid="p-fast" value={draft.fastW} min={5} max={45} unit=" W" onChange={(v) => edit('fastW', v)} />
                <Slider label="Slow" testid="p-slow" value={draft.slowW} min={5} max={45} unit=" W" onChange={(v) => edit('slowW', v)} />
              </>}
              <Slider label="Thermal limit" testid="p-tctl" value={draft.tctlC} min={60} max={95} unit=" °C" onChange={(v) => edit('tctlC', v)} />
            </div>
            {isSustained && <p className="muted" data-testid="preset-sustained-note">
              No boost sliders here on purpose. This mode runs at one flat ceiling — fast and slow are pinned to STAPM ({draft.stapmW} W).
            </p>}
          </>
        ) : <p className="muted">Loading presets…</p>}
        <div className="row-end">
          {saved && <Badge tone="ok" testid="preset-saved">Saved preset</Badge>}
          <Button variant="accent" testid="preset-apply" onClick={apply} disabled={!draft || saving}>{saving ? 'Saving…' : 'Save preset'}</Button>
        </div>
        {saveError && <p role="alert" className="muted" data-testid="preset-error">{saveError}</p>}
        {saved && <p className="muted">Preset saved. Hardware application is reported in TDP status below.</p>}
      </Frame>
      <Frame title="TDP status" hint="Desired limit, current request, hardware readback, and measured package power are separate readings. Verification refers to the last request." testid="tdp-status">
        <div className="grid2">
          <p className="muted">Desired: {desired == null ? '--' : `${desired} W`}</p>
          <p className="muted">Requested now: {tdp?.stapmW == null ? '--' : `${tdp.stapmW} W`}</p>
          <p className="muted">Read back: {tdp?.observedStapmW == null ? '--' : `${tdp.observedStapmW} W`}</p>
          <p className="muted">Measured: {tele?.packageW == null ? '--' : `${tele.packageW} W`}</p>
          <p className="muted">Verification (last request): {verifyLabel}</p>
          <p className="muted">Owner: {tdp?.owner ?? 'Unknown'}</p>
          <p className="muted">Last attempt: {lastAttempt}{tdp?.attempts == null ? '' : ` · ${tdp.attempts} attempt${tdp.attempts === 1 ? '' : 's'}`}</p>
        </div>
        {(tdp?.error || tdpError) && <p role="alert" className="muted" data-testid="tdp-error">{tdp?.error ?? tdpError}</p>}
      </Frame>
      <Frame title="Auto-TDP to FPS" hint="Gaming — hold a target FPS at the least power">
        <div className="row">
          <Toggle on={afps.enabled} onClick={toggleFps} label={afps.enabled ? 'Enabled' : 'Disabled'} testid="autofps-toggle" disabled={fpsPending} />
        </div>
        <Slider label="Target FPS" testid="autofps-target" value={fpsDraft} min={30} max={120} unit=" fps"
          disabled={fpsPending} onChange={setFpsDraft} onCommit={commitFps} />
        <p className="muted">Steers TDP with a PID to keep your FPS at target. Activates in gaming mode once FPS telemetry is available (PresentMon).</p>
        {fpsError && <p role="alert" className="muted" data-testid="autofps-error">{fpsError}</p>}
      </Frame>
      <TunerCard />
    </>
  )
}
