// GPD Forge — Quick Access Menu (the "Home button" overlay). GPL-3.0-or-later.
//
// A compact, gamepad-first panel meant to live in a borderless always-on-top window
// (or a browser app-window) launched by the user's chosen Home button / hotkey. It reuses
// the app's design system and talks to the same local daemon. Every control is a <button>
// so D-pad focus + A-to-activate works with zero range-input fiddling: a d-pad can reach a
// button but cannot meaningfully drag an input[type=range], which is why TDP and brightness
// are steppers and never sliders.
import { useCallback, useEffect, useRef, useState } from 'react'
import type { ModeId, Telemetry, BatteryBudget } from './types'
import {
  getTelemetry, getMode, setMode, setTdp, getTdp, getProfiles, getFan, setFan,
  getBrightness, setBrightness, getAutoFps, setAutoFps, getBudget, restoreStandby, getGpu, setFrameCap,
  getAppRules, getSessions, getGpuDesired,
} from './api'
import { Segmented, Stepper } from './components'
import { useToast } from './Toast'
import { useDensity } from './hooks/useDensity'
import { useSpatialNav } from './hooks/useSpatialNav'
import { useActiveProfile } from './hooks/useActiveProfile'
import {
  capInForce, captureOverrides, describeOverrides, displayName, exactRule, gameUnderOverlay, governingRule, noticeParts,
  noticeText, profileKey, toRuleFanMode,
} from './gameProfile'
import { saveGameProfile } from './gameProfileSave'
// Same placeholder rule as the main window: null renders as '--', never as 0. Telemetry went
// nullable on 2026-09-01 because an unreadable sensor used to arrive as a confident zero.
import {
  reading, staleSeconds, unsampled, offlineSeconds, tdpInForce, tdpVerifiedNow, TDP_SEED_RETRY_MS, type TdpWrite,
} from './pages/shared'
import { Icon } from './components/Icon'

const QMODES: { id: ModeId; label: string }[] = [
  { id: 'gaming', label: 'Gaming' },
  // The mode most worth having HERE: it is chosen mid-session, away from a desk, at the moment
  // someone notices the battery — which is exactly the situation the overlay exists for.
  { id: 'gaming-battery', label: 'Gaming (batt)' },
  { id: 'ai', label: 'AI' },
  { id: 'windows', label: 'Windows' },
  { id: 'battery', label: 'Battery' },
  { id: 'standby', label: 'Standby' },
]
const FAN_MODES = ['Auto', 'Quiet', 'Balanced', 'Aggressive']
// Two different things, and the overlay used to show only the first under the second's name.
//   FPS_TARGETS -> auto-FPS: steers TDP to REACH this rate. Does not stop the GPU exceeding it.
//   CAP_OPTIONS -> FRTC: the driver refusing to EXCEED this rate. An actual cap.
// Labelling auto-FPS as "FPS cap" promised a ceiling and delivered a goal — the exact class of
// mislabelled control this project has spent releases removing.
const FPS_TARGETS = [{ label: 'Off', v: 0 }, { label: '30', v: 30 }, { label: '60', v: 60 }, { label: '90', v: 90 }, { label: '120', v: 120 }]
const CAP_TARGETS = [{ label: 'Off', v: 0 }, { label: '30', v: 30 }, { label: '45', v: 45 }, { label: '60', v: 60 }, { label: '90', v: 90 }]

const FAN_OPTIONS = FAN_MODES.map((f) => ({ id: f, label: f, testid: `qam-fan-${f}` }))
const FPS_OPTIONS = FPS_TARGETS.map((t) => ({ id: String(t.v), label: t.label, testid: `qam-fps-${t.v}` }))
const CAP_OPTIONS = CAP_TARGETS.map((t) => ({ id: String(t.v), label: t.label, testid: `qam-cap-${t.v}` }))

/** Close the overlay: hide the native window if we're in Tauri, else close the browser app-window. */
function closeOverlay() {
  const w = window as unknown as { __TAURI__?: { window?: { getCurrent: () => { hide: () => void } } } }
  try { if (w.__TAURI__?.window) { w.__TAURI__.window.getCurrent().hide(); return } } catch { /* fall through */ }
  window.close()
}

