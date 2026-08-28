// GPD Forge UI — Profiles page (per-app rules + MotionAssistant import). GPL-3.0-or-later.
import { useState } from 'react'
import type { ImportResult } from '../types'
import { importMotionAssistant } from '../api'
import { Frame, Badge, Button, Soon, Unavailable } from '../components'
import { useToast } from '../Toast'

// --- Profiles -----------------------------------------------------------------
// Hardcoded in the UI. Nothing here is read back from the daemon's matcher, which is why the card
// below labels itself an example instead of a status readout.
export const RULES = [
  { app: 'ollama / lmstudio / koboldcpp', mode: 'Agents / AI' },
  { app: 'steam / retroarch / emulators', mode: 'Gaming' },
  { app: 'anything else (on AC)', mode: 'Windows' },
  { app: 'anything else (on battery)', mode: 'Battery' },
]
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

export function ProfilesPage() {
  return (
    <>
      <MotionAssistantImportCard />
      <Frame title="Per-app profiles" hint={<Badge tone="warn">example</Badge>} testid="per-app-profiles">
        <Unavailable
          testid="rules-not-live"
          reason="This table is written into the UI, not read from the daemon. It illustrates the shape of the foreground-app matcher; it is not the ruleset currently running, and no rule shown here can be edited yet."
        />
        <ul className="rules" aria-label="Example rules — not the active ruleset">
          {RULES.map((r) => (
            <li key={r.app} className="rule"><span className="rule-app">{r.app}</span><span className="rule-arrow">→</span><span className="rule-mode">{r.mode}</span></li>
          ))}
        </ul>
        <p className="muted">Reading the live rules back from the daemon, plus custom, versioned, shareable per-game profiles (import from the community) — <Soon />.</p>
      </Frame>
    </>
  )
}
