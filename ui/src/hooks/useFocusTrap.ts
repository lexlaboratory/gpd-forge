// GPD Forge — modal focus trap. GPL-3.0-or-later.
//
// The first-run wizard declared role="dialog" aria-modal="true" and then let Tab walk straight out
// into the dashboard behind it. A modal that does not hold focus is a modal only to a sighted mouse
// user; everyone else falls through it.
import { useEffect, type RefObject } from 'react'

const FOCUSABLE = 'button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'

export function useFocusTrap(ref: RefObject<HTMLElement | null>, active = true) {
  useEffect(() => {
    if (!active) return
    const node = ref.current
    if (!node) return

    const previouslyFocused = document.activeElement as HTMLElement | null
    const items = () => Array.from(node.querySelectorAll<HTMLElement>(FOCUSABLE)).filter((el) => el.offsetParent !== null)

    // Focus the first control rather than the dialog box, so the first keypress does something.
    items()[0]?.focus()

    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Tab') return
      const els = items()
      if (els.length === 0) return
      const first = els[0]
      const last = els[els.length - 1]
      const current = document.activeElement
      if (e.shiftKey && (current === first || !node.contains(current))) {
        e.preventDefault(); last.focus()
      } else if (!e.shiftKey && current === last) {
        e.preventDefault(); first.focus()
      }
    }
    document.addEventListener('keydown', onKey, true)
    return () => {
      document.removeEventListener('keydown', onKey, true)
      // Hand focus back to whatever opened the dialog.
      previouslyFocused?.focus?.()
    }
  }, [ref, active])
}
