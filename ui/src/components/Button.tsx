// GPD Forge — button. GPL-3.0-or-later.
//
// Replaces 19 hand-written `<button className="btn ...">` copies (and two `<a className="btn">`
// anchors dressed as buttons). Every one of them had to remember `type="button"`, and several did
// not — inside a form that submits the page.
import type { ButtonHTMLAttributes, ReactNode } from 'react'

export type ButtonVariant = 'default' | 'accent' | 'danger' | 'ghost'

interface Props extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'className'> {
  variant?: ButtonVariant
  children: ReactNode
  testid?: string
  /** Renders as a link that looks identical. Use only when it really navigates. */
  href?: string
  download?: string
}

const classFor = (variant: ButtonVariant) =>
  variant === 'default' ? 'btn' : `btn btn-${variant}`

export function Button({ variant = 'default', children, testid, href, download, ...rest }: Props) {
  if (href) {
    return (
      <a className={classFor(variant)} href={href} download={download} data-testid={testid}>
        {children}
      </a>
    )
  }
  return (
    <button type="button" className={classFor(variant)} data-testid={testid} {...rest}>
      {children}
    </button>
  )
}
