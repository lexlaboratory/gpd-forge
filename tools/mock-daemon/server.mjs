#!/usr/bin/env node
// GPD Forge — mock daemon. GPL-3.0-or-later.
//
// Zero-dependency Node HTTP server implementing docs/api.md. It exists so the UI and the
// E2E tests can run against the REAL API contract before the C# service exists. The C#
// `core/Api/` must match this behavior. Not for production — no auth, in-memory state.
//
// Run: node tools/mock-daemon/server.mjs   (PORT env, default 8787)

import http from 'node:http'

const PORT = Number(process.env.PORT ?? 8787)
const VERSION = '0.1.0-mock'
const MODEL = 'GPD Win 4 (G1618-04) · Ryzen AI 9 HX 370'

const MODES = new Set(['gaming', 'ai', 'windows', 'battery', 'standby'])
const PROFILES = [
  { id: 'battery', label: 'Battery', stapmW: 8, fastW: 12, slowW: 10, tctlC: 90 },
  { id: 'windows', label: 'Windows', stapmW: 15, fastW: 20, slowW: 17, tctlC: 92 },
  { id: 'gaming', label: 'Gaming', stapmW: 25, fastW: 33, slowW: 28, tctlC: 95 },
  { id: 'ai', label: 'Agents / AI', stapmW: 25, fastW: 25, slowW: 25, tctlC: 90 },
  { id: 'standby', label: 'Standby Doctor', stapmW: 15, fastW: 20, slowW: 17, tctlC: 92 },
]

const TDP_MIN = 5
const TDP_MAX = 35
const TDP_FIRMWARE_CAP = 30 // above this the "firmware" reverts → verified:false

// --- per-app profile rules (mirrors core/Profiles/) ----------------------------------------------
// The modes a rule may select. "standby" is deliberately absent: it is a preset for a system state,
// and a foreground app that could put the machine into standby mode would be a trap.
const RULE_MODES = ['battery', 'windows', 'gaming', 'ai']
const MAX_MATCH_LENGTH = 120
const DEFAULT_APP_RULES = [
  ['ai', ['ollama', 'lmstudio', 'lm studio', 'koboldcpp', 'jan', 'gpt4all', 'text-generation', 'comfyui']],
  ['gaming', ['steam', 'gamescope', 'retroarch', 'rpcs3', 'cemu', 'yuzu', 'ryujinx', 'dolphin', 'pcsx2', 'duckstation']],
]
let ruleSeq = 0

/** Mirrors GpdForge.Profiles.AppRulePolicy.Normalize: trimmed, lowercase, no ".exe" tail. */
function normalizeMatch(value) {
  if (typeof value !== 'string') return ''
  let p = value.trim().toLowerCase()
  if (p.endsWith('.exe')) p = p.slice(0, -4)
  return p.trim()
}

/** Mirrors AppRulePolicy.Validate — same order, same wording. Null when the rule is acceptable; the
 *  string is shown to the user verbatim, so it must read as a sentence, not as a code. */
function validateRule(match, mode, existing, excludingId) {
  const needle = normalizeMatch(match)
  if (needle.length === 0) return 'A rule needs a process name to match.'
  if (needle.length > MAX_MATCH_LENGTH) return `Process name is too long (max ${MAX_MATCH_LENGTH} characters).`
  if (!RULE_MODES.includes(mode)) return `Unknown mode '${mode}'. Valid modes: ${RULE_MODES.join(', ')}.`
  if (existing.some((r) => r.id !== excludingId && r.match === needle)) return `A rule for '${needle}' already exists.`
  return null
}

// --- play sessions (mirrors core/Sessions/) -------------------------------------------------------
/** A plausible frame-rate trend: a slow drift around the session's average with a couple of dips,
 *  deterministic so repeated reads (and screenshots) are stable. */
function trend(avg, points) {
  return Array.from({ length: points }, (_, i) =>
    Math.round((avg + Math.sin(i / 3.1) * 4 - (i % 17 === 0 ? 9 : 0)) * 10) / 10)
}

const HOUR = 3_600_000
const NOW = Date.now()
const SAMPLE_SESSIONS = [
  {
    // Frame probe never produced a reading for this one (PresentMon dropped out): every FPS field is
    // null and the trend is empty, so the UI has to take the "no reading" path rather than show 0.
    id: '8f1c0a4e-2b77-4e59-9a10-0d4f6c3e51aa',
    app: 'hades2',
    startedUtc: new Date(NOW - 5 * HOUR).toISOString(),
    endedUtc: new Date(NOW - 4.5 * HOUR).toISOString(),
    durationSeconds: 1800, samples: 1800, samplesWithoutFps: 1800,
    fpsAvg: null, fps1PctLow: null, fpsMax: null,
    cpuTempAvgC: 64.2, cpuTempMaxC: 72.8, packageAvgW: 15.1,
    onBattery: true, batteryStartPct: 88, batteryEndPct: 61, batteryUsedPct: 27,
    fpsTrend: [],
  },
  {
    // Plugged in for at least part of its life, so there is no meaningful drain figure: onBattery is
    // false and every battery field is null rather than 0.
    id: 'c2d9b3f1-6a04-42d7-8c55-1b9e7a20d3c4',
    app: 'cyberpunk2077',
    startedUtc: new Date(NOW - 26 * HOUR).toISOString(),
    endedUtc: new Date(NOW - 25 * HOUR).toISOString(),
    durationSeconds: 3600, samples: 3600, samplesWithoutFps: 0,
    fpsAvg: 61.8, fps1PctLow: 44.2, fpsMax: 78.9,
    cpuTempAvgC: 81, cpuTempMaxC: 94.2, packageAvgW: 31.4,
    onBattery: false, batteryStartPct: null, batteryEndPct: null, batteryUsedPct: null,
    fpsTrend: trend(61.8, 96),
  },
  {
    // Ran entirely on battery: the one shape where a drain figure means anything.
    id: '5a7e91d0-38c2-4f6b-b1e3-9c0d2f847b16',
    app: 'cyberpunk2077',
    startedUtc: new Date(NOW - 52 * HOUR).toISOString(),
    endedUtc: new Date(NOW - 50.5 * HOUR).toISOString(),
    durationSeconds: 5400, samples: 5400, samplesWithoutFps: 120,
    fpsAvg: 52.4, fps1PctLow: 38.1, fpsMax: 71.2,
    cpuTempAvgC: 78.3, cpuTempMaxC: 91.5, packageAvgW: 24.6,
    onBattery: true, batteryStartPct: 96, batteryEndPct: 31, batteryUsedPct: 65,
    fpsTrend: trend(52.4, 120),
  },
]

