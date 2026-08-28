// GPD Forge — toggle and slider. GPL-3.0-or-later.
import type { ReactNode } from 'react'

export function Toggle({ on, onClick, label, testid, disabled }: {
  on: boolean; onClick: () => void; label: ReactNode; testid?: string; disabled?: boolean
}) {
  return (
    <button
      type="button" className={`switch ${on ? 'on' : ''}`} aria-pressed={on}
      data-testid={testid} disabled={disabled} onClick={onClick}
    >
      <span className="switch-track"><span className="switch-thumb" /></span>{label}
    </button>
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
        // Commit on pointerup rather than mouseup so a touch drag on the handheld also commits;
        // the old mouseup-only version silently dropped every touch adjustment.
        onPointerUp={(e) => onCommit?.(Number((e.target as HTMLInputElement).value))}
        onKeyUp={(e) => onCommit?.(Number((e.target as HTMLInputElement).value))}
      />
    </div>
  )
}
