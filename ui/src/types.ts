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
  /** 1% low: the mean of the slowest 1% of frames, as FPS. The number that tracks stutter. */
  fps1PctLow: number
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

// Fan mode + gated manual duty (mirror of GET/POST /fan — core/Fan/GpdFanController.cs).
// `controllable` is true only when the daemon is actually gated to WRITE the EC right now.
export interface FanInfo { mode: string; manualDuty: number; controllable: boolean }

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

// Advanced (hardware-gated): LED/RGB, battery charge limit, undervolt/Curve Optimizer (mirror of
// core/Led, core/Battery/ChargeLimit*.cs, core/Undervolt/*.cs). All three are ADVISORY — real
// validators run and the desired state is stored for real, but a write is only ever attempted when
// the daemon's hardware gate is open, and even then this HX370 has no working write path for any of
// them yet; see docs/api.md.
export type LedMode = 'Off' | 'Solid' | 'Breathe' | 'Rotate'
export interface LedInfo { mode: LedMode; color: string; controllable: boolean; applied: boolean; advisory: string }
export interface ChargeLimitInfo { percent: number; available: boolean; applied: boolean; advisory: string }
export interface UndervoltInfo { coCount: number; offsetMv: number; applied: boolean; advisory: string }

export type JobStatus = 'queued' | 'running' | 'done' | 'blocked'

export interface Job {
  id: string
  cmd: string
  status: JobStatus
  constraints?: { requireAC?: boolean; maxTempC?: number; window?: string }
}

// Standby Doctor. Every field is nullable on purpose: this endpoint used to answer with invented
// literals, and the replacement reports "not measured" rather than a plausible number. `blockers`
// being empty only means anything when `diagnosticsAvailable` is true — powercfg refusing to run is
// not the same as there being no blockers.
export interface StandbyRestoreStep { name: string; restored: boolean; detail: string }
export interface StandbyRestoreOutcome { at: string; steps: StandbyRestoreStep[]; anyRestored: boolean }
export interface Standby {
  /** %/h. Null until a real suspend on battery has been observed end to end. */
  lastDrainPctPerHour: number | null
  lastDrainSleptHours: number | null
  lastDrainAt: string | null
  topWakeReason: string | null
  blockers: string[]
  diagnosticsAvailable: boolean
  diagnosticsError: string | null
  lastRestore: StandbyRestoreOutcome | null
  /**
   * Findings from `powercfg /sleepstudy`, sampled on a slow cadence by the daemon — the report costs
   * tens of seconds and ~9 MB, so it is never generated on the request path.
   *
   * Three states, and they must not be collapsed: `sleepStudy` null with `sleepStudyError` null
   * means the sampler has not run yet; `sleepStudyError` set means powercfg refused (it needs an
   * elevated session); a summary with an empty `findings` means it ran and found nothing.
   */
  sleepStudy: SleepStudySummary | null
  sleepStudyError: string | null
}

export type SleepStudyKind = 'failed-resume' | 'bugcheck' | 'worst-drain'
export interface SleepStudyFinding { kind: SleepStudyKind | string; at: string; detail: string }
export interface SleepStudySummary {
  measuredAt: string
  sessions: number
  findings: SleepStudyFinding[]
}

// GET /health — the daemon's identity. Exposed since the first release and never consumed by the
// UI, so the app could not tell you which build it was talking to or which board it had detected.
export interface DaemonHealth { ok: boolean; version: string; model: string }

// System health check / anomaly detection (mirror of core/Health/HealthCheck.cs).
export type HealthLevel = 'warn' | 'critical'
export interface HealthIssue { level: HealthLevel; code: string; message: string }
export interface HealthReport { status: 'ok' | 'warn' | 'critical'; issues: HealthIssue[] }

// Panic cool (mirror of POST /panic).
export interface PanicResult { applied: boolean; stapmW: number }

// First-run setup wizard: incumbent power-controller check (mirror of GET /system/incumbents).
export interface IncumbentsInfo { motionAssistant: boolean; gpdTool: boolean }

export type AlertSeverity = 'Info' | 'Aviso' | 'Critica'
export type AlertCategory = 'Thermal' | 'Hardware' | 'Service' | 'Configuration' | 'System'
export interface AlertEvent {
  id: string; timestampUtc: string; severity: AlertSeverity; category: AlertCategory
  title: string; message: string; technicalData?: string | null; acknowledged: boolean; dedupeKey?: string | null
  /** How many times this condition fired. A continuous phenomenon is one alert, not sixty rows. */
  count?: number
  /** `timestampUtc` is the first occurrence; this is the most recent one. */
  lastSeenUtc?: string
}
export interface AlertSummary {
  unread: number; unreadInfo: number; unreadAviso: number; unreadCritica: number; latest: AlertEvent | null
  /** Sum of every unread alert's count — collapsing rows must not hide how insistent a fault was. */
  unreadOccurrences?: number
}

// --- per-app profile rules (mirror of core/Profiles/AppRule.cs, GET/POST /app-rules) -------------
/** A rule claims a process for a mode. Precedence is list order: first ENABLED match wins. */
export interface AppRule { id: string; match: string; mode: ModeId; enabled: boolean }
/** What decided the mode on the daemon's most recent foreground tick. `ruleId` is null when nothing
 *  matched and the mode came from the AC/battery fallback — the UI must be able to say which. */
export interface AppRuleMatch {
  ruleId: string | null
  match: string | null
  mode: ModeId
  process: string | null
  acConnected: boolean
  atUtc: string
}
/** Every /app-rules response, including mutations: the whole ruleset, so the list cannot drift. */
export interface AppRulesInfo {
  rules: AppRule[]
  modes: ModeId[]
  autoProfiles: boolean
  lastMatch: AppRuleMatch | null
}

// --- play sessions (mirror of core/Sessions/SessionModels.cs, GET /sessions) ---------------------
// Nearly every metric is nullable because every sensor behind it is optional on this hardware. Null
// means "not measured" and is rendered as such — never as a zero.
export interface GameSession {
  id: string
  app: string
  startedUtc: string
  endedUtc: string
  durationSeconds: number
  samples: number
  /** Ticks where the app was presenting but the probe produced no aggregate — qualifies the averages. */
  samplesWithoutFps: number
  fpsAvg: number | null
  fps1PctLow: number | null
  fpsMax: number | null
  cpuTempAvgC: number | null
  cpuTempMaxC: number | null
  packageAvgW: number | null
  /** True only when the session ran entirely on battery; otherwise the drain figure is meaningless. */
  onBattery: boolean
  batteryStartPct: number | null
  batteryEndPct: number | null
  batteryUsedPct: number | null
  fpsTrend: number[]
}

export interface GameSummary {
  app: string
  sessions: number
  totalSeconds: number
  lastPlayedUtc: string
  fpsAvg: number | null
  fpsBest: number | null
  fps1PctLow: number | null
  cpuTempMaxC: number | null
}

export interface SessionsResponse {
  /** False when no frame-rate probe is registered: nothing can ever be recorded, and we say so. */
  fpsAvailable: boolean
  current: string | null
  sessions: GameSession[]
}

export interface GamesResponse { fpsAvailable: boolean; games: GameSummary[] }