const state = {
  activeMode: 'windows',
  stapmW: 20,
  tdpVerified: true,
  acConnected: false,
  batteryPct: 78,
  jobs: new Map(),
  jobSeq: 0,
  brightness: 70,
  refresh: { current: 60, supported: [48, 60] },
  night: { on: false, warmth: 0 },
  tablet: { raw: null }, // null = ConvertibilityEnabled not set (default OS chassis detection)
  // Advanced (hardware-gated): LED/RGB, battery charge limit, undervolt/Curve Optimizer. The mock
  // presents all three as controllable/available so the UI/E2E can exercise a full round-trip —
  // the real daemon (see core/Led, core/Battery, core/Undervolt) stays honestly gated/advisory.
  led: { mode: 'Off', color: '#00c8ff' },
  chargeLimit: { percent: 100 },
  undervolt: { coCount: 0, offsetMv: 0 },
  fanMode: 'Auto',
  fanManualDuty: 128,
  frozen: [],
  history: [], // { unixMs, snap } ring, capped at HISTORY_CAPACITY — see pushHistory()
  autoFps: { enabled: false, targetFps: 60 },
  guardian: { enabled: true, autoThrottle: true, tempThrottleC: 90, tempCriticalC: 96, throttleFloorW: 12, batteryLowPct: 15, batteryCriticalPct: 8 },
  alerts: [],
  // Per-app profile rules — seeded from the SAME default ruleset the real daemon flattens on a fresh
  // install (core/Profiles/ModeRules.DefaultRuleSet), in the same precedence order.
  appRules: DEFAULT_APP_RULES.flatMap(([mode, needles]) =>
    needles.map((match) => ({ id: `rule-${++ruleSeq}`, match, mode, enabled: true }))),
  // Play sessions, newest first. Deliberately covers all three shapes the UI must render honestly:
  // a full battery run, a plugged-in run whose battery fields are null, and a run the frame probe
  // never produced a reading for (fpsAvg/fps1PctLow/fpsMax null, fpsTrend []).
  sessions: SAMPLE_SESSIONS,
  powerSource:{ enabled: false, onBatteryMode: 'battery', onAcMode: 'windows' },
  // Canned MotionAssistant import result — enough for the UI/E2E to exercise the flow without a
  // real MotionAssistant install.
  motionAssistantProfiles: [
    { name: 'Gaming', stapmW: 25, fastW: 33, slowW: 28, tctlC: 95 },
    { name: 'Silent', stapmW: 10, fastW: 15, slowW: 12, tctlC: 85 },
  ],
  ai: {
    manualAntiStandby: false, vramMb: 512, adapterName: 'AMD Radeon 890M',
    // Inference keep-awake: shipped default is observe-only with nothing working. `holdingSince` and
    // `lastTickAt` stay null until there is something real to report — a mock that invents a plausible
    // timestamp trains the UI to never handle the null it will actually get on a fresh boot.
    inferenceEnforcing: false, inferenceWorkers: [], inferenceHoldingSince: null, inferenceLastTickAt: null,
    inferenceUnmeasured: [],
  },
  presets: {
    battery: { stapmW: 8, fastW: 12, slowW: 10, tctlC: 90 },
    windows: { stapmW: 15, fastW: 20, slowW: 17, tctlC: 92 },
    gaming:  { stapmW: 25, fastW: 33, slowW: 28, tctlC: 95 },
    ai:      { stapmW: 25, fastW: 25, slowW: 25, tctlC: 90 },
    standby: { stapmW: 15, fastW: 20, slowW: 17, tctlC: 92 },
  },
  // Shape must match core/Standby/StandbyService.cs exactly. The mock diverging from the real
  // daemon is not a theoretical risk here: an enum serialised as a number instead of a name once
  // blanked the whole app while every E2E stayed green.
  standby: {
    lastDrainPctPerHour: 6.2,
    lastDrainSleptHours: 7.5,
    lastDrainAt: new Date(Date.now() - 3_600_000).toISOString(),
    topWakeReason: 'Fingerprint device (Win 4)',
    blockers: ['GPDKeyboard.exe'],
    diagnosticsAvailable: true,
    diagnosticsError: null,
    lastRestore: null,
    // The findings the real daemon's SleepStudyWorker caches. Modelled on what the reference Win 4
    // actually produced: a hibernation it never came back from, and a DPC watchdog bugcheck.
    sleepStudy: {
      measuredAt: new Date(Date.now() - 1_800_000).toISOString(),
      sessions: 120,
      findings: [
        {
          kind: 'failed-resume',
          at: new Date(Date.now() - 21_600_000).toISOString(),
          detail:
            'Hibernate lasting 5.0 h — the next thing the machine did was an abnormal shutdown, ' +
            'so it did not come back on its own.',
        },
        {
          kind: 'bugcheck',
          at: new Date(Date.now() - 172_800_000).toISOString(),
          detail: 'Bugcheck, stop code 0x133.',
        },
      ],
    },
    sleepStudyError: null,
  },
  // Auto-tuner: mirrors core/Tuner/TunerState.cs's shape. Unlike the real daemon (whose telemetry
  // has no FPS source yet — see the honesty note in docs/api.md), the mock simulates a small FPS
  // curve so POST /tuner/start can return a populated, usable sweep for the UI/E2E to exercise.
  tuner: {
    running: false, goal: 'MaxFps', targetFps: null, minW: 8, maxW: 30, tempCapC: 95,
    currentStapmW: 8, points: [], best: null, note: null,
  },
}

const TUNE_GOALS = new Set(['MaxFps', 'BestEfficiency', 'HoldTarget'])

/** Mirrors GpdForge.Tuner.AutoTuner.PickBest: same goals, same temp-cap filter, same tie-breaks. */
function pickBestTune(points, goal, targetFps, tempCapC) {
  const underCap = points.filter((p) => p.tempC <= tempCapC)
  if (underCap.length === 0) return null

  const efficiency = (p) => (p.stapmW > 0 ? p.fps / p.stapmW : 0)

  if (goal === 'MaxFps') {
    const b = [...underCap].sort((x, y) => y.fps - x.fps || x.stapmW - y.stapmW)[0]
    return { stapmW: b.stapmW, fps: b.fps, tempC: b.tempC, note: `Highest FPS at or under the ${b.tempC}°C cap.` }
  }
  if (goal === 'BestEfficiency') {
    const b = [...underCap].sort((x, y) => efficiency(y) - efficiency(x) || x.stapmW - y.stapmW || y.fps - x.fps)[0]
    return { stapmW: b.stapmW, fps: b.fps, tempC: b.tempC, note: `Best FPS-per-watt (${efficiency(b).toFixed(2)} fps/W).` }
  }
  // HoldTarget
  if (targetFps == null) return null
  const candidates = underCap.filter((p) => p.fps >= targetFps)
  if (candidates.length === 0) return null
  const b = [...candidates].sort((x, y) => x.stapmW - y.stapmW || y.fps - x.fps)[0]
  return { stapmW: b.stapmW, fps: b.fps, tempC: b.tempC, note: `Lowest watts holding ≥${targetFps} fps.` }
}