/** The driver cap in force now (capInForce): the daemon's request, else the driver's own. A failed read
 *  is an unknown, not a refusal — the caller decides what unknown means for it. */
const readCap = () =>
  Promise.all([getGpu().catch(() => null), getGpuDesired().catch(() => null)]).then(([g, d]) => capInForce(g, d))

function fmtBudget(b: BatteryBudget | null): string {
  if (!b) return '—'
  if (b.minutesRemaining == null) return `On AC · ${b.remainingWh.toFixed(0)} Wh`
  const h = Math.floor(b.minutesRemaining / 60), m = b.minutesRemaining % 60
  return `~${h}h ${String(m).padStart(2, '0')}m @ ${b.dischargeW.toFixed(0)} W`
}

export function OverlayApp() {
  const toast = useToast()
  const rootRef = useRef<HTMLDivElement | null>(null)
  // The overlay is the surface most likely to be driven by a thumb or a pad, so it wants the same
  // density detection as the main window rather than a hardcoded size.
  useDensity()
  const [tele, setTele] = useState<Telemetry | null>(null)
  // When the last poll succeeded, and when the last poll finished either way (browser clock, Unix ms).
  // A failed poll keeps the previous reading, whose `sampleAgeMs` was fresh when served and never ages
  // here — so without these a dead daemon left a green "live" dot over frozen numbers (audit round 1,
  // 2026-09-25). The main window has its Offline pill for this; the overlay had nothing.
  const [lastOkMs, setLastOkMs] = useState<number | null>(null)
  const [nowMs, setNowMs] = useState(0)
  const [mode, setModeS] = useState<ModeId>('windows')
  const [presets, setPresets] = useState<Record<string, { stapmW: number }>>({})
  // Null until the daemon says what is in force, shown as '--' with the stepper disabled — as on the
  // Dashboard. It was a hardcoded 20 that flashed before every seed and stayed on screen, looking like
  // the value in force, whenever the seed failed (audit round 3, 2026-09-24).
  const [tdp, setTdp_] = useState<number | null>(null)
  // Null until the daemon says: the "verified" mark used to show from the first frame, before anything
  // had been written or read back. `seedVerified` is GET /tdp's answer at open; after that the mark
  // follows telemetry (tdpVerifiedNow), so a limit the 30 s reassert could not hold stops saying
  // "verified" — it used to change only when this window wrote.
  const [seedVerified, setSeedVerified] = useState<boolean | null>(null)
  const [tdpWrite, setTdpWrite] = useState<TdpWrite | null>(null)
  const [seedRound, setSeedRound] = useState(0)
  // The user has pressed the stepper: a late seed from GET /tdp must not move it back.
  const tdpTouched = useRef(false)
  // The last value the daemon accepted, to return to when a write is refused.
  const tdpApplied = useRef<number | null>(null)
  // Null until GET /fan answers. It was a hardcoded 'Auto' that a failed read left on screen — and the
  // save button captured it as the game's fan (F1 audit round 1).
  const [fan, setFanS] = useState<string | null>(null)
  const [fpsTarget, setFpsTarget] = useState(0)
  // Null while unknown. The cap row stays hidden until the daemon says the GPU can do it — a control
  // that cannot work is worse than an absent one.
  const [frameCap, setFrameCapS] = useState<number | null>(null)
  const [capSupported, setCapSupported] = useState(false)
  const [bright, setBright] = useState(70)
  const [budget, setBudget] = useState<BatteryBudget | null>(null)
  // The game under the overlay (F1): the app presenting frames, else the app a rule decided on — never
  // the overlay's own Edge window (gameUnderOverlay says why). Null when neither knows; the save button
  // then says so rather than guessing.
  const [game, setGame] = useState<string | null>(null)
  const [savingProfile, setSavingProfile] = useState(false)
  // The profile in force, shown as one line in the header: the overlay is where a player looks mid-game.
  const activeProfile = useActiveProfile()

  useEffect(() => {
    let alive = true
    // The clock moves on every tick, not when a poll settles: /telemetry has no client timeout, so a
    // daemon that hangs rather than refuses never reaches the catch — and must still age on screen.
    const tick = () => {
      setNowMs(Date.now())
      getTelemetry().then((t) => { if (alive) { setTele(t); setLastOkMs(Date.now()) } }).catch(() => {})
    }
    tick(); const id = setInterval(tick, 1000)
    getBrightness().then((b) => alive && b != null && setBright(b)).catch(() => {})
    getAutoFps().then((a) => alive && setFpsTarget(a.enabled ? a.targetFps : 0)).catch(() => {})
    const bt = () => getBudget().then((b) => alive && setBudget(b)).catch(() => {})
    bt(); const bid = setInterval(bt, 5000)
    const fg = async () => {
      const [lastMatch, presenting] = await Promise.all([
        getAppRules().then((r) => r.lastMatch).catch(() => null),
        getSessions(1).then((s) => s.current).catch(() => null),
      ])
      if (alive) setGame(gameUnderOverlay(lastMatch, presenting))
    }
    fg(); const gid = setInterval(fg, 5000)
    return () => { alive = false; clearInterval(id); clearInterval(bid); clearInterval(gid) }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // The fan row, the cap row and the TDP stepper follow the daemon: read at open, and again whenever a
  // game profile goes on or comes off — it settles ~4.5 s after focus, often after the overlay opened,
  // and sets or restores all three. Only the fan did until F1 audit round 2: the cap row kept showing
  // "Off" under a header saying "60 FPS", and the stepper kept the preset's 20 W under "22 W", so its +
  // stepped DOWN to a manual 21 W.
  const profileNow = profileKey(activeProfile)
  const seededFor = useRef<string | null | undefined>(undefined)   // undefined = the open's own seed
  useEffect(() => {
    let alive = true
    getFan().then((f) => { if (alive) setFanS(f) }).catch(() => {})
    // The cap row only appears when the driver actually offers one. Hidden rather than disabled: on a
    // gamepad-first overlay an unusable row is one more thing to skip past with the D-pad.
    readCap().then((c) => {
      if (!alive) return
      setCapSupported(c.supported)
      if (c.fps != null) setFrameCapS(c.fps === 0 ? null : c.fps)
    })
    // A value the user set on the stepper outranks the game's (TdpIntent), so it stays.
    if (seededFor.current !== undefined && seededFor.current !== profileNow && !tdpTouched.current) {
      setSeedRound((r) => r + 1)
    }
    seededFor.current = profileNow
    return () => { alive = false }
  }, [profileNow])

  // The stepper opens on what is IN FORCE: the manual override the daemon remembers (TdpIntent), else
  // its last write, else the active mode's preset. It used to open on the preset of the initial
  // 'windows' state whatever the mode, so a remembered 12 W showed as 15. A seed that finds nothing
  // asks again every TDP_SEED_RETRY_MS rather than leaving the stepper dead (audit round 3).
  useEffect(() => {
    let alive = true
    let retry: ReturnType<typeof setTimeout> | null = null
    Promise.allSettled([getMode(), getProfiles(), getTdp()]).then(([m, p, t]) => {
      if (!alive) return
      if (m.status === 'fulfilled') setModeS(m.value)
      const presetsNow = p.status === 'fulfilled' ? p.value : {}
      if (p.status === 'fulfilled') setPresets(presetsNow)
      const info = t.status === 'fulfilled' ? t.value : null
      if (info) setSeedVerified(info.verified)
      const seed = tdpInForce(info) ?? (m.status === 'fulfilled' ? presetsNow[m.value]?.stapmW : undefined)
      if (seed == null) {
        retry = setTimeout(() => setSeedRound((r) => r + 1), TDP_SEED_RETRY_MS)
        return
      }
      tdpApplied.current = seed
      if (!tdpTouched.current) setTdp_(seed)
    })
    return () => { alive = false; if (retry) clearTimeout(retry) }
  }, [seedRound])

  // Same 2-D walk as the main window. The old linear version made Left and Down do the same thing,
  // which on a grid of five mode squares is close to unusable.
  useSpatialNav(rootRef, { onCancel: closeOverlay })

  const pickMode = async (m: ModeId) => {
    const previous = mode
    setModeS(m)
    try {
      await setMode(m)
    } catch (e) {
      // Said, and undone: a failed switch used to be swallowed, then the stepper showed the new mode's
      // preset and recorded it as applied — a value a later refused write rolled back to (audit round 3).
      setModeS(previous)
      toast.push({ kind: 'error', message: `Mode was not changed — ${e instanceof Error ? e.message : String(e)}` })
      return
    }
    // A mode change ends the manual override, so the preset is what is now in force.
    if (presets[m]) { setTdp_(presets[m].stapmW); tdpApplied.current = presets[m].stapmW }
    else { tdpTouched.current = false; setSeedRound((r) => r + 1) }   // no preset known: ask the daemon
    setTdpWrite(null)
    toast.push({ kind: 'info', message: `Mode: ${QMODES.find((x) => x.id === m)?.label ?? m}` })
  }
  const applyTdp = async (next: number) => {
    tdpTouched.current = true
    setTdp_(next)
    try {
      const r = await setTdp(next)
      // What the firmware holds, when it could be read back; else the request stands. `observed` is
      // null on an unreadable readback, and writing that into the stepper blanked it.
      const held = r.observed ?? next
      setTdp_(held); tdpApplied.current = held; setTdpWrite({ verified: r.verified, atMs: Date.now() })
    } catch (e) {
      // Said, not swallowed: a refused value (400 bad_tdp) or an unreachable daemon left the stepper
      // on a number that was never applied.
      toast.push({ kind: 'error', message: `TDP ${next} W was not applied — ${e instanceof Error ? e.message : String(e)}` })
      if (tdpApplied.current != null) setTdp_(tdpApplied.current)
    }
  }
  const pickFan = async (f: string) => {
    const previous = fan
    setFanS(f)
    try {
      await setFan(f)
    } catch (e) {
      // Said, and undone, as pickMode and applyTdp do: a swallowed failure left the row on a mode the
      // fan never took — and "save as profile" then captured it.
      setFanS(previous)
      toast.push({ kind: 'error', message: `Fan was not changed — ${e instanceof Error ? e.message : String(e)}` })
    }
  }
  const pickFps = async (v: number) => { setFpsTarget(v); try { await setAutoFps(v || 60, v > 0) } catch { /* ignore */ } }
  const pickCap = async (v: number) => {
    const previous = frameCap
    setFrameCapS(v || null)
    try {
      await setFrameCap(v || null)
    } catch {
      // Put the control back where it was. The daemon refuses a cap below an active auto-FPS target
      // — leaving the switch showing a value that was rejected is how a UI starts lying.
      setFrameCapS(previous)
    }
  }
  const applyBright = async (next: number) => {
    setBright(next)
    try { const b = await setBrightness(next); setBright(b) } catch { /* ignore */ }
  }
  // "Save as profile for this game": what the user has in force right now — TDP, the driver cap (only
  // when the GPU offers one; otherwise the mode keeps deciding it) and the fan — into the game's own
  // rule. The Radeon toggles and the freeze list, which this panel cannot see, are kept as stored.
  //
  // Everything is read from the daemon at the press, not taken from this panel's state. TDP and fan
  // since F1 audit round 1: the stepper was seeded once at open from the last write by ANY owner, so
  // opening it mid-throttle and saving stored the guardian's ceiling as the game's watts. `intentStapmW`
  // is the user's own intent — manual, else the game's, else the preset — whoever wrote last. The cap
  // and the mode since round 2: a profile that set 60 FPS after the overlay opened was saved as "no FPS
  // cap", and a mode picked in the main window saved a new rule under the old one. A cap that cannot be
  // read (agent silent or stale) keeps the stored one (captureOverrides) instead of erasing it.
  const saveProfile = async () => {
    if (!game) return
    setSavingProfile(true)
    try {
      const [info, tdpNow, fanNow, modeNow, capNow] = await Promise.all([getAppRules(), getTdp(), getFan(), getMode(), readCap()])
      setModeS(modeNow)   // the tiles follow too: the mode may have been changed from the main window
      const stapmW = tdpNow.intentStapmW ?? tdpNow.manualStapmW
      if (stapmW == null) throw new Error('the TDP you have in force is not known yet — try again in a moment')
      const own = exactRule(info.rules, game)
      // An existing rule keeps its mode; a new one takes the rule that claims the game today, else the
      // mode in force if a rule may select it, else gaming.
      const ruleMode = own?.mode ?? governingRule(info.rules, game)?.mode
        ?? (info.modes.includes(modeNow) ? modeNow : 'gaming')
      const overrides = captureOverrides(own?.overrides, {
        stapmW,
        frameCapFps: capNow.fps,
        fanMode: toRuleFanMode(fanNow),
      })
      await saveGameProfile(game, ruleMode, overrides)
      toast.push({ kind: 'success', message: `Saved as the ${displayName(game)} profile: ${describeOverrides(overrides)}` })
    } catch (e) {
      toast.push({ kind: 'error', message: `Profile not saved — ${e instanceof Error ? e.message : String(e)}` })
    } finally {
      setSavingProfile(false)
    }
  }
  const doRestore = async () => { try { await restoreStandby(); toast.push({ kind: 'success', message: 'Standby state restored' }) } catch { /* ignore */ } }
  const openFull = useCallback(() => { window.location.assign('/') }, [])
  // The daemon answers GET /telemetry from its sampler's cache, so a sampler whose hardware read hangs
  // keeps serving the same numbers with a normal 200. Its age is what says so.
  // Not answering at all outranks both below: the age the daemon last reported is not what is wrong.
  const offlineS = offlineSeconds(lastOkMs, nowMs)
  const offline = offlineS != null
  const staleS = offline ? null : staleSeconds(tele)
  // Answering, but the hardware has never been read (audit round 3): not stale, and not live either.
  const noReading = !offline && unsampled(tele)
  const verified = tele ? tdpVerifiedNow(tele, tdpWrite) : (tdpWrite?.verified ?? seedVerified)

  return (
    <div className="qam" ref={rootRef} data-testid="qam">
      <header className="qam-head">
        <div className="qam-brand">
          <img className="qam-logo" src="/logo.svg" alt="" aria-hidden width={20} height={20} />
          <span>GPD Forge</span>
          {offline && (
            <span className="qam-stale" data-testid="qam-offline" role="status"
                  aria-label={`Daemon not answering — last reading ${offlineS} s ago`}>
              Offline · {offlineS} s ago
            </span>
          )}
          {staleS != null && (
            <span className="qam-stale" data-testid="qam-stale" role="status"
                  aria-label={`Telemetry stalled — last reading ${staleS} s ago`}>
              Stalled · {staleS} s ago
            </span>
          )}
          {noReading && (
            <span className="qam-stale" data-testid="qam-unsampled" role="status"
                  aria-label="No telemetry yet — the daemon has not read the hardware">
              No reading yet
            </span>
          )}
          <span className={`qam-dot ${tele && !offline && staleS == null && !noReading ? 'on' : ''}`}
                title={offline ? 'offline' : noReading ? 'no reading yet' : staleS == null ? 'live' : 'stalled'} />
        </div>
        {activeProfile?.active && (() => {
          const { lead, detail } = noticeParts(activeProfile)
          // Something refused: the reason is what the player most needs, so the line wraps rather than
          // ending in an ellipsis (a pad cannot open the title tooltip) and takes the warning tone.
          const refused = activeProfile.skipped.length > 0
          return (
            <p className={`qam-profile${refused ? ' warn' : ''}`} data-testid="qam-profile" data-skipped={refused || undefined}
               role="status" title={noticeText(activeProfile)}>
              <span className="qam-profile-lead">{lead}</span>{' '}<span className="qam-profile-detail">{detail}</span>
            </p>
          )
        })()}
        {/* The live triple is the first thing a player looks at, so it gets the largest type in the
            panel and its own bracketed frame. Dimmed when stale: a frozen reading must not look live. */}
        <div className="qam-live" data-stale={offline || staleS != null || noReading || undefined}>
          <div className="qam-stat">
            <span className="qam-stat-v">{reading(tele?.cpuTempC)}<i>°C</i></span>
            <span className="qam-stat-k">CPU</span>
          </div>
          <div className="qam-stat">
            <span className="qam-stat-v">{reading(tele?.packageW)}<i>W</i></span>
            <span className="qam-stat-k">Pkg</span>
          </div>
          <div className="qam-stat">
            <span className="qam-stat-v">{reading(tele?.fps)}<i>fps</i></span>
            <span className="qam-stat-k">Frame</span>
          </div>
        </div>
      </header>

      <div className="qam-modes" role="group" aria-label="Mode">
        {QMODES.map((m) => (
          <button key={m.id} className={`qam-mode ${mode === m.id ? 'on' : ''}`} data-testid={`qam-mode-${m.id}`}
            onClick={() => pickMode(m.id)} title={m.label} aria-pressed={mode === m.id}>
            <span className="qam-mode-i"><Icon name={m.id} size={22} /></span>
            <span className="qam-mode-k">{m.label}</span>
          </button>
        ))}
      </div>

      <div className="qam-line">
        <span className="qam-label">TDP {verified === true && <em className="qam-ok" data-testid="qam-verified">verified</em>}</span>
        <Stepper
          label="TDP" value={tdp} unit="W" min={5} max={40} onChange={applyTdp} disabled={tdp == null}
          testid="qam-tdp" decTestid="qam-tdp-dec" incTestid="qam-tdp-inc"
        />
      </div>

      <div className="qam-line stack">
        <span className="qam-label">Fan</span>
        <Segmented flavour="qam" label="Fan" options={FAN_OPTIONS} value={fan ?? ''} onChange={pickFan} />
      </div>

      <div className="qam-line stack">
        <span className="qam-label">FPS target</span>
        <Segmented flavour="qam" label="FPS target" options={FPS_OPTIONS} value={String(fpsTarget)}
          onChange={(id) => pickFps(Number(id))} />
      </div>

      {capSupported && (
        <div className="qam-line stack">
          <span className="qam-label">FPS cap</span>
          <Segmented flavour="qam" label="FPS cap" options={CAP_OPTIONS} value={String(frameCap ?? 0)}
            onChange={(id) => pickCap(Number(id))} />
        </div>
      )}

      <div className="qam-line">
        <span className="qam-label">Brightness</span>
        <Stepper
          label="Brightness" value={bright} unit="%" min={0} max={100} step={10} onChange={applyBright}
          testid="qam-bright" decTestid="qam-bright-dec" incTestid="qam-bright-inc"
        />
      </div>

      <div className="qam-batt" data-testid="qam-budget">
        <span className="qam-label">Battery</span>
        <span className="qam-batt-v">{fmtBudget(budget)}</span>
      </div>

      <footer className="qam-foot">
        <button className="qam-action qam-save" data-testid="qam-save-profile" onClick={saveProfile}
                disabled={!game || savingProfile || tdp == null || fan == null}>
          <Icon name="save" />
          <span className="qam-action-text">
            <span>{savingProfile ? 'Saving…' : 'Save as profile for this game'}</span>
            <span className="qam-action-sub">{game ? displayName(game) : 'No game in front'}</span>
          </span>
        </button>
        <button className="qam-action" data-testid="qam-restore" onClick={doRestore}><Icon name="restore" />Restore standby</button>
        <button className="qam-action" data-testid="qam-full" onClick={openFull}><Icon name="expand" />Full UI</button>
        <button className="qam-action qam-close" data-testid="qam-close" onClick={closeOverlay}><Icon name="close" />Close</button>
      </footer>
    </div>
  )
}
