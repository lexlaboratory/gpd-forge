// GPD Forge UI — the per-game profile editor (Games page). GPL-3.0-or-later.
//
// F1 (2026-09-25). Edits the overrides of ONE game's own rule (gameProfile.ts decides which rule that
// is and where it must sit to win). Every setting starts on "the mode decides": a profile is a list of
// exceptions to the mode, not a second copy of it, so nothing is written that the user did not choose.
//
// A side sheet beside the list from 1100px, stacked full width above it below that (styles.css,
// GAMES). It is in the page flow rather than floating over it, so the d-pad's spatial walk and a
// finger reach it the same way as everything else, and nothing behind it can take focus unseen.
import { useEffect, useId, useRef, useState } from 'react'
import type { AppRule, AppRulesInfo, GameSummary, ModeId, Preset, RuleFanMode, RuleOverrides } from '../types'
import { Button, Segmented, Stepper, Toggle, Unavailable } from '../components'
import { Icon } from '../components/Icon'
import { useToast } from '../Toast'
import { CANCEL_EVENT } from '../hooks/useSpatialNav'
import { AUTO_PROFILES_OFF, AUTO_PROFILES_OFF_SAVED, describeOverrides, displayName, exactRule, governingRule, hasOverrides } from '../gameProfile'
import { removeGameProfile, saveGameProfile } from '../gameProfileSave'
import { PRESET_LABEL } from './shared'
import { GameRecentSessions } from './GameRecentSessions'

const MIN_W = 5
const MAX_W = 40   // RuleOverridesPolicy's band; the daemon clamps and refuses outside it too
const clampW = (w: number) => Math.min(MAX_W, Math.max(MIN_W, Math.round(w)))

const DEFAULT = 'default'
// Off / 30 / 40 / 45 / 60: the caps that divide a 60 Hz panel's frame time evenly or nearly so (40 is
// the 120 Hz-friendly one). 0 means "cap off" to the daemon, null (DEFAULT here) "the mode decides".
const CAPS = ['0', '30', '40', '45', '60'] as const
const FANS: RuleFanMode[] = ['Auto', 'Quiet', 'Balanced', 'Aggressive']
// RSR / RIS sharpness, % (GpuImageRequest's band). 75 is where Adrenalin puts RSR out of the box; used
// only when a game turns a feature on without a stored value.
const DEFAULT_SHARPNESS = 75
const clampS = (v: number) => Math.min(100, Math.max(0, Math.round(v)))

/** A feature's sharpness while it is on: the same slider-plus-stepper as the TDP row. */
function SharpnessRow({ name, testid, value, onChange }: { name: string; testid: string; value: number; onChange: (v: number) => void }) {
  return (
    <div className="tdp-row">
      <input type="range" min={0} max={100} step={5} value={value} data-testid={`${testid}-slider`}
        aria-label={`${name} sharpness in percent`} aria-valuetext={`${value} %`}
        onChange={(e) => onChange(clampS(Number(e.target.value)))} />
      <Stepper label={`${name} sharpness`} value={value} unit="%" min={0} max={100} step={5} onChange={onChange}
        testid={testid} decTestid={`${testid}-dec`} incTestid={`${testid}-inc`} />
    </div>
  )
}

interface Props {
  game: GameSummary
  rules: readonly AppRule[]
  modes: readonly ModeId[]
  presets: Record<string, Preset> | null
  /** GET /app-rules `autoProfiles`: false = no focus worker, so a saved profile never applies. */
  autoProfiles?: boolean
  onSaved: (info: AppRulesInfo) => void
  onClose: () => void
}

