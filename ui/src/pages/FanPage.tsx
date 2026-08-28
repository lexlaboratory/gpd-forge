// GPD Forge UI — Fan page. GPL-3.0-or-later.
import { useEffect, useState } from 'react'
import type { Telemetry, FanInfo } from '../types'
import { setFan, getFanInfo, setFanManualDuty } from '../api'
import { Frame, Readout, Segmented, Slider, type Tone } from '../components'

// --- Fan ----------------------------------------------------------------------
export const FAN_MODES = ['Auto', 'Quiet', 'Balanced', 'Aggressive', 'Manual']
export const FAN_GATE_CLOSED_ADVISORY =
  'Curve editor with hysteresis + EC re-init on boot/resume lands with the fan driver (EC access pending PawnIO-stable).'
const MAX_CPU_C = 100
const tempTone = (c: number): Tone => (c > 85 ? 'danger' : c > 75 ? 'warn' : 'ok')

export function FanPage({ tele }: { tele: Telemetry | null }) {
  const [fan, setFanInfo] = useState<FanInfo>({ mode: 'Auto', manualDuty: 128, controllable: false })
  useEffect(() => { getFanInfo().then(setFanInfo).catch(() => {}) }, [])
  const pick = (f: string) => { setFanInfo((s) => ({ ...s, mode: f })); setFan(f).catch(() => {}) }
  const commitDuty = (v: number) => { setFanManualDuty(v).catch(() => {}) }
  return (
    <>
      <Frame title="Fan" hint={fan.controllable ? 'Live — writes the EC.' : 'Preference saved now; curve applied when the fan-control gate is open.'}>
        <div className="stats">
          {/* Rpm has no honest ceiling here, so only the two temperatures carry a bar. */}
          <Readout label="Fan" value={tele ? `${tele.fanRpm}` : '--'} unit="rpm" />
          <Readout label="CPU" value={tele ? `${Math.round(tele.cpuTempC)}` : '--'} unit="°C"
            fraction={tele ? tele.cpuTempC / MAX_CPU_C : undefined} tone={tele ? tempTone(tele.cpuTempC) : undefined} />
          <Readout label="GPU" value={tele ? `${Math.round(tele.gpuTempC)}` : '--'} unit="°C"
            fraction={tele ? tele.gpuTempC / MAX_CPU_C : undefined} tone={tele ? tempTone(tele.gpuTempC) : undefined} />
        </div>
        <Segmented
          label="Fan mode"
          value={fan.mode}
          onChange={pick}
          options={FAN_MODES.map((f) => ({ id: f, label: f, testid: `fan-${f.toLowerCase()}` }))}
        />
        {fan.controllable ? (
          fan.mode === 'Manual' ? (
            <Slider label="Manual duty" testid="fan-manual-duty" value={fan.manualDuty} min={0} max={255} unit=" /255"
              onChange={(v) => setFanInfo((s) => ({ ...s, manualDuty: v }))} onCommit={commitDuty} />
          ) : (
            <p className="muted">Switch to Manual to set a fixed duty; Quiet/Balanced/Aggressive drive a temp curve automatically.</p>
          )
        ) : (
          <p className="muted">{FAN_GATE_CLOSED_ADVISORY}</p>
        )}
      </Frame>
    </>
  )
}
