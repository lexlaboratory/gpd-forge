// GPD Forge — 2-D gamepad / keyboard navigation. GPL-3.0-or-later.
//
// The overlay's original version walked focusable elements in DOM order and mapped left/right to
// the same ±1 step as up/down. On a grid — five mode cards, a row of chips, a stepper — that means
// pressing Right and pressing Down do the same thing, and getting from a chip back up to a mode
// takes an unpredictable number of presses. This picks the nearest element in the direction you
// actually pushed, using on-screen geometry.
//
// Shared by the app shell and the overlay so both behave identically.
import { useEffect, type RefObject } from 'react'

export type Direction = 'up' | 'down' | 'left' | 'right'

const FOCUSABLE = 'button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])'

interface Box { el: HTMLElement; x: number; y: number }

const centres = (root: HTMLElement): Box[] =>
  Array.from(root.querySelectorAll<HTMLElement>(FOCUSABLE))
    .filter((el) => el.offsetParent !== null)
    .map((el) => {
      const r = el.getBoundingClientRect()
      return { el, x: r.left + r.width / 2, y: r.top + r.height / 2 }
    })
    .filter((b) => b.x > 0 || b.y > 0)

/**
 * Nearest candidate in `dir`. Distance along the travel axis dominates; drift across it is
 * penalised so a press does not jump diagonally across the panel, but is not forbidden either —
 * otherwise ragged rows become dead ends.
 */
export function pickNeighbour(from: Box, boxes: Box[], dir: Direction): HTMLElement | null {
  const along = (b: Box) => (dir === 'left' ? from.x - b.x : dir === 'right' ? b.x - from.x : dir === 'up' ? from.y - b.y : b.y - from.y)
  const across = (b: Box) => (dir === 'left' || dir === 'right' ? Math.abs(b.y - from.y) : Math.abs(b.x - from.x))

  let best: HTMLElement | null = null
  let bestCost = Infinity
  for (const b of boxes) {
    if (b.el === from.el) continue
    const forward = along(b)
    if (forward <= 1) continue // strictly in the pressed direction
    const cost = forward + across(b) * 2
    if (cost < bestCost) { bestCost = cost; best = b.el }
  }
  return best
}

interface Options {
  /** B / Escape. Omit in the main window, where there is nothing to close. */
  onCancel?: () => void
  enabled?: boolean
}

export function useSpatialNav(rootRef: RefObject<HTMLElement | null>, { onCancel, enabled = true }: Options = {}) {
  useEffect(() => {
    if (!enabled) return
    const root = rootRef.current
    if (!root) return

    const move = (dir: Direction) => {
      const boxes = centres(root)
      if (boxes.length === 0) return
      const active = document.activeElement as HTMLElement | null
      const current = boxes.find((b) => b.el === active)
      // Nothing focused yet (the usual state when a gamepad is picked up mid-session): start at the
      // top-left rather than wherever the DOM happens to begin.
      if (!current) {
        const first = [...boxes].sort((a, b) => a.y - b.y || a.x - b.x)[0]
        first?.el.focus()
        return
      }
      const next = pickNeighbour(current, boxes, dir)
      if (next) next.focus()
      else if (dir === 'down' || dir === 'right') {
        // Wrap at the end so a pad user is never trapped at the last control.
        const sorted = [...boxes].sort((a, b) => a.y - b.y || a.x - b.x)
        sorted[0]?.el.focus()
      }
    }
    const activate = () => (document.activeElement as HTMLElement | null)?.click()

    const onKey = (e: KeyboardEvent) => {
      // Never hijack arrows inside a text field or a slider — those own their own arrow keys.
      const t = e.target as HTMLElement | null
      const tag = t?.tagName.toLowerCase()
      if (tag === 'input' || tag === 'textarea' || tag === 'select') {
        if (e.key === 'Escape' && onCancel) { e.preventDefault(); onCancel() }
        return
      }
      const map: Record<string, Direction> = {
        ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right',
      }
      const dir = map[e.key]
      if (dir) { e.preventDefault(); move(dir) }
      else if (e.key === 'Escape' && onCancel) { e.preventDefault(); onCancel() }
    }
    window.addEventListener('keydown', onKey)

    // Gamepad polling with edge detection so a held button fires once.
    let raf = 0
    const prev = new Map<number, boolean>()
    const axis = { x: 0, y: 0 }
    const edge = (i: number, down: boolean) => { const was = prev.get(i) ?? false; prev.set(i, down); return down && !was }
    const poll = () => {
      const gp = Array.from(navigator.getGamepads?.() ?? []).find(Boolean)
      if (gp) {
        const b = (i: number) => !!gp.buttons[i]?.pressed
        if (edge(12, b(12))) move('up')
        if (edge(13, b(13))) move('down')
        if (edge(14, b(14))) move('left')
        if (edge(15, b(15))) move('right')
        if (edge(0, b(0))) activate()
        if (edge(1, b(1))) onCancel?.()
        const ax = gp.axes[0] ?? 0
        const ay = gp.axes[1] ?? 0
        if (ay > 0.6 && axis.y <= 0.6) move('down')
        if (ay < -0.6 && axis.y >= -0.6) move('up')
        if (ax > 0.6 && axis.x <= 0.6) move('right')
        if (ax < -0.6 && axis.x >= -0.6) move('left')
        axis.x = ax; axis.y = ay
      }
      raf = requestAnimationFrame(poll)
    }
    raf = requestAnimationFrame(poll)
    return () => { window.removeEventListener('keydown', onKey); cancelAnimationFrame(raf) }
  }, [rootRef, onCancel, enabled])
}
