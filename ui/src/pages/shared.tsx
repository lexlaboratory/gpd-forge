// GPD Forge UI — shared page constants and types. GPL-3.0-or-later.
import type { Mode, ModeId, Telemetry } from '../types'

export const MODES: Mode[] = [
  { id: 'gaming',  label: 'Gaming',        icon: '🎮', blurb: 'Auto-TDP to target FPS, reactive fan, OSD.' },
  { id: 'ai',      label: 'Agents / AI',   icon: '🤖', blurb: 'Sustained CPU, VRAM/UMA, anti-standby, local API.' },
  { id: 'windows', label: 'Windows',       icon: '🪟', blurb: 'Balanced power, quiet fan, hotkeys.' },
  { id: 'battery', label: 'Battery',       icon: '🔋', blurb: 'Low TDP floor, longest runtime.' },
  { id: 'standby', label: 'Standby Doctor',icon: '🩺', blurb: 'Restore TDP+fan+HID on resume, fix drain.' },
]

// Short, correctly-cased chip labels for the preset keys (so 'ai' shows as 'AI', not 'Ai').
export const PRESET_LABEL: Record<string, string> = {
  battery: 'Battery', windows: 'Windows', gaming: 'Gaming', ai: 'AI', standby: 'Standby',
}

export interface Shared {
  tele: Telemetry | null
  active: ModeId
  auto: boolean
  setAuto: (v: boolean) => void
  pickMode: (id: ModeId) => void
}
