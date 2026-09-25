// GPD Forge — value stepper. GPL-3.0-or-later.
//
// Two buttons around a readout. This existed only inline in the overlay, twice, and it is the single
// most usable pattern the project has on a handheld: a d-pad can reach a button but cannot
// meaningfully drag a range input. Now it is available to the whole app.
//
// A null value is "not known yet": the readout shows '--' and both buttons are disabled, because any
// number here would read as the value in force (the overlay's TDP opened on a hardcoded 20 W until
// audit round 3, 2026-09-24).
interface Props {
  label: string
  value: number | null
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
  const unknown = value == null
  return (
    <div className="stepper" data-testid={testid}>
      <button
        type="button" className="stepper-btn" disabled={disabled || unknown || value <= min}
        aria-label={`${label} down`} data-testid={decTestid}
        onClick={() => value != null && onChange(clamp(value - step))}
      >&minus;</button>
      <span className="stepper-val" aria-live="polite">
        {unknown ? '--' : <>{value}{unit && <i>{unit}</i>}</>}
      </span>
      <button
        type="button" className="stepper-btn" disabled={disabled || unknown || value >= max}
        aria-label={`${label} up`} data-testid={incTestid}
        onClick={() => value != null && onChange(clamp(value + step))}
      >+</button>
    </div>
  )
}
