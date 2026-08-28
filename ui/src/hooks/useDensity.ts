// GPD Forge — input-driven density. GPL-3.0-or-later.
//
// Density follows the INPUT, not the viewport. A 1920px screen driven by a gamepad still needs
// thumb-sized targets; a 1024px window driven by a mouse does not. Deciding this from a media query
// on width — the usual shortcut — gets the handheld exactly backwards.
//
// Only tokens change (see tokens.css), never structure, so there remains one layout to maintain.
//
// State lives at MODULE level, not in the hook. The shell and the Settings page both need it, and
// two independent useState copies meant Settings could pin a density that the shell's own instance
// would quietly overwrite the next time the input changed. One store, many subscribers.
import { useEffect, useState } from 'react'

export type Density = 'pad' | 'mouse'

const STORAGE_KEY = 'forge-density'

/** Coarse pointer means a touchscreen, which wants the same targets a thumbstick does. */
const coarse = () => typeof matchMedia === 'function' && matchMedia('(pointer: coarse)').matches
const padConnected = () => Array.from(navigator.getGamepads?.() ?? []).some(Boolean)
const detect = (): Density => (padConnected() || coarse() ? 'pad' : 'mouse')

interface State { density: Density; auto: boolean }

function initial(): State {
  const saved = localStorage.getItem(STORAGE_KEY)
  if (saved === 'pad' || saved === 'mouse') return { density: saved, auto: false }
  return { density: detect(), auto: true }
}

let state = initial()
const listeners = new Set<(s: State) => void>()

function apply(next: State) {
  if (next.density === state.density && next.auto === state.auto) return
  state = next
  document.documentElement.dataset.density = state.density
  listeners.forEach((l) => l(state))
}

/** Only auto mode reacts to input; a pinned density stays pinned. */
const detected = (density: Density) => { if (state.auto) apply({ ...state, density }) }

document.documentElement.dataset.density = state.density

// One set of listeners for the whole app, attached once, rather than per mounted component.
if (typeof window !== 'undefined') {
  // A gamepad only appears in getGamepads() after its first input, so the connect event is the
  // earliest reliable signal.
  window.addEventListener('gamepadconnected', () => detected('pad'))
  window.addEventListener('pointerdown', (e: PointerEvent) => {
    if (e.pointerType === 'mouse') detected('mouse')
    else if (e.pointerType === 'touch' || e.pointerType === 'pen') detected('pad')
  })
  // A gamepad button press wins pad density back after the mouse was used.
  const poll = () => {
    if (state.auto) {
      const gp = Array.from(navigator.getGamepads?.() ?? []).find(Boolean)
      if (gp?.buttons.some((b) => b.pressed)) detected('pad')
    }
    requestAnimationFrame(poll)
  }
  requestAnimationFrame(poll)
}

/** Settings can pin a density; passing null hands control back to detection. */
export function setDensity(next: Density | null) {
  if (next === null) {
    localStorage.removeItem(STORAGE_KEY)
    apply({ density: detect(), auto: true })
  } else {
    localStorage.setItem(STORAGE_KEY, next)
    apply({ density: next, auto: false })
  }
}

export function useDensity() {
  const [local, setLocal] = useState(state)
  useEffect(() => {
    listeners.add(setLocal)
    setLocal(state) // resync in case the store moved between render and subscribe
    return () => { listeners.delete(setLocal) }
  }, [])
  return { density: local.density, auto: local.auto, override: setDensity }
}
