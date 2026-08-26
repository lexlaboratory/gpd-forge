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

export interface Preset { stapmW: number; fastW: number; slowW: number; tctlC: number }
export interface BatteryBudget { minutesRemaining: number | null; remainingWh: number; dischargeW: number; projections: { watts: number; minutes: number }[] }
export interface AutoFps { enabled: boolean; targetFps: number }

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
