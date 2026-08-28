// GPD Forge — input-driven density. GPL-3.0-or-later.
//
// Density follows the INPUT, not the viewport. A 1920px screen driven by a gamepad still needs
// thumb-sized targets; a 1024px window driven by a mouse does not. Deciding this from a media query
// on width — the usual shortcut — gets the handheld exactly backwards.
//
// Only tokens change (see tokens.css), never structure, so there remains one layout to maintain.
import { useEffect, useState } from 'react'

export type Density = 'pad' | 'mouse'

const STORAGE_KEY = 'forge-density'

/** Coarse pointer means a touchscreen, which wants the same targets a thumbstick does. */
const coarse = () => typeof matchMedia === 'function' && matchMedia('(pointer: coarse)').matches
const padConnected = () => Array.from(navigator.getGamepads?.() ?? []).some(Boolean)

function initial(): { density: Density; auto: boolean } {
  const saved = localStorage.getItem(STORAGE_KEY)
  if (saved === 'pad' || saved === 'mouse') return { density: saved, auto: false }
  return { density: padConnected() || coarse() ? 'pad' : 'mouse', auto: true }
}

export function useDensity() {
  const [{ density, auto }, setState] = useState(initial)

  useEffect(() => {
    document.documentElement.dataset.density = density
  }, [density])

  useEffect(() => {
    if (!auto) return

    const toPad = () => setState((s) => (s.auto && s.density !== 'pad' ? { ...s, density: 'pad' } : s))
    const toMouse = () => setState((s) => (s.auto && s.density !== 'mouse' ? { ...s, density: 'mouse' } : s))

    // A gamepad only appears in getGamepads() after its first input, so the connect event is the
    // earliest reliable signal.
    const onConnect = () => toPad()
    const onPointer = (e: PointerEvent) => {
      if (e.pointerType === 'mouse') toMouse()
      else if (e.pointerType === 'touch' || e.pointerType === 'pen') toPad()
    }
    // A gamepad button press should win back pad density even after the mouse was used.
    let raf = 0
    const poll = () => {
      const gp = Array.from(navigator.getGamepads?.() ?? []).find(Boolean)
      if (gp?.buttons.some((b) => b.pressed)) toPad()
      raf = requestAnimationFrame(poll)
    }
    raf = requestAnimationFrame(poll)

    window.addEventListener('gamepadconnected', onConnect)
    window.addEventListener('pointerdown', onPointer)
    return () => {
      window.removeEventListener('gamepadconnected', onConnect)
      window.removeEventListener('pointerdown', onPointer)
      cancelAnimationFrame(raf)
    }
  }, [auto])

  /** Settings can pin a density; passing null hands control back to detection. */
  const override = (next: Density | null) => {
    if (next === null) {
      localStorage.removeItem(STORAGE_KEY)
      setState({ density: padConnected() || coarse() ? 'pad' : 'mouse', auto: true })
    } else {
      localStorage.setItem(STORAGE_KEY, next)
      setState({ density: next, auto: false })
    }
  }

  return { density, auto, override }
}
