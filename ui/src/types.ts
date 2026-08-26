// GPD Forge UI — shared types (mirror of core/Telemetry/TelemetrySnapshot). GPL-3.0-or-later.

export type ModeId = 'gaming' | 'ai' | 'windows' | 'battery' | 'standby'

export interface Mode {
  id: ModeId
  label: string
  icon: string
  blurb: string
}

export interface Telemetry {
  cpuTempC: number
  gpuTempC: number
  packageW: number
  cpuClockMhz: number
  fanRpm: number
  fanDutyPct: number
  fps: number
  batteryPct: number
  dischargeW: number
  acConnected: boolean
  tdpVerified: boolean
}

// One recorded telemetry sample (mirror of core/History/HistorySample) — unixMs is when the daemon
// read it (Unix epoch ms, UTC), stamped by ForgeWorker. Fed by the ring buffer behind GET /history.
export interface HistorySample { unixMs: number; snap: Telemetry }
export interface HistoryResponse { samples: HistorySample[] }

export interface Preset { stapmW: number; fastW: number; slowW: number; tctlC: number }
export interface BatteryBudget { minutesRemaining: number | null; remainingWh: number; dischargeW: number; projections: { watts: number; minutes: number }[] }
export interface AutoFps { enabled: boolean; targetFps: number }
export interface Guardian {
  enabled: boolean; autoThrottle: boolean
  tempThrottleC: number; tempCriticalC: number; throttleFloorW: number
  batteryLowPct: number; batteryCriticalPct: number
  throttling: boolean; throttledToW: number | null
  lastAlert: string | null; lastSeverity: string
}

export interface AntiStandby { active: boolean; holders: number; manual: boolean }
export interface VramInfo { reportedMb: number; adapterName: string | null; available: boolean; advisory: string }
export interface AiInfo { antiStandby: AntiStandby; sustainedProfile: Preset; vram: VramInfo }

// One profile recovered from a MotionAssistant .ini file (mirror of core/Import/ImportedProfile).
export interface ImportedProfile { name: string; stapmW: number; fastW: number; slowW: number; tctlC: number }
export interface ImportResult { found: number; profiles: ImportedProfile[]; path: string }

// Per-power-source (AC vs battery) auto mode-switch config.
export interface PowerSourceConfig { enabled: boolean; onBatteryMode: string; onAcMode: string }

// Display domain extensions (mirror of core/Display/*.cs).
export interface RefreshRateInfo { current: number; supported: number[]; error: string | null }
export interface NightMode { on: boolean; warmth: number }
export interface TabletModeInfo { convertible: boolean | null; raw: number | null; applied: boolean; advisory: string }
export interface KeyboardBacklightInfo { controllable: boolean; applied: boolean; advisory: string }

// Full settings snapshot (mirror of GET /settings/export).
export interface SettingsExport {
  modePresets: Record<string, Preset>
  guardian: Guardian
  fanMode: string
  brightness: number | null
  powerSource: PowerSourceConfig
  autoFps: AutoFps
}

// Auto-tuner (mirror of core/Tuner/AutoTuner.cs + TunerState.cs).
export type TuneGoal = 'MaxFps' | 'BestEfficiency' | 'HoldTarget'
export interface TunePoint { stapmW: number; fps: number; tempC: number }
export interface TuneResult { stapmW: number; fps: number; tempC: number; note: string }
export interface TunerInfo {
  running: boolean
  goal: TuneGoal
  targetFps: number | null
  minW: number
  maxW: number
  tempCapC: number
  currentStapmW: number
  points: TunePoint[]
  best: TuneResult | null
  note: string | null
}

// Update checker (mirror of GET /update/check).
export interface UpdateCheck { current: string; latest: string | null; updateAvailable: boolean; url: string | null }

export type JobStatus = 'queued' | 'running' | 'done' | 'blocked'

export interface Job {
  id: string
  cmd: string
  status: JobStatus
  constraints?: { requireAC?: boolean; maxTempC?: number; window?: string }
}

export interface Standby {
  lastDrainPctPerHour: number
  topWakeReason: string
  blockers: string[]
  lastRestore: string[] | null
}
