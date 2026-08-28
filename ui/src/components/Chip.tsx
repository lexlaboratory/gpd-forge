// GPD Forge — chip and segmented group. GPL-3.0-or-later.
//
// The chip was pasted 10 times in the dashboard and duplicated again in the overlay under a
// `qam-chip` prefix. One implementation, one behaviour.
import type { ReactNode } from 'react'

interface ChipProps {
  on?: boolean
  onClick: () => void
  children: ReactNode
  testid?: string
  disabled?: boolean
  /** `qam` keeps the overlay's own class so its specs and gamepad walk still match. */
  flavour?: 'page' | 'qam'
  title?: string
}

export function Chip({ on = false, onClick, children, testid, disabled, flavour = 'page', title }: ChipProps) {
  const base = flavour === 'qam' ? 'qam-chip' : 'chip-btn'
  return (
    <button
      type="button"
      className={`${base}${on ? ' on' : ''}`}
      aria-pressed={on}
      disabled={disabled}
      data-testid={testid}
      title={title}
      onClick={onClick}
    >
      {children}
    </button>
  )
}

interface SegmentedProps<T extends string> {
  options: ReadonlyArray<{ id: T; label: ReactNode; testid?: string }>
  value: T
  onChange: (id: T) => void
  label: string
  testid?: string
  flavour?: 'page' | 'qam'
}

/**
 * A single-choice row of chips. Carries the radio semantics the loose copies never had, so a
 * screen reader announces "2 of 5" instead of five unrelated toggle buttons.
 */
export function Segmented<T extends string>({ options, value, onChange, label, testid, flavour }: SegmentedProps<T>) {
  return (
    <div className="chips" role="radiogroup" aria-label={label} data-testid={testid}>
      {options.map((o) => (
        <button
          key={o.id}
          type="button"
          role="radio"
          aria-checked={value === o.id}
          className={`${flavour === 'qam' ? 'qam-chip' : 'chip-btn'}${value === o.id ? ' on' : ''}`}
          data-testid={o.testid}
          onClick={() => onChange(o.id)}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}
