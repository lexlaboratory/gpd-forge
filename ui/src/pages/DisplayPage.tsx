// GPD Forge UI — Display page (brightness, refresh rate, night mode, screen advisories). GPL-3.0-or-later.
//
// Everything left on this page really writes to the panel: brightness via WMI, refresh rate via
// ChangeDisplaySettingsEx, night mode via the GDI gamma ramp. The HUD says so out loud — each frame
// names the call it makes — because the surrounding app has a Hardware page full of controls that
// only *look* live, and the difference has to be visible at a glance.
import { useEffect, useRef, useState } from 'react'
import type { RefreshRateInfo, NightMode, GpuFeature, GpuImageRequest, GpuInfo } from '../types'
import {
  getBrightness, setBrightness, getRefreshRate, setRefreshRate, getNightMode, setNightMode,
  getGpu, setGpuImage,
} from '../api'
import { Frame, Badge, Readout, Segmented, Slider, Toggle, Unavailable } from '../components'
import { useToast } from '../Toast'

// --- Display (brightness, refresh rate, night mode: real; tablet mode, keyboard backlight: advisory) ---
export function DisplayPage() {
  const [bri, setBri] = useState<number | null>(null)
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)
  useEffect(() => { getBrightness().then(setBri).catch(() => {}) }, [])
  const onBri = (v: number) => {
    setBri(v)
    if (timer.current) clearTimeout(timer.current)
    timer.current = setTimeout(() => { setBrightness(v).then(setBri).catch(() => {}) }, 150)
  }
  return (
    <>
      <Frame title="Brightness" hint={<Badge tone="ok">WMI Â· live</Badge>}>
        <div className="grid2">
          <Readout
            label="Panel"
            value={bri === null ? '—' : String(bri)}
            unit="%"
            fraction={bri === null ? undefined : bri / 100}
            tone="info"
          />
        </div>
        <Slider label="Screen brightness" testid="brightness" value={bri ?? 0} min={0} max={100} unit=" %" onChange={onBri} />
        {bri === null
          ? <Unavailable reason="This panel exposes no WmiMonitorBrightness interface to the daemon, so the level can be neither read nor set." />
          : <p className="muted">Written straight to the monitor's brightness class — the same level the hardware keys change, not a software dimming overlay.</p>}
      </Frame>
      <RefreshRateCard />
      <NightModeCard />
      <RadeonImageCard />
      {/* Tablet mode and the keyboard backlight are advisory on this board, so they live on the
          Hardware page with the rest of what cannot be written. Display keeps brightness, refresh
          rate and night mode — all three of which really do change the screen. */}
    </>
  )
}

// Refresh-rate switching — REAL (EnumDisplaySettingsEx / ChangeDisplaySettingsEx).
export function RefreshRateCard() {
  const toast = useToast()
  const [info, setInfo] = useState<RefreshRateInfo | null>(null)
  useEffect(() => { getRefreshRate().then(setInfo).catch(() => {}) }, [])

  const pick = async (hz: number) => {
    const r = await setRefreshRate(hz).catch(() => null)
    if (!r) return
    setInfo(r)
    toast.push(r.error ? { kind: 'warn', message: r.error } : { kind: 'success', message: `Refresh rate set to ${r.current} Hz` })
  }

  return (
    <Frame title="Refresh rate" hint={<Badge tone="ok">ChangeDisplaySettingsEx</Badge>}>
      {info ? (
        <>
          <div className="grid2">
            <Readout label="Active" value={String(info.current)} unit="Hz" />
            <Readout label="Modes offered" value={String(info.supported.length)} />
          </div>
          <Segmented
            label="Refresh rate"
            testid="refresh-modes"
            value={String(info.current)}
            onChange={(id) => { void pick(Number(id)) }}
            options={info.supported.map((hz) => ({ id: String(hz), label: `${hz} Hz`, testid: `refresh-${hz}` }))}
          />
          {info.error && <Unavailable reason={info.error} />}
        </>
      ) : <p className="muted">Enumerating the modes this panel reports…</p>}
      <p className="muted">Rates come from EnumDisplaySettingsEx, so only modes the panel actually reports are offered. Applied for this session only — not written to the registry, so a bad pick never survives a reboot.</p>
    </Frame>
  )
}

