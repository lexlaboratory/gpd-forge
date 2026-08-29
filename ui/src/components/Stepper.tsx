// GPD Forge â€” value stepper. GPL-3.0-or-later.
//
// Two âˆ’/+ buttons around a readout. This existed only inline in the overlay, twice, and it is the
// single most usable pattern the project has on a handheld: a d-pad can reach a button but cannot
// meaningfully drag a range input. Now it is available to the whole app.
interface Props {
  label: string
  value: number
  unit?: string
  min: number
  max: number
  step?: number
  onChange: (v: number) => void
  testid?: string
  decTestid?: string
  incTestid?: string
  disabled?: boolean
}

export function Stepper({
  label, value, unit, min, max, step = 1, onChange, testid, decTestid, incTestid, disabled,
}: Props) {
  const clamp = (v: number) => Math.min(max, Math.max(min, v))
  return (
    <div className="stepper" data-testid={testid}>
      <button
        type="button" className="stepper-btn" disabled={disabled || value <= min}
        aria-label={`${label} down`} data-testid={decTestid}
        onClick={() => onChange(clamp(value - step))}
      >âˆ’</button>
      <span className="stepper-val" aria-live="polite">
        {value}{unit && <i>{unit}</i>}
      </span>
      <button
        type="button" className="stepper-btn" disabled={disabled || value >= max}
        aria-label={`${label} up`} data-testid={incTestid}
        onClick={() => onChange(clamp(value + step))}
      >+</button>
    </div>
  )
}

