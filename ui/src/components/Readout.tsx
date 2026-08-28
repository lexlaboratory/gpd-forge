// GPD Forge — instrument readout. GPL-3.0-or-later.
//
// The HUD's signature element: a labelled number with an optional fill bar underneath, so a glance
// gives you the magnitude without reading the digits. `fraction` is optional — a readout with no
// sensible range (FPS, rpm without a known ceiling) simply omits the bar rather than inventing one.
import type { ReactNode } from 'react'
import { Meter } from './Meter'
import type { Tone } from './Badge'

interface Props {
  label: string
  value: string
  unit?: string
  testid?: string
  /** 0..1. Omit when the value has no meaningful maximum. */
  fraction?: number
  tone?: Tone
  footer?: ReactNode
}

export function Readout({ label, value, unit, testid, fraction, tone, footer }: Props) {
  return (
    <div className="tile" data-testid={testid}>
      <span className="tile-label">{label}</span>
      <span className="tile-value">
        {value}
        {unit && <span className="tile-unit">{unit}</span>}
      </span>
      {typeof fraction === 'number' && <Meter fraction={fraction} tone={tone} label={label} />}
      {footer}
    </div>
  )
}
