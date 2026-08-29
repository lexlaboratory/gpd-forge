// GPD Forge UI — Profiles page (per-app rules + MotionAssistant import). GPL-3.0-or-later.
import { useCallback, useEffect, useState } from 'react'
import type { AppRule, AppRulesInfo, ImportResult, ModeId } from '../types'
import {
  importMotionAssistant,
  getAppRules, addAppRule, updateAppRule, deleteAppRule, moveAppRule,
} from '../api'
import { Frame, Badge, Button, Chip, Segmented, Unavailable } from '../components'
import { useToast } from '../Toast'

// Only used before the first response arrives (or while the daemon is down): the daemon is the
// authority on which modes a rule may select, and it sends them with every ruleset.
const FALLBACK_MODES: ModeId[] = ['battery', 'windows', 'gaming', 'ai']

export function MotionAssistantImportCard() {
  const toast = useToast()
  const [result, setResult] = useState<ImportResult | null>(null)
  const [busy, setBusy] = useState(false)

  const doImport = async () => {
    setBusy(true)
    const r = await importMotionAssistant().catch(() => null)
    setBusy(false)
    if (!r) { toast.push({ kind: 'error', message: 'MotionAssistant import failed' }); return }
    setResult(r)
    toast.push({
      kind: r.found > 0 ? 'success' : 'info',
      message: r.found > 0 ? `Imported ${r.found} profile${r.found === 1 ? '' : 's'} from MotionAssistant` : `No MotionAssistant profiles found at ${r.path}`,
    })
  }

  return (
    <Frame
      title="Import from MotionAssistant"
      hint={result
        ? <Badge tone={result.found > 0 ? 'ok' : 'muted'}>{result.found} found</Badge>
        : "Reads MotionAssistant's saved per-profile TDP"}
    >
      <div className="row">
        <Button variant="accent" testid="import-ma" onClick={doImport} disabled={busy}>
          {busy ? 'Importing…' : 'Import from MotionAssistant'}
        </Button>
      </div>
      {result && (
        <>
          <ul className="rules" data-testid="import-ma-results">
            {result.profiles.length === 0 && <li className="rule">No profiles found at {result.path}</li>}
            {result.profiles.map((p) => (
              <li key={p.name} className="rule">
                <span className="rule-app">{p.name}</span>
                <span className="rule-arrow">→</span>
                <span className="rule-mode">{p.stapmW}/{p.fastW}/{p.slowW} W · {p.tctlC}°C</span>
              </li>
            ))}
          </ul>
          <p className="muted">Read from <code>{result.path}</code>.</p>
        </>
      )}
      <p className="muted">Apply an imported profile's numbers on the Power page's presets — this only reads and lists them.</p>
    </Frame>
  )
}

/** Editor for one rule — reused for the row being edited and for the "add" line. */
function RuleForm({ match, mode, modes, onMatch, onMode, onSave, onCancel, saveLabel, testid, busy }: {
  match: string
  mode: ModeId
  modes: ModeId[]
  onMatch: (v: string) => void
  onMode: (v: ModeId) => void
  onSave: () => void
  onCancel?: () => void
  saveLabel: string
  testid: string
  busy: boolean
}) {
  return (
    <div className="row" data-testid={testid}>
      <input
        className="job-input"
        value={match}
        placeholder="process name (e.g. cyberpunk2077)"
        aria-label="Process name to match"
        data-testid={`${testid}-match`}
        onChange={(e) => onMatch(e.target.value)}
        onKeyDown={(e) => { if (e.key === 'Enter') onSave() }}
      />
      <Segmented
        label="Mode for this app"
        testid={`${testid}-mode`}
        value={mode}
        options={modes.map((m) => ({ id: m, label: m, testid: `${testid}-mode-${m}` }))}
        onChange={onMode}
      />
      <Button variant="accent" testid={`${testid}-save`} onClick={onSave} disabled={busy || match.trim() === ''}>
        {saveLabel}
      </Button>
      {onCancel && <Button variant="ghost" testid={`${testid}-cancel`} onClick={onCancel}>Cancel</Button>}
    </div>
  )
}

