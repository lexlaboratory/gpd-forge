// GPD Forge UI — advanced hardware-gated controls (LED, charge limit, undervolt). GPL-3.0-or-later.
import { useEffect, useState, type ChangeEvent } from 'react'
import type { LedMode, LedInfo, ChargeLimitInfo, UndervoltInfo } from '../types'
import { getLed, setLed, getChargeLimit, setChargeLimit, getUndervolt, setUndervolt } from '../api'
import { Frame, Slider, Chip, Badge } from '../components'
import { useToast } from '../Toast'

// --- Advisory controls: LED/RGB, battery charge limit, undervolt/Curve Optimizer ----------------
// All three STORE but do not APPLY: GPD Forge validates and remembers the request for real, and only
// attempts a write when the daemon's hardware gate is open — and on this HX370 unit, even then,
// there is no working write path yet (HID config, EC/BIOS and RyzenAdj all lack one). The mock
// daemon plays along as controllable/available so this round-trips in dev/E2E; the real daemon stays
// honest (see docs/api.md).
//
// These render on the Hardware page, next to the reason each one is blocked. They used to sit on the
// Power page among controls that really do change the machine, which is what made the app feel like
// a mock-up: nothing told you which buttons were real.
export const LED_MODES: LedMode[] = ['Off', 'Solid', 'Breathe', 'Rotate']

export function LedCard() {
  const toast = useToast()
  const [info, setInfo] = useState<LedInfo | null>(null)
  const [color, setColor] = useState('#00c8ff')

  useEffect(() => { getLed().then((s) => { setInfo(s); setColor(s.color) }).catch(() => {}) }, [])

  const pick = async (mode: LedMode) => {
    const r = await setLed(mode, color).catch(() => null)
    if (!r) return
    setInfo(r)
    toast.push({ kind: r.applied ? 'success' : 'info', message: r.applied ? `LED set to ${mode}` : r.advisory })
  }
  const onColor = (e: ChangeEvent<HTMLInputElement>) => {
    const next = e.target.value
    setColor(next)
    if (info) void setLed(info.mode, next).then(setInfo).catch(() => {})
  }

  return (
    <Frame title="LED / RGB" hint={<Badge tone={info?.applied ? 'ok' : 'warn'}>{info?.applied ? 'writable' : 'stored only'}</Badge>}>
      {/* Not a Segmented group: LED mode is a setting the daemon stores rather than a live choice
          with an applied state, so radio semantics would overstate what pressing one does. */}
      <div className="chips" data-testid="led-modes">
        {LED_MODES.map((m) => (
          <Chip key={m} on={info?.mode === m} onClick={() => pick(m)} testid={`led-${m.toLowerCase()}`}>{m}</Chip>
        ))}
      </div>
      <div className="row">
        <span>Color</span>
        <input type="color" className="led-color" value={color} onChange={onColor} data-testid="led-color" aria-label="LED color" />
      </div>
      <p className="muted" data-testid="led-advisory">{info?.advisory ?? 'Loading…'}</p>
    </Frame>
  )
}

export function ChargeLimitRow() {
  const toast = useToast()
  const [info, setInfo] = useState<ChargeLimitInfo | null>(null)
  useEffect(() => { getChargeLimit().then(setInfo).catch(() => {}) }, [])

  const commit = (v: number) => {
    void setChargeLimit(v).then((r) => {
      setInfo(r)
      toast.push({ kind: r.applied ? 'success' : 'info', message: r.applied ? `Charge limit set to ${r.percent}%` : r.advisory })
    }).catch(() => {})
  }

  return (
    <Frame title="Battery charge limit" hint={<Badge tone={info?.available ? 'ok' : 'warn'}>{info?.available ? 'readable' : 'stored only'}</Badge>}>
      <Slider label="Stop charging at" testid="charge-limit" value={info?.percent ?? 100} min={50} max={100} unit=" %"
        onChange={(v) => setInfo((s) => (s ? { ...s, percent: v } : s))} onCommit={commit} />
      <p className="muted" data-testid="charge-limit-advisory">{info?.advisory ?? 'Loading…'}</p>
    </Frame>
  )
}

export function UndervoltRow() {
  const toast = useToast()
  const [info, setInfo] = useState<UndervoltInfo | null>(null)
  useEffect(() => { getUndervolt().then(setInfo).catch(() => {}) }, [])

  const commit = (coCount: number, offsetMv: number) => {
    void setUndervolt(coCount, offsetMv).then((r) => { setInfo(r); toast.push({ kind: 'info', message: r.advisory }) }).catch(() => {})
  }

  return (
    <Frame title="Undervolt / Curve Optimizer" hint={<Badge tone="warn">stored only</Badge>}>
      <div className="grid2">
        <Slider label="CO count (all-core)" testid="undervolt-co" value={info?.coCount ?? 0} min={-30} max={30}
          onChange={(v) => setInfo((s) => (s ? { ...s, coCount: v } : s))} onCommit={(v) => commit(v, info?.offsetMv ?? 0)} />
        <Slider label="Offset" testid="undervolt-mv" value={info?.offsetMv ?? 0} min={-100} max={100} unit=" mV"
          onChange={(v) => setInfo((s) => (s ? { ...s, offsetMv: v } : s))} onCommit={(v) => commit(info?.coCount ?? 0, v)} />
      </div>
      <p className="muted" data-testid="undervolt-advisory">{info?.advisory ?? 'Loading…'}</p>
    </Frame>
  )
}
