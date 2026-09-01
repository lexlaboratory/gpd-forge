// GPD Forge UI — shared types (mirror of core/Telemetry/TelemetrySnapshot). GPL-3.0-or-later.

// Mirrors core/Profiles/Modes.cs. ModeCatalogueTests fails the build if this union falls behind the
// C# catalogue — a union that lags means the UI rejects a mode the daemon reports, silently.
export type ModeId = 'gaming' | 'gaming-battery' | 'ai' | 'windows' | 'battery' | 'standby'

export interface Mode {
  id: ModeId
  label: string
  icon: string
  blurb: string
}

/**
 * A telemetry reading. Every sensor is nullable as of 2026-09-01.
 *
 * Before that, an unreadable sensor arrived as 0, and with the hardware gate closed the daemon
 * reported a CPU at 0 °C and a fan at 0 rpm. The UI had no way to tell "cold" from "unmeasured", so
 * it rendered a confident, wrong number. Null means "no reading" and must render as `--`; a real
 * zero (nothing presenting frames, nothing discharging on AC) still arrives as 0 and is worth
 * showing.
 */
export interface Telemetry {
  cpuTempC: number | null
  gpuTempC: number | null
  packageW: number | null
  cpuClockMhz: number | null
  fanRpm: number | null
  fanDutyPct: number | null
  fps: number | null
  /** 1% low: the mean of the slowest 1% of frames, as FPS. The number that tracks stutter. */
  fps1PctLow: number | null
  batteryPct: number | null
  dischargeW: number | null
  acConnected: boolean
  tdpVerified: boolean
}

// One recorded telemetry sample (mirror of core/History/HistorySample) — unixMs is when the daemon
// read it (Unix epoch ms, UTC), stamped by ForgeWorker. Fed by the ring buffer behind GET /history.
export interface HistorySample { unixMs: number; snap: Telemetry }
export interface HistoryResponse { samples: HistorySample[] }

export interface Preset { stapmW: number; fastW: number; slowW: number; tctlC: number }
export interface BatteryBudget { minutesRemaining: number | null; remainingWh: number; dischargeW: number; projections: { watts: number; minutes: number }[] }

/**
 * The charge guard. `canStopCharging` is always false and is part of the contract rather than an
 * omission: without it a client could reasonably build a "stop at 80 %" switch for a capability this
 * board does not expose.
 */
export interface ChargeGuard {
  enabled: boolean
  highSocPct: number
  alertAfterHours: number
  coolWhileCharging: boolean
  coolToW: number
  totalHoursAtHighSoc: number
  episodes: number
  episodeStartedUtc: string | null
  /** Null when no episode is running — never 0, which would read as one that just began. */
  episodeHours: number | null
  canStopCharging: boolean
  advisory: string
}

/**
 * Battery health. Nearly every field is nullable and that is the design, not laxness: on this board
 * the EC does not report cycle count (it returns 0, which would read as an unused pack next to a
 * health figure of 91 %) and exposes no cell temperature at all. Each null carries its own reason
 * string so the UI can say WHY a value is missing instead of rendering a blank that looks broken.
 */
export interface BatteryHealth {
  designedMwh: number | null
  fullChargeMwh: number | null
  healthPercent: number | null
  cycleCount: number | null
  cycleCountUnavailable: string | null
  cellTemperatureC: number | null
  cellTemperatureUnavailable: string | null
  chemistry: string | null
  unavailable: string | null
  /** Percentage points lost between the oldest and newest sample; null until two days of history. */
  degradationPoints: number | null
  trendUnavailable: string | null
  samples: { atUtc: string; fullChargeMwh: number | null; healthPercent: number | null }[]
}
export interface AutoFps { enabled: boolean; targetFps: number }
export interface Guardian {
  enabled: boolean; autoThrottle: boolean
  tempThrottleC: number; tempCriticalC: number; throttleFloorW: number
  batteryLowPct: number; batteryCriticalPct: number
  throttling: boolean; throttledToW: number | null
  lastAlert: string | null; lastSeverity: string
}

