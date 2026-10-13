// GPD Forge UI — fixed GPU clock card (Power page). GPL-3.0-or-later.
//
// What it can and cannot do is measured, not assumed (2026-10-13, GPD Win 4 / HX 370): the 890M offers
// no clock tuning through ADLX, PSMU 0x89 FIXES the clock — idle included — and only a restart hands
// it back to the driver. So the card never offers "automatic" and always says how to get back to it.
import { useEffect, useState } from 'react'
import type { GpuClockInfo } from '../types'
import { getGpuClock, setGpuClock } from '../api'
import { Button, Frame, Slider } from '../components'
import { useToast } from '../Toast'

const DEFAULT_MHZ = 1400

function stateText(info: GpuClockInfo | null): string {
  if (info == null) return 'Loading…'
  if (info.forcedMhz == null) return 'Automatic — the graphics driver decides.'
  const since = info.sinceUtc ? ` since ${new Date(info.sinceUtc).toLocaleTimeString()}` : ''
  return `Fixed at ${info.forcedMhz} MHz${since} — automatic again after a restart.`
}

export function GpuClockCard() {
  const [info, setInfo] = useState<GpuClockInfo | null>(null)
  const [draft, setDraft] = useState(DEFAULT_MHZ)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const toast = useToast()

  useEffect(() => {
    getGpuClock()
      .then((v) => { setInfo(v); if (v.forcedMhz != null) setDraft(v.forcedMhz) })
      .catch((e) => setError(`GPU clock status could not be loaded: ${e instanceof Error ? e.message : 'unknown error'}`))
  }, [])

  const apply = async () => {
    if (busy) return
    setBusy(true)
    setError(null)
    try {
      const next = await setGpuClock(draft)
      setInfo(next)
      toast.push({ kind: 'success', message: `GPU clock fixed at ${next.forcedMhz} MHz until restart` })
    } catch (e) {
      setError(`GPU clock was not set: ${e instanceof Error ? e.message : 'unknown error'}`)
      // Show what the daemon holds now rather than what was asked for.
      getGpuClock().then(setInfo).catch(() => {})
    } finally { setBusy(false) }
  }

  const disabled = busy || !info?.supported
  return (
    <Frame title="GPU clock" hint="Fix the Radeon 890M at one clock. It stays fixed until the handheld restarts." testid="gpu-clock">
      <p data-testid="gpu-clock-state" role="status">{stateText(info)}</p>
      {info && !info.supported && (
        <p className="muted" data-testid="gpu-clock-unsupported">
          Not available here: it needs GPD Forge's direct power backend (pawnio-strix) with hardware access on.
        </p>
      )}
      <Slider label="Clock" testid="gpu-clock-mhz" value={draft} min={info?.minMhz ?? 400} max={info?.maxMhz ?? 2900}
        step={50} unit=" MHz" onChange={setDraft} disabled={disabled} />
      <p className="muted">
        A fixed clock stays fixed when nothing is running, so it costs power at idle. AMD's firmware has no way
        back to automatic: Restart the handheld to return to it — sleep and a shut-down with Fast Startup keep it.
      </p>
      <div className="row-end">
        <Button variant="accent" testid="gpu-clock-apply" onClick={apply} disabled={disabled}>
          {busy ? 'Applying…' : `Fix at ${draft} MHz`}
        </Button>
      </div>
      {error && <p role="alert" className="muted" data-testid="gpu-clock-error">{error}</p>}
    </Frame>
  )
}
