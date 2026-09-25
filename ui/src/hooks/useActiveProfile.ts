// GPD Forge — the game profile in force, and the moment it changes. GPL-3.0-or-later.
//
// F1 (2026-09-25), Alex's call: profiles apply automatically, WITH notice. The daemon records what it
// actually applied (GET /profiles/active); this polls it and hands a newly applied profile to the
// caller once — the main window turns that into a toast, the overlay shows the current one as a line
// in its header.
//
// The first answer is a baseline, not a change: opening the window over a game already running its
// profile must not announce it as if it had just happened. A re-application (the rule edited
// mid-game) is a change — the daemon re-stamps `sinceUtc` — so the new values are announced.
import { useEffect, useRef, useState } from 'react'
import type { ActiveGameProfile } from '../types'
import { getActiveProfile } from '../api'
import { profileKey } from '../gameProfile'

/** Same cadence as the Profiles page's rules poll; the daemon settles a profile over ~4.5 s anyway. */
export const ACTIVE_PROFILE_POLL_MS = 3000

export function useActiveProfile(onApplied?: (p: ActiveGameProfile) => void): ActiveGameProfile | null {
  const [profile, setProfile] = useState<ActiveGameProfile | null>(null)
  const lastKey = useRef<string | null | undefined>(undefined)   // undefined = no answer yet
  const notify = useRef(onApplied)
  notify.current = onApplied

  useEffect(() => {
    let alive = true
    const tick = () => getActiveProfile()
      .then((p) => {
        if (!alive) return
        setProfile(p)
        const key = profileKey(p)
        const first = lastKey.current === undefined
        if (!first && key !== null && key !== lastKey.current) notify.current?.(p)
        lastKey.current = key
      })
      // A daemon without F1 (404) or one that is down: nothing is in force as far as we can tell, and
      // the offline banner already says why. Keep the baseline so a recovery is not a false notice.
      .catch(() => { if (alive) setProfile(null) })
    tick()
    const id = setInterval(tick, ACTIVE_PROFILE_POLL_MS)
    return () => { alive = false; clearInterval(id) }
  }, [])

  return profile
}
