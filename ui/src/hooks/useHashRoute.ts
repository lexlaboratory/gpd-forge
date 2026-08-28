// GPD Forge — hash routing. GPL-3.0-or-later.
//
// The shell kept its section in useState and read `#alerts` once at boot but never wrote the hash
// back, so there was no deep link, no browser back/forward, and no way for a notification or a
// hotkey to open a specific page. This is the whole router: no dependency, no build cost.
import { useCallback, useEffect, useState } from 'react'

export function useHashRoute<T extends string>(valid: readonly T[], fallback: T) {
  const parse = useCallback((): T => {
    const id = window.location.hash.replace(/^#/, '') as T
    return valid.includes(id) ? id : fallback
  }, [valid, fallback])

  const [route, setRoute] = useState<T>(parse)

  useEffect(() => {
    const onHash = () => setRoute(parse())
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [parse])

  const navigate = useCallback((id: T) => {
    // Assigning the hash pushes a history entry, so Back returns to the previous section.
    if (window.location.hash.replace(/^#/, '') !== id) window.location.hash = id
    else setRoute(id)
  }, [])

  return [route, navigate] as const
}
