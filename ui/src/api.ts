// GPD Forge UI — local API client. GPL-3.0-or-later.
//
// Talks to the daemon (see docs/api.md). BASE is empty when the UI is served BY the daemon
// (same origin), or set via VITE_FORGE_API for dev / a remote dashboard. Calls throw on failure;
// callers decide how to degrade (the dashboard shows "Offline" and keeps the last values).

import type {
  Telemetry, ModeId, Job, Standby, Preset, BatteryBudget, AutoFps, Guardian, AiInfo, AntiStandby, VramInfo,
  HistoryResponse, ImportResult, PowerSourceConfig, SettingsExport,
} from './types'

const BASE = import.meta.env.VITE_FORGE_API ?? ''

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

export const getTelemetry = () => json<Telemetry>('/telemetry')
export const getMode = async () => (await json<{ active: ModeId }>('/mode')).active
export const setMode = async (mode: ModeId) =>
  (await json<{ active: ModeId }>('/mode', post({ name: mode }))).active
export const setTdp = (stapmW: number) => json<TdpResult>('/tdp', post({ stapmW }))
export const getJobs = () => json<Job[]>('/jobs')
export const createJob = (cmd: string, constraints?: Job['constraints']) =>
  json<{ id: string; status: Job['status'] }>('/jobs', post({ cmd, constraints }))
export const getStandby = () => json<Standby>('/standby')
export const restoreStandby = () => json<{ restored: string[] }>('/standby/restore', post())

// --- editable TDP presets (per mode) ---
export const getProfiles = () => json<Record<string, Preset>>('/profiles')
export const setProfile = (mode: string, p: Preset) =>
  json<Preset & { mode: string }>(`/profiles/${mode}`, post(p))

// --- display ---
export const getBrightness = async () => (await json<{ brightness: number | null }>('/display')).brightness
export const setBrightness = async (level: number) =>
  (await json<{ brightness: number }>('/display/brightness', post({ level }))).brightness

// --- fan mode preference ---
export const getFan = async () => (await json<{ mode: string }>('/fan')).mode
export const setFan = async (mode: string) => (await json<{ mode: string }>('/fan', post({ mode }))).mode

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
