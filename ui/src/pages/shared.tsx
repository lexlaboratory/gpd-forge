// GPD Forge UI — shared page constants and types. GPL-3.0-or-later.
import type { Mode, ModeId, TdpInfo, Telemetry } from '../types'

export const MODES: Mode[] = [
  { id: 'gaming',  label: 'Gaming', blurb: 'Auto-TDP to target FPS, reactive fan, OSD.' },
  { id: 'gaming-battery', label: 'Gaming (battery)', blurb: 'Frame-capped at 45 and cooler — the longest session away from a charger.' },
  { id: 'ai',      label: 'Agents / AI', blurb: 'Sustained CPU, VRAM/UMA, anti-standby, local API.' },
  { id: 'windows', label: 'Windows', blurb: 'Balanced power, quiet fan, hotkeys.' },
  { id: 'battery', label: 'Battery', blurb: 'Low TDP floor, longest runtime.' },
  { id: 'standby', label: 'Standby Doctor', blurb: 'Restore TDP+fan+HID on resume, fix drain.' },
]

// Short, correctly-cased chip labels for the preset keys (so 'ai' shows as 'AI', not 'Ai').
export const PRESET_LABEL: Record<string, string> = {
  battery: 'Battery', windows: 'Windows', gaming: 'Gaming', 'gaming-battery': 'Gaming (batt)',
  ai: 'AI', standby: 'Standby',
}

/**
 * Render a reading that may not exist.
 *
 * The placeholder is the whole point. Telemetry went nullable on 2026-09-01 because an unreadable
 * sensor used to arrive as 0, and the panel showed a CPU at 0 °C — a confident, wrong number nobody
 * could distinguish from "cold". Every tile that shows a sensor goes through here so that decision
 * is made in one place rather than re-derived per tile, which is how one of them ends up printing
 * the zero again.
 *
 * A real zero still renders as 0: nothing presenting frames, nothing discharging on AC.
 */
export const reading = (v: number | null | undefined, digits = 0): string =>
  v == null ? '--' : v.toFixed(digits)

/** A progress fraction only when there is something to scale — never a bar drawn from nothing. */
export const fractionOf = (v: number | null | undefined, max: number): number | undefined =>
  v == null ? undefined : v / max

/**
 * A reading older than this is stale: three of the daemon's 1 Hz sampler ticks — the same bound
 * GET /health/check and the standby drain sampler use. Since 2026-09-24 GET /telemetry serves a cached
 * sample, so a sampler whose hardware read hangs keeps answering 200 with the same numbers forever;
 * `sampleAgeMs` is the only thing that tells a frozen reading from a live one.
 */
export const STALE_AFTER_MS = 3000

/** Whole seconds since the daemon last sampled the hardware when that makes the reading stale, else
 *  null. Absent `sampleAgeMs` (an older daemon, a history row) is not evidence of staleness. */
export const staleSeconds = (t: Telemetry | null | undefined): number | null =>
  t?.sampleAgeMs != null && t.sampleAgeMs > STALE_AFTER_MS ? Math.round(t.sampleAgeMs / 1000) : null

/**
 * Whole seconds since the last successful poll when the daemon has stopped answering for longer than
 * STALE_AFTER_MS, else null. For a client that keeps its last good reading on a failed poll (the
 * overlay): that reading's `sampleAgeMs` was fresh when served and never ages on the client, so
 * `staleSeconds` alone would call a dead daemon's last numbers live (audit round 1, 2026-09-25).
 * Null before the first success — there is no reading on screen to mislabel.
 */
export const offlineSeconds = (lastOkMs: number | null, nowMs: number): number | null =>
  lastOkMs != null && nowMs - lastOkMs > STALE_AFTER_MS ? Math.round((nowMs - lastOkMs) / 1000) : null

/**
 * True when the daemon answered but has never read the hardware: `sampledAtMs` is PRESENT and null.
 * GET /telemetry serves that when the sampler's first read hangs, and `staleSeconds` reads its null
 * `sampleAgeMs` as "not stale" — so until audit round 3 (2026-09-24) the panel, the overlay and the
 * MCP tool all showed an all-null reading as live and current. Absent key (an older daemon, a history
 * row) is not evidence of anything, as with `staleSeconds`.
 */
export const unsampled = (t: Telemetry | null | undefined): boolean =>
  t != null && 'sampledAtMs' in t && t.sampledAtMs == null

/** What the TDP controls open on: the manual override in force, else the last write, else nothing. */
export const tdpInForce = (t: TdpInfo | null | undefined): number | null =>
  t == null ? null : (t.manualStapmW ?? t.stapmW)

/** The result of a TDP write made from this window, and when (browser clock, Unix ms). */
export interface TdpWrite { verified: boolean | null; atMs: number }

/**
 * Whether the TDP in force is verified: this window's own write result until a telemetry sample
 * taken AFTER that write arrives, then the daemon's `tdpVerified`. Audit round 3 (2026-09-24): the
 * badge was pinned to the last POST /tdp for the life of the page, so when the 30 s reassert later
 * found the firmware had reverted the limit and could not hold it — or the guardian took over — it
 * still said "verified". Without `sampledAtMs` (an older daemon) the write result stands, as before.
 */
export const tdpVerifiedNow = (tele: Telemetry | null | undefined, write: TdpWrite | null): boolean | null => {
  if (write && (tele?.sampledAtMs == null || tele.sampledAtMs < write.atMs)) return write.verified
  return tele?.tdpVerified ?? write?.verified ?? null
}

/** How long the TDP controls wait before asking again when nothing said what is in force. */
export const TDP_SEED_RETRY_MS = 2000

export interface Shared {
  tele: Telemetry | null
  active: ModeId
  auto: boolean
  setAuto: (v: boolean) => void
  /** Resolves true once the daemon accepted the mode, false if POST /mode failed. */
  pickMode: (id: ModeId) => Promise<boolean>
}
