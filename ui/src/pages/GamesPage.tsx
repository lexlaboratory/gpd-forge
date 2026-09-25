// GPD Forge UI — Games page: every detected game and its per-game profile. GPL-3.0-or-later.
//
// F1 (2026-09-25). Built from three reads the daemon already answers:
//   GET /sessions/games   what has been played — a game is "detected" once it presented frames for a
//                         minute (core/Sessions), so the list is evidence, not a guess from a launcher;
//   GET /app-rules        which rule claims each game, and whether it carries settings of its own;
//   GET /profiles/active  which profile is in force right now (polled once, by the shell).
// Numbers follow the Sessions page's rule: null is "not measured" and renders as a dash, never as 0.
import { useCallback, useEffect, useRef, useState } from 'react'
import type { ActiveGameProfile, AppRulesInfo, GameSummary, GamesResponse, Preset } from '../types'
import { getAppRules, getProfiles, getSessionGames } from '../api'
import { Badge, Frame, Unavailable, type Tone } from '../components'
import { AUTO_PROFILES_OFF, describeOverrides, displayName, profileState, type ProfileKind } from '../gameProfile'
import { GameProfileSheet } from './GameProfileSheet'
import { AdvisorCard } from '../AdvisorCard'
import { FALLBACK_MODES } from './ProfilesPage'
import { PRESET_LABEL } from './shared'

const DASH = '—'
const num = (v: number | null | undefined, digits = 1) => (v == null || !Number.isFinite(v) ? DASH : v.toFixed(digits))
const day = (iso: string) => {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? DASH : d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
}

const STATE: Record<ProfileKind, { label: string; tone: Tone }> = {
  profile: { label: 'Profile', tone: 'ok' },
  rule: { label: 'Mode only', tone: 'info' },
  none: { label: 'No profile', tone: 'muted' },
}

/** Rules change from other windows too (the overlay's save button), so the list re-reads them —
 *  paused while the editor is open, as the Profiles page does, so a refresh cannot move it mid-edit. */
const RULES_POLL_MS = 5000

export function GamesPage({ active = null }: { active?: ActiveGameProfile | null } = {}) {
  const [games, setGames] = useState<GamesResponse | null>(null)
  const [rules, setRules] = useState<AppRulesInfo | null>(null)
  const [presets, setPresets] = useState<Record<string, Preset> | null>(null)
  const [loading, setLoading] = useState(true)
  const [failed, setFailed] = useState(false)
  const [rulesFailed, setRulesFailed] = useState(false)
  const [selected, setSelected] = useState<string | null>(null)
  const cards = useRef(new Map<string, HTMLButtonElement>())

  useEffect(() => {
    let live = true
    Promise.allSettled([getSessionGames(), getAppRules(), getProfiles()]).then(([g, r, p]) => {
      if (!live) return
      if (g.status === 'fulfilled') setGames(g.value); else setFailed(true)
      if (r.status === 'fulfilled') setRules(r.value); else setRulesFailed(true)
      if (p.status === 'fulfilled') setPresets(p.value)
      setLoading(false)
    })
    return () => { live = false }
  }, [])

  useEffect(() => {
    if (selected) return
    const id = setInterval(() => {
      getAppRules().then((r) => { setRules(r); setRulesFailed(false) }).catch(() => setRulesFailed(true))
    }, RULES_POLL_MS)
    return () => clearInterval(id)
  }, [selected])

  const close = useCallback(() => {
    const app = selected
    setSelected(null)
    // Back to the card that opened it, so a pad user continues from where they were.
    if (app) requestAnimationFrame(() => cards.current.get(app)?.focus())
  }, [selected])

  const list = games?.games ?? []
  const game = selected ? list.find((g) => g.app === selected) ?? null : null
  const fpsAvailable = games?.fpsAvailable ?? true

  return (
    <div className={`games-layout${game ? ' has-sheet' : ''}`}>
      {game && rules && (
        <GameProfileSheet
          key={game.app}
          game={game}
          rules={rules.rules}
          modes={rules.modes?.length ? rules.modes : FALLBACK_MODES}
          presets={presets}
          autoProfiles={rules.autoProfiles}
          onSaved={setRules}
          onClose={close}
        />
      )}

      <Frame title="Detected games" testid="games-list"
             hint={games ? `${list.length} game${list.length === 1 ? '' : 's'} · most played first` : undefined}>
        {loading && <p className="muted">Loading your games…</p>}
        {!loading && failed && (
          <p className="muted" data-testid="games-error">
            The daemon did not answer. Your games come from its play history, so there is nothing to show until it is back.
          </p>
        )}
        {!loading && rulesFailed && (
          <Unavailable testid="games-rules-offline"
            reason="the rules could not be read, so which games have a profile cannot be shown or changed right now." />
        )}
        {/* F1 audit round 4: the cards still read "Profile" — the settings are stored — but with the
            focus worker off nothing puts them on, and the page must not let that pass unsaid. */}
        {rules && !rules.autoProfiles && (
          <Unavailable testid="games-auto-off" reason={`Profiles are stored and editable, but ${AUTO_PROFILES_OFF}.`} />
        )}
        {!loading && !failed && !fpsAvailable && (
          <Unavailable testid="games-no-fps"
            reason="frame-rate telemetry is off, so no game can be detected. A game is recognised by the frames it presents (PresentMon)." />
        )}
        {!loading && !failed && fpsAvailable && list.length === 0 && (
          <p className="muted" data-testid="games-empty">
            No game detected yet — one appears here after it has presented frames for a minute.
          </p>
        )}

        {list.length > 0 && (
          <div className="game-grid">
            {list.map((g) => (
              <GameCard key={g.app} game={g} rules={rules} active={active} open={selected === g.app}
                disabled={!rules}
                cardRef={(el) => { if (el) cards.current.set(g.app, el); else cards.current.delete(g.app) }}
                onOpen={() => setSelected(selected === g.app ? null : g.app)} />
            ))}
          </div>
        )}
      </Frame>

      {/* The open game's advice, else the game in front's. Applying re-reads the rules so the card's
          "Profile" state follows; an open sheet keeps its own draft and is not overwritten. */}
      <AdvisorCard game={selected} onApplied={() => { getAppRules().then(setRules).catch(() => setRulesFailed(true)) }} />
    </div>
  )
}