/** Canned sweep: a monotonic-ish fps/temp-vs-watts curve, jittered like telemetry() so repeated
 * sweeps aren't bit-identical. Purely for exercising the UI/E2E — the real daemon sweeps real TDP. */
function simulateTuneSweep(minW, maxW, tempCapC) {
  const jitter = (base, amp) => Math.round((base + (Math.random() - 0.5) * amp) * 10) / 10
  const points = []
  for (let w = minW; w <= maxW; w += 2) {
    points.push({ stapmW: w, fps: jitter(20 + w * 2.4, 3), tempC: jitter(55 + w * 1.3, 2) })
  }
  return points
}

function telemetry() {
  const jitter = (base, amp) => Math.round((base + (Math.random() - 0.5) * amp) * 10) / 10
  return {
    cpuTempC: jitter(61, 4),
    gpuTempC: jitter(58, 4),
    packageW: jitter(state.stapmW, 2),
    cpuClockMhz: Math.round(jitter(3300, 200)),
    fanRpm: Math.round(jitter(3600, 300)),
    fanDutyPct: 45,
    fps: Math.round(jitter(60, 3)),
    fps1PctLow: Math.round(jitter(48, 4)), // always below the mean, as a real 1% low is
    batteryPct: state.batteryPct,
    dischargeW: jitter(18, 2),
    acConnected: state.acConnected,
    tdpVerified: state.tdpVerified,
  }
}

// Telemetry history: a small in-memory ring, mirroring GpdForge.History.TelemetryHistory (core/History/).
// The real daemon's worker appends once per tick; here we append once per GET /telemetry so the E2E
// (and manual dev use) always has data to show without a background timer.
const HISTORY_CAPACITY = 3600 // 1h at 1 sample/s, same mental model as the real ring buffer's default
function pushHistory(snap) {
  state.history.push({ unixMs: Date.now(), snap })
  if (state.history.length > HISTORY_CAPACITY) state.history.shift()
}

const CSV_HEADER = 'unixMs,isoTime,cpuTempC,gpuTempC,packageW,cpuClockMhz,fanRpm,fps,batteryPct,dischargeW,acConnected,tdpVerified'
/** Mirrors GpdForge.History.CsvExport.ToCsv: header + one row per sample, always header-terminated. */
function csvFromHistory(samples) {
  const rows = samples.map(({ unixMs, snap: t }) =>
    [unixMs, new Date(unixMs).toISOString(), t.cpuTempC, t.gpuTempC, t.packageW, t.cpuClockMhz, t.fanRpm, t.fps, t.batteryPct, t.dischargeW, t.acConnected, t.tdpVerified].join(','))
  return [CSV_HEADER, ...rows].join('\n') + '\n'
}

/** Simulate the closed loop: high requests get reverted by "firmware". */
function applyTdp(stapmW) {
  const requested = Math.round(stapmW)
  const observed = requested > TDP_FIRMWARE_CAP ? TDP_FIRMWARE_CAP : requested
  const verified = observed === requested
  state.stapmW = observed
  state.tdpVerified = verified
  return { requested, observed, verified }
}

function evalJob(job) {
  const t = telemetry()
  if (job.constraints?.requireAC && !t.acConnected) return 'blocked'
  if (job.constraints?.maxTempC != null && t.cpuTempC > job.constraints.maxTempC) return 'blocked'
  return 'running'
}

/** Ref-counted holders: every currently-"running" job + the manual toggle, mirroring the real
 *  daemon's AntiStandbyService (each running job holds a lock; SetThreadExecutionState only
 *  matters on the 0->1 / 1->0 edge, which the UI infers from `holders`). */
function aiHolders() {
  const runningJobs = [...state.jobs.values()].filter((j) => j.status === 'running').length
  return runningJobs + (state.ai.manualAntiStandby ? 1 : 0)
}

/** Mirrors GpdForge.Ai.ProfileShaper.Shape: flat stapm=fast=slow, clamped to the safe band. */
function shapeSustained(preset) {
  const w = Math.max(5, Math.min(40, preset.stapmW))
  const t = Math.max(60, Math.min(95, preset.tctlC))
  return { stapmW: w, fastW: w, slowW: w, tctlC: t }
}

const VRAM_ADVISORY =
  'UMA/VRAM size is set by the BIOS at boot (GOP/_DSM) and only changes after a reboot. ' +
  'GPD Forge reads the current allocation but will not write it blindly — change it in BIOS ' +
  'setup, or wait for a verified, reversible write path for this board.'

function vramInfo() {
  return {
    reportedMb: state.ai.vramMb, adapterName: state.ai.adapterName, available: true, advisory: VRAM_ADVISORY,
    // First observation is the honest default for a fresh install: there is no prior reading to
    // compare against, so previousMb is null and rebootConfirmed is false — false meaning "not
    // established", not "no reboot happened". Mirrors core/Ai/VramHistory.cs.
    history: {
      kind: 'FirstObservation',
      summary: `Baseline recorded: ${state.ai.vramMb} MB. A later change to the UMA split will be detected and reported here.`,
      previousMb: null, sinceUtc: null, bootUtc: null, rebootConfirmed: false,
    },
  }
}

// --- Display domain extensions: tablet mode (advisory/gated) + keyboard backlight (advisory) ---
// Mirrors core/Display/TabletModeAdvisor.cs and KeyboardBacklightAdvisor.cs.
const TABLET_GATE_CLOSED_ADVISORY =
  'Tablet-mode detection is a system-wide registry value (ConvertibilityEnabled) that changes how Windows treats every window on this PC, not just GPD Forge — set GPDFORGE_ENABLE_HARDWARE=1 to allow a write. Read-only until then.'
const KEYBOARD_BACKLIGHT_ADVISORY =
  "Keyboard backlight is controlled by the embedded controller (the same EC path already blocked on this board's firmware) or the Fn hotkey directly. GPD Forge has no verified write path for it yet, so this stays read-only/advisory."

function describeTablet(raw) {
  if (raw === null || raw === undefined)
    return "ConvertibilityEnabled is not set — Windows falls back to chassis-type/DeviceForm detection (the source of the Win 4's known 'everything opens maximized' behavior)."
  if (raw === 0) return 'ConvertibilityEnabled = 0 — Windows is told this is NOT convertible (the known fix).'
  return `ConvertibilityEnabled = ${raw} — Windows treats this as convertible/tablet-capable.`
}
function tabletInfo(applied, advisory) {
  const raw = state.tablet.raw
  return { convertible: raw === null || raw === undefined ? null : raw !== 0, raw, applied, advisory: advisory ?? describeTablet(raw) }
}

