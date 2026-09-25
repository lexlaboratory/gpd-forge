// GPD Forge UI — the power-source pill in the top bar. GPL-3.0-or-later.
import type { Telemetry } from './types'
import { unsampled } from './pages/shared'

/**
 * AC, battery with its percentage, or — when the daemon's battery query failed — unknown.
 *
 * The third state is audit round 2 (2026-09-24). A failed query reaches the wire as the cautious
 * `acConnected: false` with `batteryPct: null`, and this pill turned that into a confident
 * "Battery --%" in the battery colour on a machine plugged into the wall. `acKnown: false` says the
 * value is a fallback; it is shown as unknown, muted, with the reason in the tooltip. An older daemon
 * without the field means known, which is what it always meant.
 *
 * A reading the daemon never took (`sampledAtMs: null` — its first hardware read hung) is unknown
 * too, whatever `acKnown` says: an older daemon sent it as `acKnown: true`, and this pill drew a
 * confident "Battery --%" from a reading that was never made (audit round 3, 2026-09-24).
 */
export function PowerPill({ tele }: { tele: Telemetry | null }) {
  const never = unsampled(tele)
  if (tele?.acKnown === false || never) {
    return (
      <span className="power-pill unknown" data-testid="power-source" data-state="unknown"
            title={never
              ? 'No telemetry yet: the daemon has not read the hardware, so the power source is not known.'
              : 'The power source could not be read. The AC/battery mode switch and the per-app rules are paused until it can.'}>
        Power --
      </span>
    )
  }
  return (
    <span className={`power-pill ${tele?.acConnected ? 'ac' : 'dc'}`} data-testid="power-source">
      {tele?.acConnected ? 'AC' : `Battery ${tele?.batteryPct ?? '--'}%`}
    </span>
  )
}
