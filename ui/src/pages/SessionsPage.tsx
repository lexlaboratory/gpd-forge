// GPD Forge UI — Sessions page (per-session and per-game play history). GPL-3.0-or-later.
//
// The daemon only records a session while an application is actually presenting frames (PresentMon,
// see core/Sessions/). So an empty list has two very different meanings and this page must not blur
// them: either frame-rate telemetry is off — in which case nothing can ever be recorded and we say
// so — or it is on and you have not played anything yet. Likewise, every metric can be null because
// every sensor behind it is optional on this hardware; null renders as an em dash, never as a zero.
import { useEffect, useState } from 'react'
import type { GameSession, GameSummary, GamesResponse, SessionsResponse } from '../types'
import { getSessions, getSessionGames } from '../api'
import { Frame, Readout, Badge, Button, Segmented, Unavailable } from '../components'
import { Sparkline } from '../Chart'

// --- formatting ---------------------------------------------------------------
const DASH = '—'
const num = (v: number | null, digits = 0) => (v === null || !Number.isFinite(v) ? DASH : v.toFixed(digits))

/** Whole minutes below an hour, h+m above — a play session is never interesting to the second. */
function duration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return DASH
  const total = Math.round(seconds / 60)
  const h = Math.floor(total / 60)
  const m = total % 60
  return h > 0 ? `${h} h ${m} min` : `${m} min`
}

const when = (iso: string) => {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? DASH : d.toLocaleString()
}

const VIEWS = [
  { id: 'sessions', label: 'Sessions' },
  { id: 'games', label: 'By game' },
] as const
type ViewId = typeof VIEWS[number]['id']

// --- page ---------------------------------------------------------------------
export function SessionsPage() {
  const [view, setView] = useState<ViewId>('sessions')
  const [data, setData] = useState<SessionsResponse | null>(null)
  const [games, setGames] = useState<GamesResponse | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let live = true
    Promise.all([getSessions(), getSessionGames()])
      .then(([s, g]) => { if (live) { setData(s); setGames(g) } })
      .catch(() => { if (live) setFailed(true) })
      .finally(() => { if (live) setLoading(false) })
    return () => { live = false }
  }, [])

  const sessions = data?.sessions ?? []
  const detail = selected ? sessions.find((s) => s.id === selected) ?? null : null
  // Either response answers the question; the daemon reports the same gate on both.
  const fpsAvailable = data?.fpsAvailable ?? games?.fpsAvailable ?? true

  if (detail) return <SessionDetail session={detail} onBack={() => setSelected(null)} />

  return (
    <>
      <Frame
        title="Play history"
        hint={data?.current
          ? <Badge tone="ok">recording · {data.current}</Badge>
          : <Badge tone="muted">{fpsAvailable ? 'idle' : 'fps telemetry off'}</Badge>}
        testid="sessions-page"
      >
        <div className="row">
          <Segmented options={VIEWS} value={view} onChange={setView} label="Session history view" />
        </div>

        {loading && <p className="muted">Loading play history…</p>}
        {!loading && failed && (
          <p className="muted" data-testid="sessions-error">
            The daemon did not answer. Play history lives in the daemon, so there is nothing to show until it is back.
          </p>
        )}

        {!loading && !failed && !fpsAvailable && (
          <Unavailable
            testid="sessions-no-fps"
            reason="frame-rate telemetry is off, so no session can be recorded. Sessions are detected from the frames a game presents (PresentMon); without it the daemon has no honest way to tell that you were playing."
          />
        )}

        {!loading && !failed && fpsAvailable && sessions.length === 0 && (
          <p className="muted" data-testid="sessions-empty">
            No session has been recorded yet — one is recorded when a game presents frames for at least a minute.
          </p>
        )}

        {!loading && !failed && view === 'sessions' && sessions.length > 0 && (
          <div className="session-list" data-testid="session-list">
            {sessions.map((s) => (
              <SessionRow key={s.id} session={s} onOpen={() => setSelected(s.id)} />
            ))}
          </div>
        )}

        {!loading && !failed && view === 'games' && (games?.games.length ?? 0) > 0 && (
          <div className="session-list" data-testid="game-list">
            {games!.games.map((g) => <GameRow key={g.app} game={g} />)}
          </div>
        )}
      </Frame>
    </>
  )
}

