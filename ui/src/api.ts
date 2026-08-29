// GPD Forge UI — local API client. GPL-3.0-or-later.
//
// Talks to daemon on http://127.0.0.1:8787 (see docs/api.md). BASE is empty when the UI is served BY
// the daemon (same origin), or set via VITE_FORGE_API for dev / a remote dashboard. When loaded from
// the Tauri desktop shell (tauri:// / file:// / tauri.localhost), force the absolute local API URL so
// relative requests do not stay inside the local shell. Calls throw on failure; callers degrade.

import type {
  Telemetry, ModeId, Job, Standby, Preset, BatteryBudget, AutoFps, Guardian, AiInfo, AntiStandby, VramInfo,
  HistoryResponse, ImportResult, PowerSourceConfig, SettingsExport,
  RefreshRateInfo, NightMode, TabletModeInfo, KeyboardBacklightInfo,
  TuneGoal, TunerInfo, UpdateCheck,
  LedMode, LedInfo, ChargeLimitInfo, UndervoltInfo,
  HealthReport, PanicResult, IncumbentsInfo, FanInfo, AlertEvent, AlertSummary,
  DaemonHealth, StandbyRestoreOutcome,
} from './types'

const LOCAL_API = 'http://127.0.0.1:8787'
const isLocalShell = typeof window !== 'undefined' && (
  window.location.protocol === 'file:' ||
  window.location.hostname === 'tauri.localhost' ||
  window.location.href.startsWith('tauri://')
)
// BASE is exported so the offline-banner UI can show a sensible "tried to reach" hint and so
// tests can stub it. Same-origin by default; absolute 127.0.0.1:8787 from the Tauri desktop shell.
export const BASE: string = import.meta.env.VITE_FORGE_API || (isLocalShell ? LOCAL_API : '')

export interface TdpResult { requested: number; observed: number; verified: boolean }

async function json<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, init)
  if (!res.ok) throw new Error(`${init?.method ?? 'GET'} ${path} → ${res.status}`)
  return res.json() as Promise<T>
}

