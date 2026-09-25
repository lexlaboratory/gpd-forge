// GPD Forge UI — a game's last few play sessions, in its profile editor. GPL-3.0-or-later.
//
// F1 audit round 1 (2026-09-25). The plan's Games page is "each detected game with its profile, its
// recent sessions and a profile editor"; the cards only carried the per-game averages, so no single
// session — when, how long, how it ran — was anywhere on the page. Read from GET /sessions with the
// game's exact app name (appFilter), newest first, formatted as the Sessions page writes them.
import { useEffect, useState } from 'react'
import type { GameSession } from '../types'
import { getSessions } from '../api'
import { duration, num, when } from './sessionFormat'

/** Enough to see a trend across a profile change, few enough to keep Save in reach below it. */
export const RECENT_SESSIONS = 5

export function GameRecentSessions({ app }: { app: string }) {
  const [sessions, setSessions] = useState<GameSession[] | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let live = true
    getSessions(RECENT_SESSIONS, app)
      .then((r) => { if (live) setSessions(r.sessions) })
      .catch(() => { if (live) setFailed(true) })
    return () => { live = false }
  }, [app])

  return (
    <div className="sheet-field" data-testid="game-recent">
      <span className="sheet-label">Recent sessions</span>
      {failed && <p className="muted" data-testid="game-recent-error">The sessions could not be read right now.</p>}
      {!failed && sessions == null && <p className="muted">Loading sessions…</p>}
      {sessions?.length === 0 && <p className="muted" data-testid="game-recent-empty">No session recorded yet.</p>}
      {sessions != null && sessions.length > 0 && (
        <ul className="game-recent-list">
          {sessions.map((s) => (
            <li key={s.id} className="game-recent" data-testid={`game-recent-${s.id}`}>
              <span className="game-recent-when">{when(s.startedUtc)}</span>
              <span className="game-recent-stats">
                {duration(s.durationSeconds)} · {num(s.fpsAvg, 1)} FPS · 1% low {num(s.fps1PctLow, 1)}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