// --- Advanced (hardware-gated): LED/RGB, battery charge limit, undervolt/Curve Optimizer ---------
// Mirrors core/Led/LedService.cs, core/Battery/ChargeLimitService.cs, core/Undervolt/
// CurveOptimizerService.cs — except the mock reports everything as controllable/available/applied
// so the UI and E2E can exercise a full round-trip without real hardware. The real daemon is gated
// behind GPDFORGE_ENABLE_HARDWARE=1 and, even then, is honest that no working write path exists yet.
const LED_MODES = new Set(['Off', 'Solid', 'Breathe', 'Rotate'])
const LED_MOCK_ADVISORY =
  'Mock daemon: LED is presented as controllable so the UI/E2E can exercise the round-trip. The ' +
  "real daemon is gated behind GPDFORGE_ENABLE_HARDWARE=1 and, even then, this HX370's firmware has " +
  'no working HID write path yet.'
const CHARGE_LIMIT_MOCK_ADVISORY =
  'Mock daemon: charge limit is presented as available/controllable for UI/E2E. The real daemon is ' +
  'gated, and "stop charging at N%" is an EC/BIOS feature with no known driverless write path yet.'
const UNDERVOLT_MOCK_ADVISORY =
  'Mock daemon: undervolt is presented as applied for UI/E2E. The real daemon is gated, and ' +
  'RyzenAdj (its TDP backend) does not expose Curve Optimizer / PBO at all.'

function ledInfo() {
  return { mode: state.led.mode, color: state.led.color, controllable: true, applied: true, advisory: LED_MOCK_ADVISORY }
}
function normalizeHexColor(input) {
  if (typeof input !== 'string') return null
  const hex = input.startsWith('#') ? input.slice(1) : input
  return /^[0-9a-fA-F]{6}$/.test(hex) ? `#${hex.toLowerCase()}` : null
}
function chargeLimitInfo() {
  return { percent: state.chargeLimit.percent, available: true, applied: true, advisory: CHARGE_LIMIT_MOCK_ADVISORY }
}
function undervoltInfo() {
  return { coCount: state.undervolt.coCount, offsetMv: state.undervolt.offsetMv, applied: true, advisory: UNDERVOLT_MOCK_ADVISORY }
}

// Fan mode + manual duty (mirrors core/Fan/GpdFanController.cs + core/Program.cs's GET/POST /fan).
// The mock reports controllable:true unconditionally (like LED/ChargeLimit/Undervolt above) so the
// UI/E2E can exercise the manual-duty slider without real hardware; the real daemon only reports
// controllable:true when BOTH GPDFORGE_ENABLE_HARDWARE=1 AND GPDFORGE_ENABLE_FAN_CONTROL=1 (a second,
// separate opt-in — fan writes are gated more strictly than other hardware writes) and a matched board.
function fanInfo() {
  return { mode: state.fanMode, manualDuty: state.fanManualDuty, controllable: true }
}

// Per-app rules: every response (including mutations) carries the WHOLE ruleset plus the context the
// editor needs — the selectable modes and whether anything is actually applying them.
const MOCK_FOREGROUND = 'steam'
function lastRuleMatch() {
  const rule = state.appRules.find((r) => r.enabled && r.match.length > 0 && MOCK_FOREGROUND.includes(r.match))
  return {
    ruleId: rule?.id ?? null,
    match: rule?.match ?? null,
    // No rule matched → the mode is just the power-source default, and the UI must say so instead of
    // implying a rule is in charge. Disabling/deleting the `steam` rule exercises exactly that.
    mode: rule?.mode ?? (state.acConnected ? 'windows' : 'battery'),
    process: MOCK_FOREGROUND,
    acConnected: state.acConnected,
    atUtc: new Date().toISOString(),
  }
}
function appRulesInfo() {
  return { rules: state.appRules, modes: RULE_MODES, autoProfiles: true, lastMatch: lastRuleMatch() }
}

const round1 = (v) => Math.round(v * 10) / 10
/** Mirrors GpdForge.Sessions.SessionMath.PerGame: duration-weighted averages (a two-minute run must
 *  not drag a three-hour one around), nulls preserved, most-played first. */
function perGame(sessions) {
  const groups = new Map()
  for (const s of sessions) {
    const key = s.app.toLowerCase()
    if (!groups.has(key)) groups.set(key, [])
    groups.get(key).push(s)
  }
  const weighted = (rows, pick) => {
    let weight = 0, total = 0
    for (const s of rows) {
      const v = pick(s)
      if (v === null || v === undefined) continue
      const w = s.durationSeconds > 0 ? s.durationSeconds : 1
      weight += w
      total += v * w
    }
    return weight > 0 ? round1(total / weight) : null
  }
  const maxOrNull = (rows, pick) => {
    let best = null
    for (const s of rows) {
      const v = pick(s)
      if (v !== null && v !== undefined && (best === null || v > best)) best = v
    }
    return best === null ? null : round1(best)
  }
  return [...groups.values()]
    .map((rows) => ({
      app: rows[0].app,
      sessions: rows.length,
      totalSeconds: round1(rows.reduce((n, s) => n + s.durationSeconds, 0)),
      lastPlayedUtc: rows.map((s) => s.startedUtc).sort().at(-1),
      fpsAvg: weighted(rows, (s) => s.fpsAvg),
      fpsBest: maxOrNull(rows, (s) => s.fpsMax ?? s.fpsAvg),
      fps1PctLow: weighted(rows, (s) => s.fps1PctLow),
      cpuTempMaxC: maxOrNull(rows, (s) => s.cpuTempMaxC),
    }))
    .sort((a, b) => b.totalSeconds - a.totalSeconds || (a.lastPlayedUtc < b.lastPlayedUtc ? 1 : -1))
}

// The inference keep-awake, as the daemon reports it. Mirrors core/Ai/InferenceHoldStatus.
//
// This mock deliberately defaults to the SHIPPED default — enforcing:false, holding:false, no workers
// — rather than to a populated happy path. A mock that always returns the interesting case is how this
// repo shipped an app whose alert severities were numbers in production and strings in the mock: the
// contract was only ever tested against its own maquette. The null-carrying case is the common one, so
// it is the one the UI gets tested against.
function inferenceHold() {
  return {
    enforcing: state.ai.inferenceEnforcing,
    holding: state.ai.inferenceWorkers.length > 0,
    holdingSince: state.ai.inferenceWorkers.length > 0 ? state.ai.inferenceHoldingSince : null,
    lastTickAt: state.ai.inferenceLastTickAt,
    reason: state.ai.inferenceWorkers.length > 0
      ? `${state.ai.inferenceWorkers[0].name} (pid ${state.ai.inferenceWorkers[0].pid}) sustained CPU work`
      : 'no sustained inference work',
    watchedNames: ['ollama', 'ollama app', 'llama-server', 'llama-cli', 'LM Studio', 'koboldcpp', 'python', 'pythonw'],
    busyCpuFraction: 0.15,
    workers: state.ai.inferenceWorkers,
    // Watched processes we could not read. Distinct from "idle" on purpose — see core/Ai/InferenceActivity.cs.
    unmeasured: state.ai.inferenceUnmeasured,
  }
}

