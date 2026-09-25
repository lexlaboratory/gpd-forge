// GPD Forge — Forge Advisor "Suggestions" card (plan F3). GPL-3.0-or-later.
//
// Shared by the Dashboard (the game in front) and the Games page (the game whose sheet is open, else
// the one in front). The daemon decides what to suggest; this only shows it and sends Apply / Dismiss.
// Apply writes the one suggestion into the game's profile and nothing else, so the card says so.
//
// Renders nothing while there is nothing to say: an empty "Suggestions" frame on every visit would
// teach the user to scroll past it.
import { useCallback, useEffect, useState } from 'react'
import type { AdvisorView } from './types'
import { applySuggestion, dismissSuggestion, getAdvisor } from './api'
import { Button, Frame } from './components'
import { displayName } from './gameProfile'

const POLL_MS = 5_000

function time(iso: string): string {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

/** The advisor's answer for a game, polled, with Apply / Dismiss that replace it with the daemon's reply. */
function useAdvisor(game: string | null | undefined, onApplied?: () => void) {
  const [view, setView] = useState<AdvisorView | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => getAdvisor(game ?? undefined).then(setView).catch(() => { /* keep the last answer */ }), [game])

  useEffect(() => {
    setView(null)
    setError(null)
    load()
    const id = setInterval(load, POLL_MS)
    return () => clearInterval(id)
  }, [load])

  const act = async (id: string, kind: 'apply' | 'dismiss') => {
    setBusy(id)
    setError(null)
    try {
      setView(await (kind === 'apply' ? applySuggestion(id) : dismissSuggestion(id)))
      if (kind === 'apply') onApplied?.()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      load()
    } finally {
      setBusy(null)
    }
  }
  return { view, busy, error, act }
}

export function AdvisorCard({ game, onApplied }: {
  /** Ask about this game; omitted = the game in front. */
  game?: string | null
  /** Called after a suggestion was written into a profile, so the page can re-read its rules. */
  onApplied?: () => void
}) {
  const { view, busy, error, act } = useAdvisor(game, onApplied)

  if (!view?.game || (view.suggestions.length === 0 && view.applied.length === 0 && !error)) return null

  return (
    <Frame title="Suggestions" testid="advisor"
           hint={<span data-testid="advisor-game">{displayName(view.game)}{view.live ? ' · live' : ' · from its last session'}</span>}>
      {view.suggestions.length === 0 && <p className="muted" data-testid="advisor-none">Nothing to suggest right now.</p>}
      <ul className="advisor-list">
        {view.suggestions.map((s) => (
          <li key={s.id} className="advisor-item" data-testid={`advisor-item-${s.kind}`}>
            <div className="advisor-text">
              <strong>{s.title}</strong>
              <span className="muted">{s.detail}</span>
            </div>
            <div className="advisor-actions">
              {s.applicable && (
                <Button variant="accent" testid={`advisor-apply-${s.kind}`} disabled={busy !== null}
                        onClick={() => act(s.id, 'apply')}>Apply</Button>
              )}
              <Button variant="ghost" testid={`advisor-dismiss-${s.kind}`} disabled={busy !== null}
                      onClick={() => act(s.id, 'dismiss')}>Dismiss</Button>
            </div>
          </li>
        ))}
      </ul>
      {view.suggestions.some((s) => s.applicable) && (
        <p className="muted advisor-note">Apply saves the change in this game's profile; nothing else is changed.</p>
      )}
      {error && <p className="muted" role="alert" data-testid="advisor-error">{error}</p>}
      {view.applied.length > 0 && (
        <p className="muted advisor-note" data-testid="advisor-applied">
          Applied: {view.applied.slice(0, 3).map((a) => `${a.stapmW != null ? `${a.stapmW} W` : `${a.frameCapFps} FPS cap`} (${time(a.atUtc)})`).join(' · ')}
        </p>
      )}
    </Frame>
  )
}

/**
 * The overlay's one line: the first suggestion that can be applied for the game in front, with Apply.
 * One line because the overlay is glanced at mid-game; the reasoning and Dismiss live on the card.
 */
export function OverlayAdvice() {
  const { view, busy, error, act } = useAdvisor(null)
  const s = view?.suggestions.find((x) => x.applicable)
  if (!s && !error) return null
  return (
    <p className="qam-advice" data-testid="qam-advice" role="status" title={s?.detail}>
      {s && <span className="qam-advice-text">{s.title}</span>}
      {s && (
        <button type="button" className="qam-advice-apply" data-testid="qam-advice-apply" disabled={busy !== null}
                onClick={() => act(s.id, 'apply')}>Apply</button>
      )}
      {error && <span className="qam-advice-error" data-testid="qam-advice-error">{error}</span>}
    </p>
  )
}