function GameCard({ game: g, rules, active, open, disabled, cardRef, onOpen }: {
  game: GameSummary
  rules: AppRulesInfo | null
  active: ActiveGameProfile | null
  open: boolean
  disabled: boolean
  cardRef: (el: HTMLButtonElement | null) => void
  onOpen: () => void
}) {
  const { kind, rule } = rules ? profileState(rules.rules, g.app) : { kind: 'none' as const, rule: null }
  const inForce = !!active?.active && rule != null && active.ruleId === rule.id
  const state = STATE[kind]
  const summary = kind === 'profile' ? describeOverrides(rule!.overrides)
    : kind === 'rule' ? `${PRESET_LABEL[rule!.mode] ?? rule!.mode} mode, nothing else changed`
    : 'The AC/battery default decides'
  return (
    <button type="button" ref={cardRef} className={`game-card${open ? ' open' : ''}${kind === 'profile' ? ' has-profile' : ''}`}
            data-testid={`games-card-${g.app}`} aria-expanded={open} disabled={disabled} onClick={onOpen}>
      <span className="game-card-head">
        <span className="game-name">{displayName(g.app)}</span>
        {inForce && <Badge tone="ok" testid={`games-live-${g.app}`}>In force</Badge>}
        <Badge tone={state.tone} testid={`games-state-${g.app}`}>{state.label}</Badge>
      </span>
      <span className="game-meta">
        Last played <span data-testid={`games-last-${g.app}`}>{day(g.lastPlayedUtc)}</span>
        {' · '}<span data-testid={`games-sessions-${g.app}`}>{g.sessions} session{g.sessions === 1 ? '' : 's'}</span>
      </span>
      <span className="game-stats">
        <Stat testid={`games-fps-${g.app}`} value={num(g.fpsAvg)} label="FPS avg" />
        <Stat testid={`games-low-${g.app}`} value={num(g.fps1PctLow)} label="1% low" />
        <Stat testid={`games-watts-${g.app}`} value={num(g.packageAvgW)} unit={g.packageAvgW == null ? undefined : 'W'} label="Power avg" />
      </span>
      <span className="game-summary" data-testid={`games-summary-${g.app}`}>{summary}</span>
    </button>
  )
}

function Stat({ value, unit, label, testid }: { value: string; unit?: string; label: string; testid: string }) {
  return (
    <span className="game-stat" data-testid={testid}>
      <span className="game-stat-v">{value}{unit && <i>{unit}</i>}</span>
      <span className="game-stat-k">{label}</span>
    </span>
  )
}