function aiInfo() {
  const holders = aiHolders()
  const h = inferenceHold()
  return {
    antiStandby: { active: holders > 0, holders, manual: state.ai.manualAntiStandby },
    sustainedProfile: shapeSustained(state.presets.ai),
    vram: vramInfo(),
    inferenceHold: { enforcing: h.enforcing, holding: h.holding, holdingSince: h.holdingSince, workers: h.workers, unmeasured: h.unmeasured },
  }
}

// --- tiny HTTP helpers ---
const CORS = {
  'access-control-allow-origin': '*',
  'access-control-allow-methods': 'GET,POST,PUT,DELETE,OPTIONS',
  'access-control-allow-headers': 'content-type,authorization',
}
function send(res, status, body) {
  res.writeHead(status, { 'content-type': 'application/json', ...CORS })
  res.end(JSON.stringify(body))
}
function err(res, status, code, message) {
  send(res, status, { error: { code, message } })
}
function readBody(req) {
  return new Promise((resolve) => {
    let data = ''
    req.on('data', (c) => (data += c))
    req.on('end', () => {
      try { resolve(data ? JSON.parse(data) : {}) } catch { resolve(null) }
    })
  })
}

const server = http.createServer(async (req, res) => {
  const { method } = req
  const url = new URL(req.url, `http://localhost:${PORT}`)
  const path = url.pathname

  if (method === 'OPTIONS') { res.writeHead(204, CORS); return res.end() }

  if (method === 'GET' && path === '/health') return send(res, 200, { ok: true, version: VERSION, model: MODEL })
  if (method === 'GET' && path === '/alerts') {
    const limit = Math.max(1, Math.min(500, Number(url.searchParams.get('limit')) || 100))
    const unread = url.searchParams.get('unreadOnly') === 'true'
    return send(res, 200, { alerts: state.alerts.filter((a) => !unread || !a.acknowledged).slice(0, limit) })
  }
  if (method === 'GET' && path === '/alerts/summary') {
    const unread = state.alerts.filter((a) => !a.acknowledged)
    return send(res, 200, {
      unread: unread.length,
      unreadInfo: unread.filter((a) => a.severity === 'Info').length,
      unreadAviso: unread.filter((a) => a.severity === 'Aviso').length,
      unreadCritica: unread.filter((a) => a.severity === 'Critica').length,
      // Collapsing repeats into one row must not hide how insistent a condition was.
      unreadOccurrences: unread.reduce((n, a) => n + (a.count ?? 1), 0),
      latest: state.alerts[0] ?? null,
    })
  }
  // TEST-ONLY. Not a route the real daemon has: the mock starts with no alerts so the empty state
  // can be asserted, and a spec that needs populated alerts seeds them here. Shape mirrors
  // core/Alerts/AlertModels.cs, `count`/`lastSeenUtc` included — the coalescing fields exist to be
  // shown, and a mock that omitted them would leave that display untested.
  if (method === 'POST' && path === '/alerts/_test-seed') {
    const body = await readBody(req)
    const now = Date.now()
    state.alerts = (body?.alerts ?? []).map((a, i) => ({
      id: `seed-${i}`,
      timestampUtc: new Date(now - (a.count ?? 1) * 60_000).toISOString(),
      lastSeenUtc: new Date(now).toISOString(),
      severity: a.severity ?? 'Aviso',
      category: a.category ?? 'Thermal',
      title: a.title ?? 'Thermal guardian',
      message: a.message ?? 'CPU 91°C — easing to 24 W',
      technicalData: a.technicalData ?? null,
      acknowledged: false,
      dedupeKey: a.dedupeKey ?? null,
      count: a.count ?? 1,
    }))
    return send(res, 200, { seeded: state.alerts.length })
  }
  if (method === 'POST' && path === '/alerts/ack-all') { const n = state.alerts.filter((a) => !a.acknowledged).length; state.alerts.forEach((a) => { a.acknowledged = true }); return send(res, 200, { acknowledged: n }) }
  if (method === 'POST' && path.match(/^\/alerts\/[^/]+\/ack$/)) { const a = state.alerts.find((x) => x.id === path.split('/')[2]); if (!a || a.acknowledged) return err(res, 404, 'not_found', 'alert not found or already acknowledged'); a.acknowledged = true; return send(res, 200, { acknowledged: true, id: a.id }) }
  if (method === 'DELETE' && path.startsWith('/alerts/')) { const id = path.slice('/alerts/'.length); const n = state.alerts.length; state.alerts = state.alerts.filter((a) => a.id !== id); return state.alerts.length < n ? send(res, 204, null) : err(res, 404, 'not_found', 'alert not found') }
  if (method === 'GET' && path === '/telemetry') {
    const t = telemetry()
    pushHistory(t)
    return send(res, 200, t)
  }
  if (method === 'GET' && path === '/mode') return send(res, 200, { active: state.activeMode })

  if (method === 'GET' && path === '/history') {
    const minutes = Math.max(1, Math.min(60, Number(url.searchParams.get('minutes')) || 5))
    const since = Date.now() - minutes * 60_000
    return send(res, 200, { samples: state.history.filter((s) => s.unixMs >= since) })
  }
  if (method === 'GET' && path === '/history/export.csv') {
    const csv = csvFromHistory(state.history)
    res.writeHead(200, {
      'content-type': 'text/csv',
      'content-disposition': 'attachment; filename="gpd-forge-telemetry.csv"',
      ...CORS,
    })
    return res.end(csv)
  }

  if (method === 'GET' && path === '/telemetry/stream') {
    res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache', connection: 'keep-alive', ...CORS })
    const id = setInterval(() => res.write(`data: ${JSON.stringify(telemetry())}\n\n`), 250)
    req.on('close', () => clearInterval(id))
    return
  }

  if (method === 'POST' && path === '/mode') {
    const body = await readBody(req)
    if (!body || !MODES.has(body.name)) return err(res, 400, 'bad_mode', 'unknown mode')
    state.activeMode = body.name
    const p = PROFILES.find((x) => x.id === body.name)
    if (p) applyTdp(p.stapmW)
    return send(res, 200, { active: state.activeMode })
  }

  if (method === 'POST' && path === '/tdp') {
    const body = await readBody(req)
    const w = Number(body?.stapmW)
    if (!Number.isFinite(w) || w < TDP_MIN || w > TDP_MAX) return err(res, 400, 'bad_tdp', `stapmW must be ${TDP_MIN}..${TDP_MAX}`)
    return send(res, 200, applyTdp(w))
  }

  // Panic cool — flat 8W floor + Aggressive fan. Mirrors core/Program.cs's POST /panic.
  if (method === 'POST' && path === '/panic') {
    const r = applyTdp(8)
    state.fanMode = 'Aggressive'
    return send(res, 200, { applied: r.verified, stapmW: 8 })
  }

  if (method === 'POST' && path === '/jobs') {
    const body = await readBody(req)
    if (!body?.cmd) return err(res, 400, 'bad_job', 'cmd required')
    const id = `job-${++state.jobSeq}`
    const job = { id, cmd: body.cmd, constraints: body.constraints ?? {}, log: [], status: 'queued' }
    job.status = evalJob(job)
    state.jobs.set(id, job)
    return send(res, 200, { id, status: job.status })
  }

  if (method === 'GET' && path === '/jobs') return send(res, 200, [...state.jobs.values()])
  if (method === 'GET' && path.startsWith('/jobs/')) {
    const job = state.jobs.get(path.slice('/jobs/'.length))
    return job ? send(res, 200, job) : err(res, 404, 'no_job', 'not found')
  }

  if (method === 'GET' && path === '/profiles') return send(res, 200, state.presets)
  if (method === 'POST' && path.startsWith('/profiles/')) {
    const mode = path.slice('/profiles/'.length)
    const body = await readBody(req)
    if (!body) return err(res, 400, 'bad', 'json')
    // The AI mode is a sustained ceiling, not a burst budget: the daemon collapses fast/slow onto
    // STAPM on the way in, so what /profiles reports is what actually reaches the silicon. Mirrored
    // here because a mock that accepts boost the real daemon discards would let the UI ship a field
    // that silently does nothing. See core/Profiles/ModeProfiles.cs.
    const sustained = mode === 'ai'
    state.presets[mode] = sustained
      ? { stapmW: body.stapmW, fastW: body.stapmW, slowW: body.stapmW, tctlC: body.tctlC }
      : { stapmW: body.stapmW, fastW: body.fastW, slowW: body.slowW, tctlC: body.tctlC }
    return send(res, 200, { mode, ...state.presets[mode], sustained })
  }
  // --- per-app profile rules -----------------------------------------------------------------
  // Prefix is /app-rules, NOT /profiles/rules: POST /profiles/:mode above already owns that space.
  // A rejected rule answers 400 { error: "<sentence>" } — the bare `error` string of core/Program.cs,
  // not this file's { error: { code, message } } shape — because that sentence is what the user sees.
  if (method === 'GET' && path === '/app-rules') return send(res, 200, appRulesInfo())
  if (method === 'POST' && path === '/app-rules') {
    const body = await readBody(req)
    const error = validateRule(body?.match, body?.mode, state.appRules, null)
    if (error) return send(res, 400, { error })
    state.appRules.push({
      id: `rule-${++ruleSeq}`, match: normalizeMatch(body.match), mode: body.mode, enabled: body.enabled !== false,
    })
    return send(res, 200, appRulesInfo())
  }
  if (method === 'POST' && /^\/app-rules\/[^/]+\/move$/.test(path)) {
    const id = path.split('/')[2]
    const from = state.appRules.findIndex((r) => r.id === id)
    if (from < 0) return send(res, 404, { error: 'That rule no longer exists.' })
    const body = await readBody(req)
    const to = Math.max(0, Math.min(state.appRules.length - 1, from + (Number(body?.delta) || 0)))
    // Already at the end it was asked to move towards: the ruleset is what the caller wanted, so
    // this is a no-op and not an error.
    const [rule] = state.appRules.splice(from, 1)
    state.appRules.splice(to, 0, rule)
    return send(res, 200, appRulesInfo())
  }
  if (method === 'PUT' && path.startsWith('/app-rules/')) {
    const id = path.slice('/app-rules/'.length)
    const rule = state.appRules.find((r) => r.id === id)
    if (!rule) return send(res, 404, { error: 'That rule no longer exists.' })
    const body = await readBody(req)
    const error = validateRule(body?.match, body?.mode, state.appRules, id)
    if (error) return send(res, 400, { error })
    rule.match = normalizeMatch(body.match)
    rule.mode = body.mode
    rule.enabled = body.enabled !== false
    return send(res, 200, appRulesInfo())
  }
  if (method === 'DELETE' && path.startsWith('/app-rules/')) {
    const id = path.slice('/app-rules/'.length)
    const before = state.appRules.length
    state.appRules = state.appRules.filter((r) => r.id !== id)
    if (state.appRules.length === before) return send(res, 404, { error: 'That rule no longer exists.' })
    return send(res, 200, appRulesInfo())
  }

  // --- play sessions -------------------------------------------------------------------------
  // `fpsAvailable` is why an empty list can be empty: with no frame probe nothing is ever recorded.
  // The mock has an FPS source, so it reports true. `current` is null — no session is in flight here.
  if (method === 'GET' && path === '/sessions') {
    const limit = Math.max(1, Math.min(500, Number(url.searchParams.get('limit')) || 100))
    const appFilter = url.searchParams.get('appFilter')
    const rows = [...state.sessions]
      .sort((a, b) => (a.startedUtc < b.startedUtc ? 1 : -1))
      .filter((s) => !appFilter || s.app.toLowerCase() === appFilter.trim().toLowerCase())
      .slice(0, limit)
    return send(res, 200, { fpsAvailable: true, current: null, sessions: rows })
  }
  if (method === 'GET' && path === '/sessions/games') {
    return send(res, 200, { fpsAvailable: true, games: perGame(state.sessions) })
  }
  if (method === 'GET' && path.startsWith('/sessions/')) {
    const s = state.sessions.find((x) => x.id === path.slice('/sessions/'.length))
    return s ? send(res, 200, s) : send(res, 404, { error: 'session not found' })
  }
  if (method === 'DELETE' && path.startsWith('/sessions/')) {
    const id = path.slice('/sessions/'.length)
    const before = state.sessions.length
    state.sessions = state.sessions.filter((s) => s.id !== id)
    if (state.sessions.length === before) return send(res, 404, { error: 'session not found' })
    res.writeHead(204, CORS)
    return res.end()
  }

  if (method === 'GET' && path === '/fan') return send(res, 200, fanInfo())
  if (method === 'POST' && path === '/fan') {
    const body = await readBody(req)
    const validFanModes = new Set(['Auto', 'Quiet', 'Balanced', 'Aggressive', 'Manual'])
    if (body?.mode !== undefined && !validFanModes.has(body.mode))
      return send(res, 400, { error: { code: 'bad_mode', message: 'mode must be one of Auto, Quiet, Balanced, Aggressive, Manual' } })
    if (body?.mode) state.fanMode = body.mode
    if (body?.manualDuty !== undefined && body?.manualDuty !== null && Number.isFinite(Number(body.manualDuty)))
      state.fanManualDuty = Math.max(0, Math.min(255, Number(body.manualDuty)))
    return send(res, 200, fanInfo())
  }
  if (method === 'GET' && path === '/battery/budget') return send(res, 200, {
    minutesRemaining: 78, remainingWh: 40.2, dischargeW: 18.4,
    projections: [{ watts: 8, minutes: 301 }, { watts: 12, minutes: 201 }, { watts: 15, minutes: 160 }, { watts: 20, minutes: 120 }, { watts: 25, minutes: 96 }],
  })

  if (method === 'GET' && path === '/freezer') return send(res, 200, { frozen: state.frozen })
  if (method === 'POST' && path === '/freezer/freeze') {
    const body = await readBody(req)
    if (!body?.name) return err(res, 400, 'bad_name', 'name required')
    if (!state.frozen.includes(body.name)) state.frozen.push(body.name)
    return send(res, 200, { name: body.name, suspended: 1, frozen: state.frozen })
  }
  if (method === 'POST' && path === '/freezer/thaw') {
    const body = await readBody(req)
    if (!body?.name) return err(res, 400, 'bad_name', 'name required')
    state.frozen = state.frozen.filter((n) => n !== body.name)
    return send(res, 200, { name: body.name, resumed: 1, frozen: state.frozen })
  }

  if (method === 'GET' && path === '/auto-fps') return send(res, 200, state.autoFps)
  if (method === 'POST' && path === '/auto-fps') {
    const body = await readBody(req)
    state.autoFps = { enabled: !!body?.enable, targetFps: body?.targetFps > 0 ? body.targetFps : state.autoFps.targetFps }
    return send(res, 200, state.autoFps)
  }

  // System health check / anomaly detection. Mirrors core/Health/HealthCheck.cs's rules. The mock
  // returns a canned "fan not spinning while warm" warn so the System page's health card always has
  // something to show in dev/E2E without needing a real parked fan.
  if (method === 'GET' && path === '/health/check') {
    return send(res, 200, {
      status: 'warn',
      issues: [
        { level: 'warn', code: 'fan_not_spinning', message: 'Fan not spinning while warm — 0 rpm at 74°C CPU.' },
      ],
    })
  }

  if (method === 'GET' && path === '/guardian') return send(res, 200, {
    ...state.guardian, throttling: false, throttledToW: null, lastAlert: null, lastSeverity: 'ok',
  })
  if (method === 'POST' && path === '/guardian') {
    const b = await readBody(req)
    const g = state.guardian
    for (const k of ['enabled', 'autoThrottle', 'tempThrottleC', 'tempCriticalC', 'throttleFloorW', 'batteryLowPct', 'batteryCriticalPct']) {
      if (b?.[k] !== undefined && b[k] !== null) g[k] = b[k]
    }
    return send(res, 200, g)
  }

  // MotionAssistant .ini importer — mock returns a small canned set so the E2E has something to
  // import without a real MotionAssistant install.
  if (method === 'POST' && path === '/import/motionassistant') {
    const profiles = state.motionAssistantProfiles
    return send(res, 200, { found: profiles.length, profiles, path: 'C:\\Program Files\\Motion Assistant\\Profiles' })
  }

  // First-run setup wizard — incumbent power-controller check. The mock always reports clear so the
  // wizard/E2E can exercise the "no conflict" path without a real MotionAssistant/GPD Tool install.
  if (method === 'GET' && path === '/system/incumbents') {
    return send(res, 200, { motionAssistant: false, gpdTool: false })
  }

  // Per-power-source auto mode-switch (AC vs battery).
  if (method === 'GET' && path === '/power-source') return send(res, 200, state.powerSource)
  if (method === 'POST' && path === '/power-source') {
    const b = await readBody(req)
    const p = state.powerSource
    if (b?.enabled !== undefined && b.enabled !== null) p.enabled = !!b.enabled
    if (b?.onBatteryMode) p.onBatteryMode = b.onBatteryMode
    if (b?.onAcMode) p.onAcMode = b.onAcMode
    return send(res, 200, p)
  }

  // Settings backup/restore — aggregates the same tunables the real daemon does; tolerant import.
  if (method === 'GET' && path === '/settings/export') {
    return send(res, 200, {
      modePresets: state.presets,
      guardian: state.guardian,
      fanMode: state.fanMode,
      brightness: state.brightness,
      powerSource: state.powerSource,
      autoFps: state.autoFps,
    })
  }
  if (method === 'POST' && path === '/settings/import') {
    const b = await readBody(req)
    const applied = []
    if (b?.modePresets) { Object.assign(state.presets, b.modePresets); applied.push('modePresets') }
    if (b?.guardian) { Object.assign(state.guardian, b.guardian); applied.push('guardian') }
    if (b?.fanMode) { state.fanMode = b.fanMode; applied.push('fanMode') }
    if (b?.brightness !== undefined && b.brightness !== null) {
      state.brightness = Math.max(0, Math.min(100, Number(b.brightness)))
      applied.push('brightness')
    }
    if (b?.powerSource) { Object.assign(state.powerSource, b.powerSource); applied.push('powerSource') }
    if (b?.autoFps) {
      state.autoFps = { enabled: !!b.autoFps.enable, targetFps: b.autoFps.targetFps > 0 ? b.autoFps.targetFps : state.autoFps.targetFps }
      applied.push('autoFps')
    }
    return send(res, 200, { applied })
  }

  // Agents / AI — anti-Modern-Standby, sustained power shaping, VRAM/UMA advisory.
  if (method === 'GET' && path === '/ai') return send(res, 200, aiInfo())
  if (method === 'GET' && path === '/ai/inference-hold') return send(res, 200, inferenceHold())
  if (method === 'POST' && path === '/ai/anti-standby') {
    const body = await readBody(req)
    state.ai.manualAntiStandby = !!body?.enable
    const holders = aiHolders()
    return send(res, 200, { active: holders > 0, holders, manual: state.ai.manualAntiStandby })
  }
  if (method === 'POST' && path === '/ai/vram') {
    // Honest by construction: never a real write (UMA size is BIOS-set), so always applied:false.
    return send(res, 200, { ...vramInfo(), applied: false, requiresBiosReboot: true })
  }

  if (method === 'GET' && path === '/display') return send(res, 200, { brightness: state.brightness })
  if (method === 'POST' && path === '/display/brightness') {
    const body = await readBody(req)
    state.brightness = Math.max(0, Math.min(100, Number(body?.level ?? state.brightness)))
    return send(res, 200, { brightness: state.brightness })
  }

  // Refresh-rate switching (REAL on the daemon; mirrors core/Display/RefreshRateService.cs).
  if (method === 'GET' && path === '/display/refresh') return send(res, 200, { ...state.refresh, error: null })
  if (method === 'POST' && path === '/display/refresh') {
    const body = await readBody(req)
    const hz = Number(body?.hz)
    if (!state.refresh.supported.includes(hz)) {
      const error = `${hz} Hz is not supported on this display (supported: ${state.refresh.supported.join(', ')})`
      return send(res, 200, { ...state.refresh, error })
    }
    state.refresh.current = hz
    return send(res, 200, { ...state.refresh, error: null })
  }

  // Night mode (REAL gamma ramp on the daemon; mirrors core/Display/NightModeService.cs). warmth
  // always reflects what's actually applied — 0 while off, never a merely-remembered value.
  if (method === 'GET' && path === '/display/night') return send(res, 200, state.night)
  if (method === 'POST' && path === '/display/night') {
    const body = await readBody(req)
    const on = !!body?.on
    const requested = body?.warmth !== undefined && body?.warmth !== null ? Math.max(0, Math.min(100, Number(body.warmth))) : (state.night.warmth || 50)
    state.night = { on, warmth: on ? requested : 0 }
    return send(res, 200, state.night)
  }

  // Tablet mode (ADVISORY; write gated behind GPDFORGE_ENABLE_HARDWARE=1 — mirrors TabletModeService.cs).
  if (method === 'GET' && path === '/display/tablet') return send(res, 200, tabletInfo(false))
  if (method === 'POST' && path === '/display/tablet') {
    const gateOpen = process.env.GPDFORGE_ENABLE_HARDWARE === '1'
    if (!gateOpen) return send(res, 200, tabletInfo(false, TABLET_GATE_CLOSED_ADVISORY))
    const body = await readBody(req)
    state.tablet.raw = body?.enable ? 1 : 0
    return send(res, 200, tabletInfo(true))
  }

  // Keyboard backlight (ADVISORY only — mirrors KeyboardBacklightService.cs; no state, no writes).
  if (method === 'GET' && path === '/display/keyboard-backlight') return send(res, 200, { controllable: false, applied: false, advisory: KEYBOARD_BACKLIGHT_ADVISORY })
  if (method === 'POST' && path === '/display/keyboard-backlight') return send(res, 200, { controllable: false, applied: false, advisory: KEYBOARD_BACKLIGHT_ADVISORY })

  // Advanced (hardware-gated): LED/RGB, battery charge limit, undervolt/Curve Optimizer. The mock
  // plays along as controllable (see ledInfo/chargeLimitInfo/undervoltInfo above); the real daemon
  // (core/Led, core/Battery, core/Undervolt) stays gated + honestly applied:false.
  if (method === 'GET' && path === '/led') return send(res, 200, ledInfo())
  if (method === 'POST' && path === '/led') {
    const body = await readBody(req)
    if (!LED_MODES.has(body?.mode)) return err(res, 400, 'bad_mode', 'mode must be one of Off, Solid, Breathe, Rotate')
    state.led.mode = body.mode
    const color = normalizeHexColor(body?.color)
    if (color) state.led.color = color
    return send(res, 200, ledInfo())
  }

  if (method === 'GET' && path === '/battery/charge-limit') return send(res, 200, chargeLimitInfo())
  if (method === 'POST' && path === '/battery/charge-limit') {
    const body = await readBody(req)
    const pct = Math.max(50, Math.min(100, Number(body?.percent)))
    if (Number.isFinite(pct)) state.chargeLimit.percent = pct
    return send(res, 200, chargeLimitInfo())
  }

  if (method === 'GET' && path === '/undervolt') return send(res, 200, undervoltInfo())
  if (method === 'POST' && path === '/undervolt') {
    const body = await readBody(req)
    if (body?.coCount !== undefined && body.coCount !== null && Number.isFinite(Number(body.coCount)))
      state.undervolt.coCount = Math.max(-30, Math.min(30, Number(body.coCount)))
    if (body?.offsetMv !== undefined && body.offsetMv !== null && Number.isFinite(Number(body.offsetMv)))
      state.undervolt.offsetMv = Math.max(-100, Math.min(100, Number(body.offsetMv)))
    return send(res, 200, undervoltInfo())
  }

  if (method === 'GET' && path === '/standby') return send(res, 200, state.standby)
  if (method === 'POST' && path === '/standby/restore') {
    // Per-step outcomes, like the real service. `hid` models the common real case: the pad survived
    // the suspend, so nothing was restarted and the step is a success *because* it did nothing —
    // restarting a working controller mid-game would be worse than the fault it repairs.
    const outcome = {
      at: new Date().toISOString(),
      steps: [
        { name: 'fan', restored: true, detail: 'Fan mode re-applied after resume.' },
        { name: 'tdp', restored: true, detail: 'Sustained TDP re-applied and verified.' },
        {
          name: 'hid',
          restored: true,
          detail:
            'The controller came back on its own — 7 device node(s), none reporting a fault. ' +
            'Nothing was restarted.',
        },
      ],
      anyRestored: true,
    }
    state.tdpVerified = true
    state.standby = { ...state.standby, lastRestore: outcome }
    return send(res, 200, outcome)
  }

  // Update checker — canned "no update" (mirrors GET /update/check's honest-degrade shape; the mock
  // never reaches the real network).
  if (method === 'GET' && path === '/update/check') {
    return send(res, 200, { current: VERSION.replace('-mock', ''), latest: null, updateAvailable: false, url: null })
  }

  // Auto-tuner — see docs/api.md and core/Tuner/TunerState.cs for the real contract. The mock runs
  // the whole sweep synchronously and returns it already finished (`running:false`), so the UI/E2E
  // don't need to poll a multi-second sweep.
  if (method === 'GET' && path === '/tuner') return send(res, 200, state.tuner)
  if (method === 'POST' && path === '/tuner/start') {
    const body = await readBody(req)
    if (!TUNE_GOALS.has(body?.goal))
      return err(res, 400, 'bad_goal', 'goal must be one of MaxFps, BestEfficiency, HoldTarget')

    const rawMin = Number.isFinite(body?.minW) ? body.minW : state.tuner.minW
    const rawMax = Number.isFinite(body?.maxW) ? body.maxW : state.tuner.maxW
    const minW = Math.max(5, Math.min(40, Math.min(rawMin, rawMax)))
    const maxW = Math.max(5, Math.min(40, Math.max(rawMin, rawMax)))
    const tempCapC = Number.isFinite(body?.tempCapC) ? body.tempCapC : state.tuner.tempCapC
    const targetFps = Number.isFinite(body?.targetFps) && body.targetFps > 0 ? body.targetFps : null

    const points = simulateTuneSweep(minW, maxW, tempCapC)
    const best = pickBestTune(points, body.goal, targetFps, tempCapC)
    state.tuner = {
      running: false, goal: body.goal, targetFps, minW, maxW, tempCapC,
      currentStapmW: points.length ? points[points.length - 1].stapmW : minW,
      points, best,
      note: points.length === 0 ? 'Sweep finished but recorded no usable points.' : null,
    }
    return send(res, 200, state.tuner)
  }

  return err(res, 404, 'not_found', `${method} ${path}`)
})

server.listen(PORT, '127.0.0.1', () => console.log(`[gpd-forge mock] http://127.0.0.1:${PORT}`))
