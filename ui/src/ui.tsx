// GPD Forge UI — shared atoms. GPL-3.0-or-later.
import type { ReactNode } from 'react'

export function Tile({ label, value, unit, testid }: { label: string; value: string; unit?: string; testid?: string }) {
  return (
    <div className="tile" data-testid={testid}>
      <span className="tile-label">{label}</span>
      <span className="tile-value">{value}{unit && <span className="tile-unit">{unit}</span>}</span>
    </div>
  )
}

export function Card({ title, hint, children, testid }: { title?: string; hint?: ReactNode; children: ReactNode; testid?: string }) {
  return (
    <section className="card" data-testid={testid}>
      {(title || hint) && (
        <div className="card-head">
          {title && <h2 className="card-title">{title}</h2>}
          {hint && <span className="card-hint">{hint}</span>}
        </div>
      )}
      {children}
    </section>
  )
}

export function Slider({
  label, value, min, max, step = 1, unit, onChange, onCommit, testid, disabled,
}: {
  label: string; value: number; min: number; max: number; step?: number; unit?: string
  onChange: (v: number) => void; onCommit?: (v: number) => void; testid?: string; disabled?: boolean
}) {
  return (
    <div className={`slider ${disabled ? 'disabled' : ''}`}>
      <div className="slider-top"><span>{label}</span><output>{value}{unit}</output></div>
      <input
        type="range" min={min} max={max} step={step} value={value}
        data-testid={testid} disabled={disabled} aria-label={label}
        onChange={(e) => onChange(Number(e.target.value))}
        onMouseUp={(e) => onCommit?.(Number((e.target as HTMLInputElement).value))}
        onKeyUp={(e) => onCommit?.(Number((e.target as HTMLInputElement).value))}
      />
    </div>
  )
}

export function Toggle({ on, onClick, label, testid }: { on: boolean; onClick: () => void; label: string; testid?: string }) {
  return (
    <button type="button" className={`switch ${on ? 'on' : ''}`} aria-pressed={on} data-testid={testid} onClick={onClick}>
      <span className="switch-track"><span className="switch-thumb" /></span>{label}
    </button>
  )
}

export function Soon({ children }: { children?: ReactNode }) {
  return <span className="soon">{children ?? 'coming soon'}</span>
}
