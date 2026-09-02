// GPD Forge — command palette. GPL-3.0-or-later.
//
// Ctrl+K opens a single line that reaches everything: modes, TDP, brightness, panic cool, and every
// section of the app. It fits the HUD the way a console fits an instrument panel, and it is the
// fastest path on a handheld where the alternative is walking a d-pad across ten sidebar entries.
//
// Deliberately not a fuzzy matcher over a giant action registry: the command list is small enough to
// read, and a command that takes an argument (`tdp 25`) parses it rather than making you pick from a
// submenu.
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ModeId } from './types'
import { setMode, setTdp, setBrightness, panicCool, setFan } from './api'
import { useToast } from './Toast'
import { useFocusTrap } from './hooks/useFocusTrap'

export interface Command {
  id: string
  /** What the user types. Matched as a prefix, so `mo` finds `mode gaming`. */
  title: string
  hint: string
  /** Present when the command consumes a trailing number, e.g. `tdp 25`. */
  arg?: { label: string; min: number; max: number }
  run: (arg?: number) => Promise<string>
}

// Mirrors core/Profiles/Modes.cs. ModeCatalogueTests parses this line.
const MODE_IDS: ModeId[] = ['gaming', 'gaming-battery', 'ai', 'windows', 'battery', 'standby']
const FAN_MODES = ['auto', 'quiet', 'balanced', 'aggressive']

/** Commands that navigate are supplied by the shell, which owns routing. */
export function buildCommands(navigate: (page: string) => void, pages: readonly string[]): Command[] {
  return [
    ...MODE_IDS.map((m): Command => ({
      id: `mode-${m}`,
      title: `mode ${m}`,
      hint: 'switch power mode',
      run: async () => { await setMode(m); return `Mode → ${m}` },
    })),
    {
      id: 'tdp',
      title: 'tdp',
      hint: 'set sustained watts',
      arg: { label: 'watts', min: 5, max: 40 },
      run: async (w) => {
        if (w === undefined) throw new Error('tdp needs a number, e.g. "tdp 25"')
        const r = await setTdp(w)
        // Report what the hardware actually did, not what we asked for — the closed loop can come
        // back with a different figure, and that is the number worth showing.
        return r.verified ? `TDP → ${r.observed} W (verified)` : `TDP requested ${r.requested} W — not verified`
      },
    },
    {
      id: 'brightness',
      title: 'brightness',
      hint: 'set panel brightness',
      arg: { label: 'percent', min: 0, max: 100 },
      run: async (v) => {
        if (v === undefined) throw new Error('brightness needs a number, e.g. "brightness 60"')
        return `Brightness → ${await setBrightness(v)}%`
      },
    },
    ...FAN_MODES.map((f): Command => ({
      id: `fan-${f}`,
      title: `fan ${f}`,
      hint: 'set fan curve',
      run: async () => {
        const applied = await setFan(f.charAt(0).toUpperCase() + f.slice(1))
        return `Fan → ${applied}`
      },
    })),
    {
      id: 'panic',
      title: 'panic cool',
      hint: 'floor TDP and max the fan, now',
      run: async () => {
        const r = await panicCool()
        return r.applied ? `Panic cool applied — ${r.stapmW} W floor` : `Panic cool requested — ${r.stapmW} W floor not verified`
      },
    },
    ...pages.map((p): Command => ({
      id: `go-${p}`,
      title: `go ${p}`,
      hint: 'open section',
      run: async () => { navigate(p); return `Opened ${p}` },
    })),
  ]
}

const parse = (input: string, commands: Command[]) => {
  const q = input.trim().toLowerCase()
  if (!q) return { matches: commands, arg: undefined as number | undefined }
  // Split a trailing number off so `tdp 25` still matches the `tdp` command.
  const m = q.match(/^(.*?)\s+(-?\d+(?:\.\d+)?)$/)
  const [text, arg] = m ? [m[1].trim(), Number(m[2])] : [q, undefined]
  const matches = commands.filter((c) => c.title.startsWith(text) || c.title.includes(text))
  return { matches, arg }
}

export function CommandPalette({ navigate, pages }: { navigate: (page: string) => void; pages: readonly string[] }) {
  const [open, setOpen] = useState(false)
  const [input, setInput] = useState('')
  const [cursor, setCursor] = useState(0)
  const [busy, setBusy] = useState(false)
  const boxRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const toast = useToast()
  useFocusTrap(boxRef, open)

  const commands = useMemo(() => buildCommands(navigate, pages), [navigate, pages])
  const { matches, arg } = useMemo(() => parse(input, commands), [input, commands])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault()
        setOpen((o) => !o)
        setInput(''); setCursor(0)
      } else if (e.key === 'Escape') setOpen(false)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  useEffect(() => { if (open) inputRef.current?.focus() }, [open])
  useEffect(() => { setCursor(0) }, [input])

  const run = useCallback(async (cmd: Command) => {
    setBusy(true)
    try {
      if (cmd.arg && arg !== undefined && (arg < cmd.arg.min || arg > cmd.arg.max)) {
        throw new Error(`${cmd.arg.label} must be between ${cmd.arg.min} and ${cmd.arg.max}`)
      }
      const message = await cmd.run(arg)
      toast.push({ kind: 'success', message })
      setOpen(false)
    } catch (e) {
      toast.push({ kind: 'error', message: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }, [arg, toast])

  if (!open) return null

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setCursor((c) => Math.min(c + 1, matches.length - 1)) }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setCursor((c) => Math.max(c - 1, 0)) }
    else if (e.key === 'Enter' && matches[cursor]) { e.preventDefault(); void run(matches[cursor]) }
  }

  return (
    <div className="palette-scrim" data-testid="palette" onClick={() => setOpen(false)}>
      <div className="palette" ref={boxRef} role="dialog" aria-modal="true" aria-label="Command palette"
           onClick={(e) => e.stopPropagation()}>
        <div className="palette-input-row">
          <span className="palette-prompt" aria-hidden>&gt;</span>
          <input
            ref={inputRef} className="palette-input" value={input} disabled={busy}
            data-testid="palette-input" aria-label="Command"
            placeholder="mode gaming · tdp 25 · panic cool · go monitor"
            onChange={(e) => setInput(e.target.value)} onKeyDown={onKeyDown}
          />
        </div>
        <ul className="palette-list" role="listbox" aria-label="Commands" data-testid="palette-list">
          {matches.length === 0 && <li className="palette-empty">No command matches that.</li>}
          {matches.slice(0, 8).map((c, i) => (
            <li key={c.id}>
              <button
                type="button" role="option" aria-selected={i === cursor}
                className={`palette-item${i === cursor ? ' on' : ''}`}
                data-testid={`palette-${c.id}`}
                onMouseEnter={() => setCursor(i)} onClick={() => void run(c)}
              >
                <span className="palette-title">{c.title}{c.arg && <em> {c.arg.label}</em>}</span>
                <span className="palette-hint">{c.hint}</span>
              </button>
            </li>
          ))}
        </ul>
      </div>
    </div>
  )
}
