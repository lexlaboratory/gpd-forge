// GPD Forge UI — first-run setup wizard. GPL-3.0-or-later.
//
// Shown once: the first time this browser/profile opens GPD Forge (localStorage['forge-setup-done']
// is absent). Steps: (1) welcome, (2) incumbent power-controller check, (3) pick a default mode,
// (4) finish. Skippable at any step — either path marks the flag done so it never nags again.
//
// Self-contained: reads/writes its own localStorage flag and talks to the API directly, so mounting
// it from App.tsx is a single additive line (see App.tsx's `showWizard` state).
import { useEffect, useRef, useState } from 'react'
import type { IncumbentsInfo, ModeId } from './types'
import { getIncumbents, setMode as apiSetMode } from './api'
import { useFocusTrap } from './hooks/useFocusTrap'
import { MODES } from './pages'

export const SETUP_DONE_KEY = 'forge-setup-done'

/** True once setup has been completed or skipped. Fails open (true) if storage is unavailable —
 * never nag when we can't tell. */
export function isSetupDone(): boolean {
  try { return localStorage.getItem(SETUP_DONE_KEY) === '1' } catch { return true }
}

function markSetupDone() {
  try { localStorage.setItem(SETUP_DONE_KEY, '1') } catch { /* storage unavailable — nothing to persist */ }
}

type Step = 'welcome' | 'incumbents' | 'mode' | 'finish'
const STEPS: Step[] = ['welcome', 'incumbents', 'mode', 'finish']
const STEP_LABEL: Record<Step, string> = {
  welcome: 'Welcome', incumbents: 'Check for conflicts', mode: 'Default mode', finish: 'Done',
}

export function Wizard({ onClose }: { onClose: () => void }) {
  const cardRef = useRef<HTMLDivElement>(null)
  useFocusTrap(cardRef)
  const [step, setStep] = useState<Step>('welcome')
  const [incumbents, setIncumbents] = useState<IncumbentsInfo | null>(null)
  const [checking, setChecking] = useState(false)
  const [defaultMode, setDefaultMode] = useState<ModeId>('windows')

  // Run the incumbents check once, when that step is reached.
  useEffect(() => {
    if (step !== 'incumbents' || incumbents || checking) return
    setChecking(true)
    getIncumbents()
      .then(setIncumbents)
      .catch(() => setIncumbents(null))
      .finally(() => setChecking(false))
  }, [step, incumbents, checking])

  const idx = STEPS.indexOf(step)
  const goNext = () => setStep(STEPS[Math.min(idx + 1, STEPS.length - 1)])
  const goBack = () => setStep(STEPS[Math.max(idx - 1, 0)])

  const finish = () => {
    void apiSetMode(defaultMode).catch(() => { /* best effort — Settings/Dashboard still let you pick */ })
    markSetupDone()
    onClose()
  }
  const skip = () => { markSetupDone(); onClose() }

  const conflict = !!incumbents && (incumbents.motionAssistant || incumbents.gpdTool)
  const conflictingNames = incumbents
    ? [incumbents.motionAssistant && 'MotionAssistant', incumbents.gpdTool && 'GPD Tool'].filter(Boolean).join(' and ')
    : ''

  return (
    <div className="wizard-overlay" data-testid="wizard">
      <div className="wizard-card" ref={cardRef} role="dialog" aria-modal="true" aria-label="GPD Forge first-run setup">
        <div className="wizard-steps" aria-hidden="true">
          {STEPS.map((s) => <span key={s} className={`wizard-dot ${s === step ? 'on' : ''}`} title={STEP_LABEL[s]} />)}
        </div>

        {step === 'welcome' && (
          <>
            <h2 className="card-title">Welcome to GPD Forge</h2>
            <p className="muted">
              A quick 3-step setup: make sure no other power tool is fighting for control, pick a
              default mode, and you're ready. About 20 seconds.
            </p>
          </>
        )}

        {step === 'incumbents' && (
          <>
            <h2 className="card-title">Checking for other power tools</h2>
            <p className="muted" data-testid="wizard-incumbents-status">
              {checking && 'Checking…'}
              {!checking && incumbents && (conflict
                ? `Found ${conflictingNames} running. Run the installer with -Substitute to stop and disable it — GPD Forge only takes over TDP once it's the sole power controller.`
                : 'Clear — no conflicting power controller is running.')}
              {!checking && !incumbents && 'Could not reach the daemon to check — you can re-check anytime from the System page.'}
            </p>
          </>
        )}

        {step === 'mode' && (
          <>
            <h2 className="card-title">Pick a default mode</h2>
            <p className="muted">You can change this anytime — from the Dashboard, or automatically per-app.</p>
            <div className="chips" data-testid="wizard-modes">
              {MODES.map((m) => (
                <button key={m.id} type="button" className={`chip-btn ${defaultMode === m.id ? 'on' : ''}`}
                  onClick={() => setDefaultMode(m.id)} data-testid={`wizard-mode-${m.id}`}>
                  <span aria-hidden="true">{m.icon}</span> {m.label}
                </button>
              ))}
            </div>
          </>
        )}

        {step === 'finish' && (
          <>
            <h2 className="card-title">All set</h2>
            <p className="muted">
              GPD Forge will start in <b>{MODES.find((m) => m.id === defaultMode)?.label}</b> mode.
              Everything here — modes, presets, fan, guardian — stays adjustable from the app.
            </p>
          </>
        )}

        <div className="row-end wizard-actions">
          <button type="button" className="btn" data-testid="wizard-skip" onClick={skip}>Skip</button>
          {idx > 0 && <button type="button" className="btn" data-testid="wizard-back" onClick={goBack}>Back</button>}
          {step !== 'finish'
            ? <button type="button" className="btn btn-accent" data-testid="wizard-next" onClick={goNext}>Next</button>
            : <button type="button" className="btn btn-accent" data-testid="wizard-finish" onClick={finish}>Finish</button>}
        </div>
      </div>
    </div>
  )
}
