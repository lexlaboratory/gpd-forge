// GPD Forge — status badge. GPL-3.0-or-later.
//
// The old stylesheet grew TEN near-identical pill systems (.badge, .badge-verified, .conn,
// .power-pill, .job-status-*, .soon, .chip, .nav-badge, .bb-chip, .qam-ok) with no shared
// abstraction. They are one component with a tone.
import type { ReactNode } from 'react'

export type Tone = 'ok' | 'warn' | 'danger' | 'info' | 'muted'

const TONE_CLASS: Record<Tone, string> = {
  ok: 'badge-verified',
  warn: 'badge-unverified',
  danger: 'badge-health-critical',
  info: 'job-status-queued',
  muted: 'job-status-done',
}

export function Badge({ tone = 'muted', children, testid }: { tone?: Tone; children: ReactNode; testid?: string }) {
  return <span className={`badge ${TONE_CLASS[tone]}`} data-testid={testid}>{children}</span>
}

/** "Coming soon" marker. Kept distinct from Badge because it carries a promise, not a state. */
export function Soon({ children }: { children?: ReactNode }) {
  return <span className="soon">{children ?? 'coming soon'}</span>
}

/**
 * Why a control does nothing, stated plainly next to it. The alternative — a button that looks
 * live and silently no-ops — is what made the app feel fake.
 */
export function Unavailable({ reason, testid }: { reason: string; testid?: string }) {
  return (
    <p className="muted" data-testid={testid}>
      <span className="badge badge-unverified">unavailable</span> {reason}
    </p>
  )
}
