// GPD Forge UI — hardware capability report. GPL-3.0-or-later.
//
// Everything GPD Forge cannot do on this machine, gathered in one place and explained.
//
// These controls used to sit inline among the working ones — LED and charge limit and undervolt on
// the Power page, keyboard backlight on Display, an OSD placeholder on Monitor, and a whole
// Controller page that was nothing but disabled sliders. They looked live, they were not, and that
// is what made the app feel like a mock-up. They still round-trip through the daemon (it validates
// and stores the request), so nothing was deleted; they are simply no longer presented as if they
// worked.
//
// The rule this page enforces: a control that cannot reach the hardware must say so next to itself,
// and say what would change that.
import { useEffect, useState } from 'react'
import type { DaemonHealth, FanInfo, LedInfo, ChargeLimitInfo, UndervoltInfo, KeyboardBacklightInfo, TabletModeInfo } from '../types'
import { getHealth, getFanInfo, getLed, getChargeLimit, getUndervolt, getKeyboardBacklight, getTabletMode } from '../api'
import { Frame, Badge } from '../components'
import { LedCard, ChargeLimitRow, UndervoltRow } from './hardware'

/** One capability, its live state, and the reason it is blocked — straight from the daemon. */
function Capability({ name, blocked, reason, testid }: {
  name: string; blocked: boolean; reason: string | null; testid?: string
}) {
  return (
    <li className="rule cap-row" data-testid={testid}>
      <span className="rule-app">{name}</span>
      <Badge tone={blocked ? 'warn' : 'ok'}>{blocked ? 'blocked' : 'available'}</Badge>
      <span className="cap-reason">{reason ?? 'Checking…'}</span>
    </li>
  )
}

function CapabilityReport() {
  const [health, setHealth] = useState<DaemonHealth | null>(null)
  const [fan, setFan] = useState<FanInfo | null>(null)
  const [led, setLed] = useState<LedInfo | null>(null)
  const [charge, setCharge] = useState<ChargeLimitInfo | null>(null)
  const [uv, setUv] = useState<UndervoltInfo | null>(null)
  const [kb, setKb] = useState<KeyboardBacklightInfo | null>(null)
  const [tablet, setTablet] = useState<TabletModeInfo | null>(null)

  useEffect(() => {
    getHealth().then(setHealth).catch(() => {})
    getFanInfo().then(setFan).catch(() => {})
    getLed().then(setLed).catch(() => {})
    getChargeLimit().then(setCharge).catch(() => {})
    getUndervolt().then(setUv).catch(() => {})
    getKeyboardBacklight().then(setKb).catch(() => {})
    getTabletMode().then(setTablet).catch(() => {})
  }, [])

  return (
    <Frame
      title="Capability report"
      hint={health ? `daemon ${health.version} · ${health.model}` : 'querying daemon…'}
      testid="capability-report"
    >
      <p className="muted">
        Measured from this daemon, on this board — not a feature list. Anything marked
        <Badge tone="warn"> blocked</Badge> stores your setting but never reaches the hardware.
      </p>
      <ul className="rules">
        <Capability name="Fan control (PWM)" testid="cap-fan"
          blocked={fan ? !fan.controllable : true}
          reason={fan
            ? (fan.controllable
              ? 'EC write path open — curves and manual duty are live.'
              : 'Needs GPDFORGE_ENABLE_HARDWARE=1 and GPDFORGE_ENABLE_FAN_CONTROL=1, plus a board this build has a verified register map for.')
            : null} />
        <Capability name="LED / RGB" testid="cap-led"
          blocked={led ? !led.controllable : true} reason={led?.advisory ?? null} />
        <Capability name="Battery charge limit" testid="cap-charge"
          blocked={charge ? !charge.available : true} reason={charge?.advisory ?? null} />
        <Capability name="Undervolt / Curve Optimizer" testid="cap-undervolt"
          blocked={uv ? !uv.applied : true} reason={uv?.advisory ?? null} />
        <Capability name="Keyboard backlight" testid="cap-keyboard"
          blocked={kb ? !kb.controllable : true} reason={kb?.advisory ?? null} />
        {/* Tablet mode is the one entry here that CAN be written — it is a registry value, not an
            EC register — so it is never "blocked".
            Note it is judged on `convertible`, NOT on `applied`: on a GET the daemon always returns
            applied:false, because that field means "this request performed a write", not "writes are
            possible". Reading it as a capability flag reported the one working item as broken. */}
        <Capability name="Tablet mode" testid="cap-tablet"
          blocked={false}
          reason={tablet?.advisory ?? null} />
      </ul>
    </Frame>
  )
}

/** The two features that have no daemon endpoint at all — not gated, simply not built. */
function NotBuiltYet() {
  return (
    <Frame title="Not built yet" hint="no daemon endpoint" testid="not-built">
      <p className="muted">
        These have no implementation behind them at all — not a closed gate, just work that has not
        been done. They are listed so the roadmap is visible instead of being faked with dead
        controls.
      </p>
      <ul className="rules">
        <li className="rule cap-row" data-testid="cap-controller">
          <span className="rule-app">Controller remap (L4/R4, gyro, deadzones)</span>
          <Badge tone="muted">not built</Badge>
          <span className="cap-reason">
            Needs ViGEmBus + HidHide and a verified 1024-byte config write. The safe writer exists in
            the daemon (core/Hid/SafeConfigWriter.cs) but is not wired to any route, and this unit's
            firmware rejects the HID config path that pyWinControls uses. Today the only working
            remap is the external scripts/gpd-winctl.ps1.
          </span>
        </li>
        <li className="rule cap-row" data-testid="cap-osd">
          <span className="rule-app">On-screen display (in-game overlay)</span>
          <Badge tone="muted">not built</Badge>
          <span className="cap-reason">
            Needs RTSS single-owner arbitration so it never fights MSI Afterburner or GPD Tool.
            Frame data is already available — FPS and 1% low come from PresentMon — so this is
            presentation work, not measurement work.
          </span>
        </li>
      </ul>
    </Frame>
  )
}

export function HardwarePage() {
  return (
    <>
      <CapabilityReport />
      <NotBuiltYet />

      <Frame title="Stored but not applied" hint="round-trips through the daemon" testid="advisory-controls">
        <p className="muted">
          These still work as settings: the daemon validates and remembers them, and will apply them
          the day a write path exists. Changing one now does nothing to the hardware.
        </p>
      </Frame>
      <LedCard />
      <ChargeLimitRow />
      <UndervoltRow />
    </>
  )
}