// Night mode — REAL (GDI gamma ramp). Deliberately NOT Windows Night Light.
export function NightModeCard() {
  const [night, setNight] = useState<NightMode>({ on: false, warmth: 0 })
  useEffect(() => { getNightMode().then(setNight).catch(() => {}) }, [])

  const toggle = () => { void setNightMode(!night.on, night.warmth || 50).then(setNight).catch(() => {}) }
  const onWarmth = (v: number) => {
    setNight((s) => ({ ...s, warmth: v }))
    if (night.on) void setNightMode(true, v).then(setNight).catch(() => {})
  }

  return (
    <Frame title="Night mode" hint={<Badge tone={night.on ? 'ok' : 'muted'}>{night.on ? 'gamma ramp active' : 'gamma ramp idle'}</Badge>}>
      <div className="grid2">
        <Readout
          label="Warmth"
          value={night.on ? String(night.warmth) : '—'}
          unit="%"
          fraction={night.on ? night.warmth / 100 : undefined}
          tone="warn"
        />
      </div>
      <div className="row">
        <Toggle on={night.on} onClick={toggle} label={night.on ? 'On' : 'Off'} testid="night-toggle" />
      </div>
      <Slider label="Warmth" testid="night-warmth" value={night.warmth} min={0} max={100} unit="%" disabled={!night.on} onChange={onWarmth} />
      <p className="muted">Warms the screen by reducing blue in the GDI gamma ramp — the change lands on the display the moment you move the slider. Independent of Windows Night Light, which GPD Forge deliberately leaves untouched.</p>
    </Frame>
  )
}

// Radeon Super Resolution and Image Sharpening (F4) — through the GPU agent, like the frame cap.
// Absent entirely unless the agent reports ADLX usable AND at least one of the two supported: the
// GPU panel's rule (a greyed-out control reads as "nearly working" when the machine cannot). What is
// shown is what the DRIVER reports, re-read after each request, never our last write.
export function RadeonImageCard() {
  const toast = useToast()
  const [gpu, setGpu] = useState<GpuInfo | null>(null)
  const [draft, setDraft] = useState<{ rsr?: number; ris?: number }>({})
  const refresh = () => getGpu().then((g) => { setGpu(g); setDraft({}) }).catch(() => {})
  useEffect(() => { void refresh() }, [])

  const rsr = gpu?.available ? gpu.settings?.superResolution ?? null : null
  const ris = gpu?.available ? gpu.settings?.imageSharpening ?? null : null
  if (!rsr?.supported && !ris?.supported) return null

  const send = async (image: GpuImageRequest, done: string) => {
    try {
      const r = await setGpuImage(image)
      toast.push(r.pending ? { kind: 'success', message: done } : { kind: 'warn', message: r.reason })
    } catch (e) {
      toast.push({ kind: 'error', message: e instanceof Error ? e.message : 'Not sent to the GPU agent' })
    }
    // The agent carries it out on its next tick (3 s) and reports after; read back what the driver holds.
    setTimeout(() => { void refresh() }, 3500)
  }

  const row = (f: GpuFeature | null, key: 'rsr' | 'ris', name: string) => {
    if (!f?.supported) return null
    const lo = Math.max(0, f.min ?? 0)
    const hi = Math.min(100, f.max ?? 100)
    const sharp = draft[key] ?? f.value ?? lo
    const sharpKey = key === 'rsr' ? 'rsrSharpness' : 'risSharpness'
    return (
      <div className="sheet-field" data-testid={`display-${key}`}>
        <Toggle on={f.enabled} label={name} testid={`display-${key}-toggle`}
          onClick={() => { void send({ [key]: !f.enabled }, `${name} ${f.enabled ? 'off' : 'on'} — handed to the GPU agent`) }} />
        <Slider label={`${name} sharpness`} testid={`display-${key}-sharpness`} value={sharp} min={lo} max={hi} step={5} unit=" %"
          disabled={!f.enabled}
          onChange={(v) => setDraft((d) => ({ ...d, [key]: v }))}
          onCommit={(v) => { void send({ [sharpKey]: v }, `${name} sharpness ${v} % — handed to the GPU agent`) }} />
      </div>
    )
  }

  return (
    <Frame title="Radeon image" hint={<Badge tone="ok">ADLX · GPU agent</Badge>}>
      {row(rsr, 'rsr', 'Super Resolution')}
      {row(ris, 'ris', 'Image Sharpening')}
      <p className="muted">
        Super Resolution renders a fullscreen game below the panel's resolution and upscales it — the most frames
        inside the 22 W limit — and turns Radeon Boost off. It acts only when the game runs fullscreen at a lower
        resolution. Changes go to the driver within a few seconds; a game profile can set its own.
      </p>
    </Frame>
  )
}