export interface AntiStandby { active: boolean; holders: number; manual: boolean }

// The confirmation half of P3.3. `rebootConfirmed: false` means we could not ESTABLISH that a reboot
// happened between the two readings — not that none did. Render the summary, never re-derive a verdict
// from the numbers here: the reported MB saturates at the uint32 4095/4096 MB ceiling, so a delta
// involving that value is not a measurement of the split.
export interface VramHistoryInfo {
  kind: string; summary: string
  previousMb: number | null; sinceUtc: string | null; bootUtc: string | null
  rebootConfirmed: boolean
}
export interface VramInfo {
  reportedMb: number; adapterName: string | null; available: boolean; advisory: string
  history?: VramHistoryInfo
}

// One process currently earning the inference keep-awake hold. `cpuFraction` is a fraction of the
// WHOLE machine's CPU capacity (not one core) and is null when the last tick produced no usable
// measurement — show "—" for null, never 0, which would read as "idle" when the truth is "unknown".
export interface InferenceWorker { pid: number; name: string; cpuFraction: number | null; busySince: string }

// A watched process we could NOT read — not one that is idle. An unelevated daemon cannot read an
// elevated ollama's CPU time, and without this the panel would confidently report "no inference work".
export interface UnmeasuredProcess { name: string; pid: number | null; why: string }

// `enforcing` false is the shipped default: the worker observes and publishes what it WOULD hold for
// without actually holding, until GPDFORGE_INFERENCE_HOLD=1.
export interface InferenceHold {
  enforcing: boolean; holding: boolean; holdingSince: string | null
  lastTickAt?: string | null; reason?: string | null
  watchedNames?: string[]; busyCpuFraction?: number
  workers: InferenceWorker[]
  unmeasured?: UnmeasuredProcess[]
}

export interface AiInfo {
  antiStandby: AntiStandby; sustainedProfile: Preset; vram: VramInfo
  inferenceHold: InferenceHold
}

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

// What the daemon build actually IS, read from its assembly rather than typed into a literal.
// `commit` and `builtUtc` are null when the build did not record them — unknown must render as
// unknown, because the entire value of these fields is that they can be trusted. `builtUtc` is the
// field that answers "is the thing running older than the fix?" without archaeology on a binary.
export interface DaemonVersion {
  version: string
  commit: string | null
  builtUtc: string | null
  runtime: string
  model: string
}

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

// --- AMD GPU profiles (ADLX) -------------------------------------------------------------------
// One Radeon feature as the driver reports it. A feature that could not be QUERIED comes back as
// null in GpuSettings — which is not the same fact as `supported: false` (this GPU cannot do it) or
// `enabled: false` (it can, and it is off). Render the three differently or do not render at all.
export interface GpuFeature { supported: boolean; enabled: boolean; value: number | null }

export interface GpuSettings {
  antiLag: GpuFeature | null
  chill: GpuFeature | null
  boost: GpuFeature | null
  imageSharpening: GpuFeature | null
  frameRateCap: GpuFeature | null
}

// What a mode will do to the GPU when it becomes active.
export interface GpuModeProfile { name: string; antiLag: boolean; chill: boolean; boost: boolean }

// `available: false` carries only status/detail — there is nothing else true to send. The panel hides
// itself entirely in that case rather than greying out: a disabled row still reads as "nearly
// working" when the honest answer is "this machine cannot" or "you have not switched it on".
export interface GpuInfo {
  available: boolean
  status: string
  detail: string
  adapter: string | null
  adlxVersion?: string | null
  // When the user-session agent last checked in. ADLX cannot be reached from the session-0 service,
  // so everything here is second-hand: null means no agent has ever reported, which is a different
  // answer from "the agent said the GPU is unavailable".
  lastReportUtc?: string | null
  settings?: GpuSettings | null
  modeProfiles?: Record<string, GpuModeProfile>
}
