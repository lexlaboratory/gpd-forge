// GPD Forge — the overlay's frame-time graph and pacing line (plan F2). GPL-3.0-or-later.
//
// An FPS number hides what a player feels: 60 FPS with a handful of 50 ms frames is a hitchy 60. The
// graph shows the last 10 s of frame times (up is slower), and the line under it says whether the run
// is even or how often it hitches — the same definition as GET /frames (core/Telemetry/FramePacing.cs).
import type { FramesResponse } from './types'

// The graph's drawing box; the SVG stretches it to the panel's width, so only the ratio matters.
const W = 340
const H = 44
// A graph wider than this many points draws sub-pixel segments; frames are bucketed down to it.
const MAX_POINTS = W / 2

/** Buckets frame times down to at most MAX_POINTS, keeping each bucket's SLOWEST frame: averaging would
 *  melt a single 55 ms hitch into its neighbours, and the hitch is the point of the graph. */
export function bucketMax(times: number[], max = MAX_POINTS): number[] {
  if (times.length <= max) return times
  const out: number[] = []
  for (let i = 0; i < max; i++) {
    const from = Math.floor((i * times.length) / max)
    const to = Math.max(from + 1, Math.floor(((i + 1) * times.length) / max))
    out.push(Math.max(...times.slice(from, to)))
  }
  return out
}

/** "Steady pacing", or how often it hitched. Null when there is nothing to judge. */
export function pacingLabel(frames: FramesResponse | null): string | null {
  const m = frames?.available ? frames.metrics : null
  if (!m) return null
  return m.stutters === 0 ? 'Steady pacing' : `Stutters: ${m.stuttersPerMin}/min`
}

export function FramePacingGraph({ frames }: { frames: FramesResponse | null }) {
  const label = pacingLabel(frames)
  // Nothing presenting, or no frame source at all: the panel says nothing rather than drawing a flat
  // line that would read as "perfectly even".
  if (!frames?.available || !frames.metrics || !label) return null
  const m = frames.metrics
  const points = bucketMax(frames.frametimesMs)
  // Scale to at least 33.3 ms (a 30 FPS frame) so an even 60 FPS run sits low and calm instead of being
  // stretched to fill the box, and capped so one 500 ms load hitch does not flatten everything else.
  const ceiling = Math.min(100, Math.max(33.3, ...points))
  const y = (ms: number) => H - (Math.min(ms, ceiling) / ceiling) * (H - 2) - 1
  const step = points.length > 1 ? W / (points.length - 1) : W
  const path = points.map((ms, i) => `${(i * step).toFixed(1)},${y(ms).toFixed(1)}`).join(' ')
  const hitching = m.stutters > 0

  return (
    <div className="qam-pacing" data-testid="qam-pacing" data-stutters={hitching || undefined}>
      <svg className="qam-pacing-graph" viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none"
        role="img" aria-label={`Frame times, last ${m.spanSeconds} s. ${label}.`} data-testid="qam-pacing-graph">
        {/* The stutter floor (25 ms): a frame above this line is slow enough to be felt. */}
        <line className="qam-pacing-floor" x1="0" x2={W} y1={y(25)} y2={y(25)} />
        <polyline className="qam-pacing-line" points={path} />
      </svg>
      <div className="qam-pacing-row">
        <span className={`qam-pacing-state${hitching ? ' warn' : ''}`} data-testid="qam-pacing-state">{label}</span>
        <span className="qam-pacing-lows" data-testid="qam-pacing-lows">
          1% {Math.round(m.fps1PctLow)} · 0.1% {Math.round(m.fps01PctLow)} fps
        </span>
      </div>
    </div>
  )
}
