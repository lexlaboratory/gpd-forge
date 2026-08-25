// GPD Forge UI — local API client. GPL-3.0-or-later.
//
// Talks to the daemon (see docs/api.md) when VITE_FORGE_API is set; otherwise serves a
// deterministic demo so the UI builds and renders standalone. The mock daemon in
// tools/mock-daemon implements the same contract for dev + E2E.

import type { Telemetry, ModeId, Job, Standby } from './types'

const BASE = import.meta.env.VITE_FORGE_API ?? ''
export const HAS_API = BASE !== ''

const DEMO: Telemetry = {
  cpuTempC: 61, gpuTempC: 58, packageW: 22, cpuClockMhz: 3300,
  fanRpm: 3600, fanDutyPct: 45, fps: 60,
  batteryPct: 78, dischargeW: 18.4, acConnected: false, tdpVerified: true,
}

export interface TdpResult { requested: number; observed: number; verified: boolean }

async function json<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, init)
  if (!res.ok) throw new Error(`${init?.method ?? 'GET'} ${path} → ${res.status}`)
  return res.json() as Promise<T>
}

export async function getTelemetry(): Promise<Telemetry> {
  if (!HAS_API) return DEMO
  return json<Telemetry>('/telemetry')
}

export async function getMode(): Promise<ModeId> {
  if (!HAS_API) return 'windows'
  return (await json<{ active: ModeId }>('/mode')).active
}

export async function setMode(mode: ModeId): Promise<ModeId> {
  if (!HAS_API) return mode
  const r = await json<{ active: ModeId }>('/mode', {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ name: mode }),
  })
  return r.active
}

export async function setTdp(stapmW: number): Promise<TdpResult> {
  if (!HAS_API) return { requested: stapmW, observed: stapmW, verified: true }
  return json<TdpResult>('/tdp', {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ stapmW }),
  })
}

// --- Agents / AI mode: job queue ---

export async function getJobs(): Promise<Job[]> {
  if (!HAS_API) return []
  return json<Job[]>('/jobs')
}

export async function createJob(cmd: string, constraints?: Job['constraints']): Promise<{ id: string; status: Job['status'] }> {
  if (!HAS_API) return { id: 'demo', status: 'queued' }
  return json('/jobs', {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ cmd, constraints }),
  })
}

// --- Standby Doctor ---

export async function getStandby(): Promise<Standby | null> {
  if (!HAS_API) return null
  return json<Standby>('/standby')
}

export async function restoreStandby(): Promise<{ restored: string[] }> {
  if (!HAS_API) return { restored: ['tdp', 'fan', 'hid'] }
  return json('/standby/restore', { method: 'POST' })
}
