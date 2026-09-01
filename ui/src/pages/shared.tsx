// GPD Forge UI — shared page constants and types. GPL-3.0-or-later.
import type { Mode, ModeId, Telemetry } from '../types'

export const MODES: Mode[] = [
  { id: 'gaming',  label: 'Gaming',        icon: '🎮', blurb: 'Auto-TDP to target FPS, reactive fan, OSD.' },
  { id: 'gaming-battery', label: 'Gaming (battery)', icon: '🎮', blurb: 'Frame-capped at 45 and cooler — the longest session away from a charger.' },
  { id: 'ai',      label: 'Agents / AI',   icon: '🤖', blurb: 'Sustained CPU, VRAM/UMA, anti-standby, local API.' },
  { id: 'windows', label: 'Windows',       icon: '🪟', blurb: 'Balanced power, quiet fan, hotkeys.' },
  { id: 'battery', label: 'Battery',       icon: '🔋', blurb: 'Low TDP floor, longest runtime.' },
  { id: 'standby', label: 'Standby Doctor',icon: '🩺', blurb: 'Restore TDP+fan+HID on resume, fix drain.' },
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

export interface Shared {
  tele: Telemetry | null
  active: ModeId
  auto: boolean
  setAuto: (v: boolean) => void
  pickMode: (id: ModeId) => void
}
