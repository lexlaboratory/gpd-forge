// GPD Forge — fill bar. GPL-3.0-or-later.
//
// Presentational only: it renders the fraction it is given and never decides what "hot" means.
// Thresholds belong to the daemon's guardian, not to a bar.
import type { Tone } from './Badge'

interface Props {
  /** 0..1; clamped, and NaN renders as empty rather than as a broken bar. */
  fraction: number
  tone?: Tone
  label?: string
}

const clamp = (n: number) => (Number.isFinite(n) ? Math.min(1, Math.max(0, n)) : 0)

export function Meter({ fraction, tone = 'info', label }: Props) {
  const pct = clamp(fraction) * 100
  return (
    <div
      className={`meter meter-${tone}`}
      role="meter"
      aria-valuenow={Math.round(pct)}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-label={label}
    >
      <span className="meter-fill" style={{ width: `${pct}%` }} />
    </div>
  )
}