export function PerAppRulesCard() {
  const toast = useToast()
  const [info, setInfo] = useState<AppRulesInfo | null>(null)
  const [offline, setOffline] = useState(false)
  const [busy, setBusy] = useState(false)
  const [editing, setEditing] = useState<AppRule | null>(null)
  const [draftMatch, setDraftMatch] = useState('')
  const [draftMode, setDraftMode] = useState<ModeId>('gaming')

  const load = useCallback(async () => {
    const r = await getAppRules().catch(() => null)
    if (r) { setInfo(r); setOffline(false) } else setOffline(true)
  }, [])

  // Polls so the "deciding right now" readout tracks the foreground app. Paused while a row is
  // being edited: a refresh mid-edit would yank the form out from under the keyboard.
  useEffect(() => {
    load()
    if (editing) return
    const id = setInterval(load, 3000)
    return () => clearInterval(id)
  }, [load, editing])

  const run = async (action: () => Promise<AppRulesInfo>, ok: string) => {
    setBusy(true)
    try {
      setInfo(await action())
      setOffline(false)
      toast.push({ kind: 'success', message: ok })
      return true
    } catch (e) {
      toast.push({ kind: 'error', message: e instanceof Error ? e.message : 'Rule change failed' })
      return false
    } finally {
      setBusy(false)
    }
  }

  const modes = info?.modes?.length ? info.modes : FALLBACK_MODES
  const active = info?.lastMatch ?? null
  const activeRule = active?.ruleId ? info?.rules.find((r) => r.id === active.ruleId) ?? null : null

  const doAdd = async () => {
    if (await run(() => addAppRule(draftMatch.trim(), draftMode), `Rule added for "${draftMatch.trim()}"`)) setDraftMatch('')
  }
  const doSaveEdit = async () => {
    if (!editing) return
    const target = editing
    if (await run(() => updateAppRule(target.id, { match: target.match, mode: target.mode, enabled: target.enabled }), 'Rule saved')) setEditing(null)
  }

  return (
    <Frame
      title="Per-app profiles"
      testid="per-app-profiles"
      hint={info && <Badge tone={info.autoProfiles ? 'ok' : 'muted'}>{info.autoProfiles ? 'live' : 'auto-switching off'}</Badge>}
    >
      {offline && <Unavailable testid="rules-offline" reason="The daemon is not answering, so these rules cannot be read or changed right now." />}

      {info && !info.autoProfiles && (
        <Unavailable
          testid="rules-auto-off"
          reason="Automatic profile switching is disabled (GPDFORGE_AUTO_PROFILES=0). These rules are stored and editable, but nothing is applying them."
        />
      )}

      <p className="muted" data-testid="active-rule">
        {!info || !active
          ? 'Waiting for the first foreground sample…'
          : activeRule
            ? <>Now: <strong>{active.mode}</strong> — matched <code>{activeRule.match}</code> against <code>{active.process ?? 'unknown'}</code>.</>
            : <>Now: <strong>{active.mode}</strong> — no rule matched <code>{active.process ?? 'unknown'}</code>, so this is the {active.acConnected ? 'AC' : 'battery'} default.</>}
      </p>

      <ul className="rules" aria-label="Per-app rules, highest precedence first">
        {info?.rules.length === 0 && <li className="rule" data-testid="rules-empty">No rules. Every app falls back to the AC/battery default.</li>}
        {info?.rules.map((r, i) => (
          <li key={r.id} className="rule cap-row" data-testid="rule-row">
            {editing?.id === r.id ? (
              <RuleForm
                testid="rule-edit" busy={busy} saveLabel="Save"
                match={editing.match} mode={editing.mode} modes={modes}
                onMatch={(v) => setEditing({ ...editing, match: v })}
                onMode={(v) => setEditing({ ...editing, mode: v })}
                onSave={doSaveEdit}
                onCancel={() => setEditing(null)}
              />
            ) : (
              <>
                <span className="rule-app">
                  {r.match}{!r.enabled && ' '}
                  {!r.enabled && <Badge tone="muted">off</Badge>}
                  {activeRule?.id === r.id && <> <Badge tone="ok" testid="rule-active">deciding now</Badge></>}
                </span>
                <span className="rule-arrow">→</span>
                <span className="rule-mode">{r.mode}</span>
                <span className="row">
                  <Chip on={r.enabled} testid="rule-toggle" title="Enable or disable this rule" disabled={busy}
                    onClick={() => run(() => updateAppRule(r.id, { match: r.match, mode: r.mode, enabled: !r.enabled }), r.enabled ? 'Rule disabled' : 'Rule enabled')}>
                    {r.enabled ? 'on' : 'off'}
                  </Chip>
                  <Button testid="rule-up" title="Higher precedence" disabled={busy || i === 0}
                    onClick={() => run(() => moveAppRule(r.id, -1), 'Rule moved up')}>↑</Button>
                  <Button testid="rule-down" title="Lower precedence" disabled={busy || i === info.rules.length - 1}
                    onClick={() => run(() => moveAppRule(r.id, 1), 'Rule moved down')}>↓</Button>
                  <Button testid="rule-edit-start" disabled={busy} onClick={() => setEditing(r)}>Edit</Button>
                  <Button variant="danger" testid="rule-delete" disabled={busy}
                    onClick={() => run(() => deleteAppRule(r.id), `Rule "${r.match}" deleted`)}>Delete</Button>
                </span>
              </>
            )}
          </li>
        ))}
      </ul>

      <RuleForm
        testid="rule-add" busy={busy || offline} saveLabel="Add rule"
        match={draftMatch} mode={draftMode} modes={modes}
        onMatch={setDraftMatch} onMode={setDraftMode} onSave={doAdd}
      />

      <p className="muted">
        Matched as a lowercase substring of the foreground process name (<code>.exe</code> ignored), first
        enabled rule wins — use ↑/↓ when two rules could claim the same app. Anything unmatched falls back
        to the AC/battery default.
      </p>
    </Frame>
  )
}

export function ProfilesPage() {
  return (
    <>
      <MotionAssistantImportCard />
      <PerAppRulesCard />
    </>
  )
}