function post(body?: unknown): RequestInit {
  return {
    method: 'POST',
    headers: body === undefined ? undefined : { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  }
}

export const getHealth = () => json<DaemonHealth>('/health')
export const getTelemetry = () => json<Telemetry>('/telemetry')
export const getMode = async () => (await json<{ active: ModeId }>('/mode')).active
export const setMode = async (mode: ModeId) =>
  (await json<{ active: ModeId }>('/mode', post({ name: mode }))).active
export const setTdp = (stapmW: number) => json<TdpResult>('/tdp', post({ stapmW }))
export const getJobs = () => json<Job[]>('/jobs')
export const createJob = (cmd: string, constraints?: Job['constraints']) =>
  json<{ id: string; status: Job['status'] }>('/jobs', post({ cmd, constraints }))
export const getStandby = () => json<Standby>('/standby')
export const restoreStandby = () => json<StandbyRestoreOutcome>('/standby/restore', post())

// --- editable TDP presets (per mode) ---
export const getProfiles = () => json<Record<string, Preset>>('/profiles')
export const setProfile = (mode: string, p: Preset) =>
  json<Preset & { mode: string }>(`/profiles/${mode}`, post(p))

// --- display ---
export const getBrightness = async () => (await json<{ brightness: number | null }>('/display')).brightness
export const setBrightness = async (level: number) =>
  (await json<{ brightness: number }>('/display/brightness', post({ level }))).brightness

// --- display: refresh rate + night mode (real), tablet mode + keyboard backlight (advisory) ---
export const getRefreshRate = () => json<RefreshRateInfo>('/display/refresh')
export const setRefreshRate = (hz: number) => json<RefreshRateInfo>('/display/refresh', post({ hz }))
export const getNightMode = () => json<NightMode>('/display/night')
export const setNightMode = (on: boolean, warmth?: number) => json<NightMode>('/display/night', post({ on, warmth }))
export const getTabletMode = () => json<TabletModeInfo>('/display/tablet')
export const setTabletMode = (enable: boolean) => json<TabletModeInfo>('/display/tablet', post({ enable }))
export const getKeyboardBacklight = () => json<KeyboardBacklightInfo>('/display/keyboard-backlight')
export const setKeyboardBacklight = () => json<KeyboardBacklightInfo>('/display/keyboard-backlight', post())

// --- fan mode preference + gated manual duty (core/Fan/GpdFanController.cs) ---
export const getFan = async () => (await json<{ mode: string }>('/fan')).mode
export const setFan = async (mode: string) => (await json<{ mode: string }>('/fan', post({ mode }))).mode
export const getFanInfo = () => json<FanInfo>('/fan')
export const setFanManualDuty = (manualDuty: number) => json<FanInfo>('/fan', post({ manualDuty }))

// --- battery budget ---
export const getBudget = () => json<BatteryBudget>('/battery/budget')

// --- freezer ---
export const getFrozen = async () => (await json<{ frozen: string[] }>('/freezer')).frozen
export const freeze = (name: string) => json<{ name: string; suspended: number; frozen: string[] }>('/freezer/freeze', post({ name }))
export const thaw = (name: string) => json<{ name: string; resumed: number; frozen: string[] }>('/freezer/thaw', post({ name }))

// --- auto-TDP to FPS ---
export const getAutoFps = () => json<AutoFps>('/auto-fps')
export const setAutoFps = (targetFps: number, enable: boolean) => json<AutoFps>('/auto-fps', post({ targetFps, enable }))

// --- thermal/battery guardian ---
export const getGuardian = () => json<Guardian>('/guardian')
export const setGuardian = (patch: Partial<Guardian>) => json<Guardian>('/guardian', post(patch))

// --- Agents / AI mode: anti-standby, sustained profile, VRAM/UMA advisory ---
export const getAi = () => json<AiInfo>('/ai')
export const setAntiStandby = (enable: boolean) => json<AntiStandby>('/ai/anti-standby', post({ enable }))
export const requestVram = (requestedMb?: number) =>
  json<VramInfo & { applied: boolean; requiresBiosReboot: boolean }>('/ai/vram', post({ requestedMb }))

// --- telemetry history + CSV export ---
export const getHistory = (minutes?: number) => json<HistoryResponse>(`/history${minutes ? `?minutes=${minutes}` : ''}`)
export const historyExportUrl = () => `${BASE}/history/export.csv`

// --- MotionAssistant .ini importer ---
export const importMotionAssistant = () => json<ImportResult>('/import/motionassistant', post())

// --- per-power-source (AC vs battery) auto mode-switch ---
export const getPowerSource = () => json<PowerSourceConfig>('/power-source')
export const setPowerSource = (patch: Partial<PowerSourceConfig>) => json<PowerSourceConfig>('/power-source', post(patch))

// --- settings backup / restore ---
export const exportSettings = () => json<SettingsExport>('/settings/export')
export const importSettings = (blob: unknown) => json<{ applied: string[] }>('/settings/import', post(blob))
export const settingsExportUrl = () => `${BASE}/settings/export`

// --- auto-tuner (TDP sweep) ---
export interface StartTunerRequest { goal: TuneGoal; targetFps?: number; minW?: number; maxW?: number; tempCapC?: number }
export const getTuner = () => json<TunerInfo>('/tuner')
export const startTuner = (req: StartTunerRequest) => json<TunerInfo>('/tuner/start', post(req))

// --- update checker ---
export const checkUpdate = () => json<UpdateCheck>('/update/check')

// --- Advanced (hardware-gated): LED/RGB, battery charge limit, undervolt/Curve Optimizer ---
export const getLed = () => json<LedInfo>('/led')
export const setLed = (mode: LedMode, color?: string) => json<LedInfo>('/led', post({ mode, color }))
export const getChargeLimit = () => json<ChargeLimitInfo>('/battery/charge-limit')
export const setChargeLimit = (percent: number) => json<ChargeLimitInfo>('/battery/charge-limit', post({ percent }))
export const getUndervolt = () => json<UndervoltInfo>('/undervolt')
export const setUndervolt = (coCount?: number, offsetMv?: number) =>
  json<UndervoltInfo>('/undervolt', post({ coCount, offsetMv }))

// --- system health check / anomaly detection ---
export const getHealthCheck = () => json<HealthReport>('/health/check')

// --- panic cool (safety) ---
export const panicCool = () => json<PanicResult>('/panic', post())

// --- first-run setup wizard: incumbent power-controller check ---
export const getIncumbents = () => json<IncumbentsInfo>('/system/incumbents')
export const getAlerts = (unreadOnly = false, limit = 100) => json<{ alerts: AlertEvent[] }>(`/alerts?limit=${limit}&unreadOnly=${unreadOnly}`)
export const getAlertSummary = () => json<AlertSummary>('/alerts/summary')
export const acknowledgeAlert = (id: string) => json<{ acknowledged: boolean }>(`/alerts/${id}/ack`, post())
export const acknowledgeAllAlerts = () => json<{ acknowledged: number }>('/alerts/ack-all', post())
export const deleteAlert = (id: string) => json<void>(`/alerts/${id}`, { method: 'DELETE' })
