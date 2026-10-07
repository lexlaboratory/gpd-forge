// GPD Forge UI — Fan page. GPL-3.0-or-later.
import { useEffect, useRef, useState } from 'react'
import type { Telemetry, FanInfo } from '../types'
import { setFan, getFanInfo, setFanManualDuty } from '../api'
import { reading, fractionOf } from './shared'
import { Frame, Readout, Segmented, Slider, type Tone } from '../components'

export const FAN_MODES = ['Auto', 'Quiet', 'Balanced', 'Aggressive', 'Manual']
export const FAN_GATE_CLOSED_ADVISORY =
  'Curve editor with hysteresis + EC re-init on boot/resume lands with the fan driver (EC access pending PawnIO-stable).'
const MAX_CPU_C = 100
const MIN_EFFECTIVE_DUTY = 40
const MIN_DUTY_PCT = Math.ceil((MIN_EFFECTIVE_DUTY / 255) * 1000) / 10
const rawToPct = (duty: number) => Math.max(MIN_DUTY_PCT, Math.round((duty / 255) * 1000) / 10)
const pct = (duty: number | null | undefined) => duty == null ? '--' : `${Math.round((duty / 255) * 100)}%`
const tempTone = (c: number | null): Tone | undefined =>
  c == null ? undefined : c > 85 ? 'danger' : c > 75 ? 'warn' : 'ok'
interface ExpectedFanStatus { mode: string; requestedDuty: number | null }