function SessionRow({ session: s, onOpen }: { session: GameSession; onOpen: () => void }) {
  return (
    <article className="session-row" data-testid={`session-${s.id}`}>
      <div className="session-row-head">
        <h3 className="mono">{s.app}</h3>
        <span className="muted">{when(s.startedUtc)}</span>
      </div>
      <div className="stats">
        <Readout label="Duration" value={duration(s.durationSeconds)} />
        <Readout label="FPS avg" value={num(s.fpsAvg, 1)} />
        <Readout label="1% low" value={num(s.fps1PctLow, 1)} />
        <Readout label="CPU peak" value={num(s.cpuTempMaxC, 1)} unit="°C" />
        {/* Battery drain only exists for a session that never saw the charger — see GameSession.OnBattery. */}
        <Readout label="Battery used" value={s.onBattery ? num(s.batteryUsedPct) : DASH} unit={s.onBattery && s.batteryUsedPct !== null ? '%' : undefined} />
      </div>
      <div className="row-end">
        <Button variant="ghost" onClick={onOpen} testid={`session-open-${s.id}`}>Details</Button>
      </div>
    </article>
  )
}

function GameRow({ game: g }: { game: GameSummary }) {
  return (
    <article className="session-row" data-testid={`game-${g.app}`}>
      <div className="session-row-head">
        <h3 className="mono">{g.app}</h3>
        <span className="muted">last played {when(g.lastPlayedUtc)}</span>
      </div>
      <div className="stats">
        <Readout label="Playtime" value={duration(g.totalSeconds)} />
        <Readout label="Sessions" value={String(g.sessions)} />
        <Readout label="FPS avg" value={num(g.fpsAvg, 1)} />
        <Readout label="Best run" value={num(g.fpsBest, 1)} />
        <Readout label="CPU peak" value={num(g.cpuTempMaxC, 1)} unit="°C" />
      </div>
    </article>
  )
}

function SessionDetail({ session: s, onBack }: { session: GameSession; onBack: () => void }) {
  // Coverage is worth stating whenever it is not total: an average over 40% of a session is a
  // weaker claim than one over all of it, and the reader deserves to know which they are reading.
  const covered = s.samples > 0 ? Math.round(((s.samples - s.samplesWithoutFps) / s.samples) * 100) : 0
  return (
    <Frame
      title={s.app}
      hint={<Badge tone={s.onBattery ? 'warn' : 'muted'}>{s.onBattery ? 'on battery' : 'plugged in'}</Badge>}
      testid="session-detail"
    >
      <p className="muted">{when(s.startedUtc)} → {when(s.endedUtc)} · {duration(s.durationSeconds)}</p>

      <div className="stats">
        <Readout label="FPS avg" value={num(s.fpsAvg, 1)} />
        <Readout label="1% low" value={num(s.fps1PctLow, 1)} />
        <Readout label="FPS peak" value={num(s.fpsMax, 1)} />
        <Readout label="CPU avg" value={num(s.cpuTempAvgC, 1)} unit="°C" />
        <Readout label="CPU peak" value={num(s.cpuTempMaxC, 1)} unit="°C" />
        <Readout label="Package" value={num(s.packageAvgW, 1)} unit="W" />
        <Readout
          label="Battery"
          value={s.onBattery && s.batteryStartPct !== null ? `${s.batteryStartPct} → ${num(s.batteryEndPct)}` : DASH}
          unit={s.onBattery && s.batteryStartPct !== null ? '%' : undefined}
          footer={s.onBattery && s.batteryUsedPct !== null
            ? <span className="muted">{s.batteryUsedPct}% used</span>
            : undefined}
        />
      </div>

      {s.fpsTrend.length > 1 ? (
        <div className="charts">
          <Sparkline
            data={s.fpsTrend}
            label="FPS over the session"
            color="var(--good)"
            width={360}
            height={92}
            surface="var(--bg-elev)"
            testid="session-fps-trend"
          />
        </div>
      ) : (
        <p className="muted">Not enough frame-rate samples to draw a trend for this session.</p>
      )}

      <p className="muted" data-testid="session-coverage">
        {s.samples} sample{s.samples === 1 ? '' : 's'}
        {s.samplesWithoutFps > 0 && ` · frame rate measured for ${covered}% of them`}
      </p>

      <div className="row-end">
        <Button variant="ghost" onClick={onBack} testid="session-back">Back to history</Button>
      </div>
    </Frame>
  )
}