export function GameProfileSheet({ game, rules, modes, presets, autoProfiles = true, onSaved, onClose }: Props) {
  const toast = useToast()
  const titleId = useId()
  const ref = useRef<HTMLElement>(null)
  const own = exactRule(rules, game.app)
  const governing = governingRule(rules, game.app)
  const stored = own?.overrides ?? null
  const name = displayName(game.app)

  const initialMode: ModeId = own?.mode ?? governing?.mode ?? (modes.includes('gaming') ? 'gaming' : modes[0] ?? 'gaming')
  const [mode, setMode] = useState<ModeId>(initialMode)
  const [tdpOn, setTdpOn] = useState(stored?.stapmW != null)
  const [tdp, setTdp] = useState(clampW(stored?.stapmW ?? presets?.[initialMode]?.stapmW ?? 20))
  const [cap, setCap] = useState<string>(stored?.frameCapFps == null ? DEFAULT : String(stored.frameCapFps))
  const [fan, setFan] = useState<string>(stored?.fanMode ?? DEFAULT)
  const [antiLag, setAntiLag] = useState(stored?.gpu?.antiLag === true)
  const [chill, setChill] = useState(stored?.gpu?.chill === true)
  const [rsr, setRsr] = useState(stored?.gpu?.rsr === true)
  const [rsrSharp, setRsrSharp] = useState(clampS(stored?.gpu?.rsrSharpness ?? DEFAULT_SHARPNESS))
  const [ris, setRis] = useState(stored?.gpu?.ris === true)
  const [risSharp, setRisSharp] = useState(clampS(stored?.gpu?.risSharpness ?? DEFAULT_SHARPNESS))
  const [busy, setBusy] = useState(false)

  // Focus moves in on open, so the next d-pad press acts inside the editor rather than on the card
  // behind it — onto the selected mode, where editing starts, not the close button. Below 1100px the
  // sheet is above the list, so it is also scrolled into view.
  useEffect(() => {
    const sheet = ref.current
    sheet?.scrollIntoView?.({ block: 'nearest' })
    const start = sheet?.querySelector<HTMLElement>('[role="radio"][aria-checked="true"]')
      ?? sheet?.querySelector<HTMLElement>('button:not([disabled])')
    start?.focus({ preventScroll: true })
  }, [game.app])

  // B on the pad / Escape — broadcast by the shell. Only while focus is in here: closing the command
  // palette must not close the editor under it.
  useEffect(() => {
    const onCancel = () => { if (ref.current?.contains(document.activeElement)) onClose() }
    window.addEventListener(CANCEL_EVENT, onCancel)
    return () => window.removeEventListener(CANCEL_EVENT, onCancel)
  }, [onClose])

  // A stored value outside the chips (a hand-edited 50) is still shown, so it is not lost on save.
  const capOptions = [...CAPS, ...(cap !== DEFAULT && !CAPS.includes(cap as typeof CAPS[number]) ? [cap] : [])]

  // Anti-Lag and Chill are exclusive: AMD refuses the pair, and the daemon refuses a profile asking for
  // both (bad_gpu). Turning one on turns the other off here, where the user can see it happen.
  const pickAntiLag = () => { setAntiLag(!antiLag); if (!antiLag) setChill(false) }
  const pickChill = () => { setChill(!chill); if (!chill) setAntiLag(false) }

  // Off keeps an explicit `false` the rule already had (set by hand or by an older client) rather than
  // quietly widening it to "the mode decides".
  const gpuValue = (on: boolean, before: boolean | null | undefined) => (on ? true : before === false ? false : null)
  const draft = (): RuleOverrides => {
    const a = chill ? null : gpuValue(antiLag, stored?.gpu?.antiLag)
    const c = gpuValue(chill, stored?.gpu?.chill)
    // A sharpness only rides with its feature ON: for "off" or "as the driver has it" it would be a
    // value nothing applies (the agent writes no sharpness for a feature it turns off).
    const r = gpuValue(rsr, stored?.gpu?.rsr)
    const i = gpuValue(ris, stored?.gpu?.ris)
    const gpu = { antiLag: a, chill: c, rsr: r, rsrSharpness: rsr ? rsrSharp : null, ris: i, risSharpness: ris ? risSharp : null }
    return {
      stapmW: tdpOn ? tdp : null,
      frameCapFps: cap === DEFAULT ? null : Number(cap),
      fanMode: fan === DEFAULT ? null : (fan as RuleFanMode),
      gpu: Object.values(gpu).every((v) => v == null) ? null : gpu,
      freeze: stored?.freeze ?? null,   // F5's list: not editable here yet, and never wiped by a save
    }
  }

  const run = async (action: () => Promise<AppRulesInfo>, done: string) => {
    setBusy(true)
    try {
      onSaved(await action())
      toast.push({ kind: 'success', message: done })
    } catch (e) {
      toast.push({ kind: 'error', message: e instanceof Error ? e.message : 'The profile was not saved' })
    } finally {
      setBusy(false)
    }
  }
  const save = () => {
    const o = draft()
    const saved = `Profile saved for ${name}: ${describeOverrides(hasOverrides(o) ? o : null)}`
    return run(() => saveGameProfile(game.app, mode, o), autoProfiles ? saved : `${saved} — ${AUTO_PROFILES_OFF_SAVED}`)
  }
  const remove = () => run(() => removeGameProfile(game.app), `Profile removed for ${name} — the mode decides again`)

  const toggleTdp = () => {
    // Opening the TDP row starts from the mode's own preset: the number the game runs at today.
    if (!tdpOn && stored?.stapmW == null) setTdp(clampW(presets?.[mode]?.stapmW ?? tdp))
    setTdpOn(!tdpOn)
  }

  return (
    <section className="card game-sheet" data-testid="game-sheet" ref={ref} aria-labelledby={titleId}>
      <div className="game-sheet-head">
        <div className="game-sheet-heading">
          <span className="eyebrow">Game profile</span>
          <h2 className="card-title game-sheet-title" id={titleId} data-testid="game-sheet-title">{name}</h2>
        </div>
        <button type="button" className="btn btn-ghost game-sheet-close" data-testid="game-sheet-close"
                aria-label="Close the profile editor" onClick={onClose}>
          <Icon name="close" size={18} />
        </button>
      </div>

      {governing && governing.id !== own?.id && (
        <p className="muted" data-testid="game-sheet-inherited">
          Right now the <code>{governing.match}</code> rule decides this game ({PRESET_LABEL[governing.mode] ?? governing.mode}).
          Saving gives {name} a rule of its own, placed above it.
        </p>
      )}
      {own && !own.enabled && (
        <p className="muted" data-testid="game-sheet-disabled">This game's rule is switched off; saving turns it back on.</p>
      )}

      <div className="sheet-field">
        <span className="sheet-label">Mode</span>
        <Segmented label="Mode for this game" testid="game-mode" value={mode} onChange={setMode}
          options={modes.map((m) => ({ id: m, label: PRESET_LABEL[m] ?? m, testid: `game-mode-${m}` }))} />
      </div>

      <div className="sheet-field">
        <Toggle on={tdpOn} onClick={toggleTdp} label="Set TDP for this game" testid="game-tdp-toggle" />
        {tdpOn && (
          <div className="tdp-row">
            <input type="range" min={MIN_W} max={MAX_W} step={1} value={tdp} data-testid="game-tdp-slider"
              aria-label="TDP for this game in watts" aria-valuetext={`${tdp} W`}
              onChange={(e) => setTdp(clampW(Number(e.target.value)))} />
            <Stepper label="TDP for this game" value={tdp} unit="W" min={MIN_W} max={MAX_W} onChange={setTdp}
              testid="game-tdp" decTestid="game-tdp-dec" incTestid="game-tdp-inc" />
          </div>
        )}
      </div>

      <div className="sheet-field">
        <span className="sheet-label">Frame cap</span>
        <Segmented label="Frame cap for this game" testid="game-cap" value={cap} onChange={setCap}
          options={[
            ...capOptions.map((c) => ({ id: c, label: c === '0' ? 'Off' : c, testid: `game-cap-${c}` })),
            { id: DEFAULT, label: 'Mode default', testid: 'game-cap-default' },
          ]} />
      </div>

      <div className="sheet-field">
        <span className="sheet-label">Fan</span>
        <Segmented label="Fan for this game" testid="game-fan" value={fan} onChange={setFan}
          options={[
            ...FANS.map((f) => ({ id: f, label: f, testid: `game-fan-${f}` })),
            { id: DEFAULT, label: 'Mode default', testid: 'game-fan-default' },
          ]} />
      </div>

      <div className="sheet-field">
        <span className="sheet-label">Radeon</span>
        <div className="row">
          <Toggle on={antiLag} onClick={pickAntiLag} label="Anti-Lag" testid="game-antilag" />
          <Toggle on={chill} onClick={pickChill} label="Chill" testid="game-chill" />
        </div>
        <p className="muted">Off leaves it to the mode. AMD refuses the two together, so one turns the other off.</p>
      </div>

      <div className="sheet-field">
        <span className="sheet-label">Image</span>
        <Toggle on={rsr} onClick={() => setRsr(!rsr)} label="Radeon Super Resolution" testid="game-rsr" />
        {rsr && <SharpnessRow name="RSR" testid="game-rsr-sharpness" value={rsrSharp} onChange={setRsrSharp} />}
        <Toggle on={ris} onClick={() => setRis(!ris)} label="Image Sharpening" testid="game-ris" />
        {ris && <SharpnessRow name="Image Sharpening" testid="game-ris-sharpness" value={risSharp} onChange={setRisSharp} />}
        <p className="muted">
          RSR renders below the screen's resolution and upscales — the most frames inside the 22 W limit; set the
          game to a lower fullscreen resolution for it to act. Turns Radeon Boost off. Off leaves the driver's own
          setting; what a game changes is put back when you leave it.
        </p>
      </div>

      {autoProfiles ? (
        <p className="muted game-sheet-note">
          Applies automatically while {name} is in front and the mode is {PRESET_LABEL[mode] ?? mode}, and comes
          off when you leave it.
        </p>
      ) : (
        <Unavailable testid="game-auto-off" reason={`Saved profiles are kept, but ${AUTO_PROFILES_OFF}: ${name} will run on its mode alone.`} />
      )}

      <div className="row game-sheet-actions">
        <Button variant="accent" testid="game-save" onClick={save} disabled={busy}>{busy ? 'Saving…' : 'Save profile'}</Button>
        {hasOverrides(stored) && (
          <Button variant="danger" testid="game-remove" onClick={remove} disabled={busy}>Remove profile</Button>
        )}
      </div>

      {/* Below the actions: the history is for judging the profile, and must never push Save out of
          reach (the 1280x800 large-text check in games-page.spec). */}
      <GameRecentSessions app={game.app} />
    </section>
  )
}