export function FanPage({ tele }: { tele: Telemetry | null }) {
  const [fan, setFanInfo] = useState<FanInfo>({ mode: 'Auto', manualDuty: 128, controllable: false })
  const [error, setError] = useState<string | null>(null)
  const [pending, setPending] = useState(false)
  const [draftPct, setDraftPct] = useState(rawToPct(128))
  const [expectedStatus, setExpectedStatus] = useState<ExpectedFanStatus | null>(null)
  const mounted = useRef(true)
  const pendingRef = useRef(false)
  const writeRevision = useRef(0)

  useEffect(() => {
    mounted.current = true
    let timer: ReturnType<typeof setTimeout> | undefined
    let stopped = false
    const refresh = async () => {
      if (pendingRef.current) {
        timer = setTimeout(refresh, 1000)
        return
      }
      const revision = writeRevision.current
      try {
        const next = await getFanInfo()
        if (stopped) return
        if (revision !== writeRevision.current) { timer = setTimeout(refresh, 1000); return }
        setFanInfo(next)
        setDraftPct(rawToPct(next.manualDuty))
        setError(next.status?.error ?? null)
        timer = setTimeout(refresh, 1000)
      } catch (e) {
        if (stopped) return
        if (revision !== writeRevision.current) { timer = setTimeout(refresh, 1000); return }
        setError(`Fan status could not be refreshed: ${e instanceof Error ? e.message : 'unknown error'}`)
        timer = setTimeout(refresh, 2000)
      }
    }
    void refresh()
    return () => { stopped = true; mounted.current = false; if (timer) clearTimeout(timer) }
  }, [])

  const startPending = (value: boolean) => { pendingRef.current = value; setPending(value) }
  const pick = async (mode: string) => {
    writeRevision.current++
    startPending(true)
    setError(null)
    try {
      const accepted = await setFan(mode)
      if (!mounted.current) return
      setExpectedStatus({
        mode: accepted,
        requestedDuty: accepted === 'Manual' ? Math.max(MIN_EFFECTIVE_DUTY, Math.round(fan.manualDuty)) : null,
      })
      setFanInfo((current) => ({ ...current, mode: accepted }))
      try {
        const current = await getFanInfo()
        if (!mounted.current) return
        setFanInfo(current)
        setError(current.status?.error ?? null)
      } catch (e) {
        setError(`Fan mode was saved, but status could not be refreshed: ${e instanceof Error ? e.message : 'unknown error'}`)
      }
    } catch (e) {
      if (mounted.current) setError(`Fan mode was not changed: ${e instanceof Error ? e.message : 'unknown error'}`)
    } finally {
      if (mounted.current) startPending(false)
    }
  }
  const commitDuty = async (value: number) => {
    const rawDuty = Math.max(MIN_EFFECTIVE_DUTY, Math.min(255, Math.round((value / 100) * 255)))
    writeRevision.current++
    startPending(true)
    setError(null)
    try {
      const updated = await setFanManualDuty(rawDuty)
      if (!mounted.current) return
      setExpectedStatus({ mode: 'Manual', requestedDuty: rawDuty })
      setFanInfo(updated)
      setDraftPct(rawToPct(updated.manualDuty))
      setError(updated.status?.error ?? null)
    } catch (e) {
      if (mounted.current) {
        setDraftPct(rawToPct(fan.manualDuty))
        setError(`Fan duty was not changed: ${e instanceof Error ? e.message : 'unknown error'}`)
      }
    } finally {
      if (mounted.current) startPending(false)
    }
  }

  const status = fan.status
  const statusMatchesRequest = expectedStatus == null || (
    status?.mode === expectedStatus.mode &&
    (expectedStatus.requestedDuty == null || status.requestedDuty === expectedStatus.requestedDuty)
  )
  const requestedDuty = expectedStatus && !statusMatchesRequest
    ? expectedStatus.requestedDuty : status?.requestedDuty
  const verificationLabel = expectedStatus && !statusMatchesRequest ? 'Awaiting readback'
    : status?.verified == null ? expectedStatus ? 'Pending' : 'Unknown'
      : status.verified ? 'Verified' : 'Not verified'
  return (
    <Frame title="Fan" hint={fan.controllable ? 'Fan controls are available; check readback status below.' : 'Fan preferences can be saved; hardware control is unavailable on this device.'}>
      <div className="stats">
        <Readout label="Fan" value={reading(tele?.fanRpm)} unit="rpm" />
        <Readout label="CPU" value={reading(tele?.cpuTempC)} unit="°C"
          fraction={fractionOf(tele?.cpuTempC, MAX_CPU_C)} tone={tempTone(tele?.cpuTempC ?? null)} />
        <Readout label="GPU" value={reading(tele?.gpuTempC)} unit="°C"
          fraction={fractionOf(tele?.gpuTempC, MAX_CPU_C)} tone={tempTone(tele?.gpuTempC ?? null)} />
      </div>
      <Segmented
        label="Fan mode"
        value={fan.mode}
        onChange={pick}
        disabled={pending}
        options={FAN_MODES.map((f) => ({ id: f, label: f, testid: `fan-${f.toLowerCase()}` }))}
      />
      {fan.controllable ? (
        fan.mode === 'Manual' ? (
          <Slider label="Manual duty" testid="fan-manual-duty" value={draftPct} min={MIN_DUTY_PCT} max={100} step={0.1} unit="%"
            disabled={pending}
            onChange={setDraftPct} onCommit={commitDuty} />
        ) : (
          <p className="muted">Switch to Manual to set a fixed duty; Quiet/Balanced/Aggressive drive a temperature curve automatically.</p>
        )
      ) : (
        <p className="muted">{FAN_GATE_CLOSED_ADVISORY}</p>
      )}
      {error && <p role="alert" className="muted" data-testid="fan-error">{error}</p>}
      <div className="grid2" data-testid="fan-status">
        <p className="muted" data-testid="fan-requested-duty">Requested: {pct(requestedDuty)}</p>
        <p className="muted" data-testid="fan-readback-duty">Read back: {pct(status?.observedDuty)}</p>
        <p className="muted" data-testid="fan-verification">{verificationLabel}</p>
        <p className="muted">Reported mode: {status?.mode ?? 'Unknown'}</p>
        <p className="muted">Last update: {status?.atUtc ? new Date(status.atUtc).toLocaleString() : 'Unknown'}</p>
      </div>
    </Frame>
  )
}
