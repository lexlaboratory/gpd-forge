// GPD Forge — framed surface. GPL-3.0-or-later.
//
// `.card` and `.panel` were byte-for-byte the same box under two names. One component; the class
// stays selectable so existing specs and the corner-bracket rules keep matching.
import type { ReactNode } from 'react'

interface Props {
  title?: string
  hint?: ReactNode
  children: ReactNode
  testid?: string
  /** `panel` only exists so Jobs/Standby keep their historical class hook. */
  as?: 'card' | 'panel'
}

export function Frame({ title, hint, children, testid, as = 'card' }: Props) {
  return (
    <section className={as} data-testid={testid}>
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
