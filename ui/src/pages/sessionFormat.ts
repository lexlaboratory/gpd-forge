// GPD Forge UI — how a play session reads on screen. GPL-3.0-or-later.
//
// Shared by the Sessions page and the Games page's recent sessions (F1 audit round 1, 2026-09-25), so a
// session is written the same way wherever it appears. Null is "not measured" and renders as a dash,
// never as 0 — every metric behind a session is optional on this hardware.

export const DASH = '—'

export const num = (v: number | null, digits = 0) => (v === null || !Number.isFinite(v) ? DASH : v.toFixed(digits))

/** Whole minutes below an hour, h+m above — a play session is never interesting to the second. */
export function duration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return DASH
  const total = Math.round(seconds / 60)
  const h = Math.floor(total / 60)
  const m = total % 60
  return h > 0 ? `${h} h ${m} min` : `${m} min`
}

export const when = (iso: string) => {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? DASH : d.toLocaleString()
}
