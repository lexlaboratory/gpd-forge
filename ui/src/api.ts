// GPD Forge UI — local API client. GPL-3.0-or-later.
//
// Talks to the daemon (see docs/api.md). BASE is empty when the UI is served BY the daemon
// (same origin), or set via VITE_FORGE_API for dev / a remote dashboard. Calls throw on failure;
// callers decide how to degrade (the dashboard shows "Offline" and keeps the last values).

import type { Telemetry, ModeId, Job, Standby, Preset, BatteryBudget, AutoFps, Guardian } from './types'

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
