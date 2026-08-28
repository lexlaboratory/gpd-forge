// GPD Forge UI â€” Display page (brightness, refresh rate, night mode, screen advisories). GPL-3.0-or-later.
//
// Everything left on this page really writes to the panel: brightness via WMI, refresh rate via
// ChangeDisplaySettingsEx, night mode via the GDI gamma ramp. The HUD says so out loud â€” each frame
// names the call it makes â€” because the surrounding app has a Hardware page full of controls that
// only *look* live, and the difference has to be visible at a glance.
import { useEffect, useRef, useState } from 'react'
import type { RefreshRateInfo, NightMode } from '../types'
import {
  getBrightness, setBrightness, getRefreshRate, setRefreshRate, getNightMode, setNightMode,
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
            value={bri === null ? 'â€”' : String(bri)}
            unit="%"
            fraction={bri === null ? undefined : bri / 100}
            tone="info"
          />
        </div>
        <Slider label="Screen brightness" testid="brightness" value={bri ?? 0} min={0} max={100} unit=" %" onChange={onBri} />
        {bri === null
          ? <Unavailable reason="This panel exposes no WmiMonitorBrightness interface to the daemon, so the level can be neither read nor set." />
          : <p className="muted">Written straight to the monitor's brightness class â€” the same level the hardware keys change, not a software dimming overlay.</p>}
      </Frame>
      <RefreshRateCard />
      <NightModeCard />
      {/* Tablet mode and the keyboard backlight are advisory on this board, so they live on the
          Hardware page with the rest of what cannot be written. Display keeps brightness, refresh
          rate and night mode â€” all three of which really do change the screen. */}
    </>
  )
}

// Refresh-rate switching â€” REAL (EnumDisplaySettingsEx / ChangeDisplaySettingsEx).
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
      ) : <p className="muted">Enumerating the modes this panel reportsâ€¦</p>}
      <p className="muted">Rates come from EnumDisplaySettingsEx, so only modes the panel actually reports are offered. Applied for this session only â€” not written to the registry, so a bad pick never survives a reboot.</p>
    </Frame>
  )
}

// Night mode â€” REAL (GDI gamma ramp). Deliberately NOT Windows Night Light.
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
          value={night.on ? String(night.warmth) : 'â€”'}
          unit="%"
          fraction={night.on ? night.warmth / 100 : undefined}
          tone="warn"
        />
      </div>
      <div className="row">
        <Toggle on={night.on} onClick={toggle} label={night.on ? 'On' : 'Off'} testid="night-toggle" />
      </div>
      <Slider label="Warmth" testid="night-warmth" value={night.warmth} min={0} max={100} unit="%" disabled={!night.on} onChange={onWarmth} />
      <p className="muted">Warms the screen by reducing blue in the GDI gamma ramp â€” the change lands on the display the moment you move the slider. Independent of Windows Night Light, which GPD Forge deliberately leaves untouched.</p>
    </Frame>
  )
}
