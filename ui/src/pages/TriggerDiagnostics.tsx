import { useEffect, useState } from 'react'
import { Frame } from '../components'

interface TriggerValue { value: number; max: number | null }
type PadState = 'none' | 'unsupported' | 'standard'

export function TriggerDiagnostics() {
  const [padState, setPadState] = useState<PadState>('none')
  const [left, setLeft] = useState<TriggerValue>({ value: 0, max: null })
  const [right, setRight] = useState<TriggerValue>({ value: 0, max: null })

  useEffect(() => {
    let frame = 0
    let active = true
    let maxLeft: number | null = null
    let maxRight: number | null = null
    const sample = () => {
      if (!active) return
      const pads = typeof navigator.getGamepads === 'function' ? Array.from(navigator.getGamepads()) : []
      const pad = pads.find((entry) => entry != null)
      if (!pad) {
        setPadState('none')
        setLeft({ value: 0, max: maxLeft })
        setRight({ value: 0, max: maxRight })
      } else if (pad.mapping !== 'standard' || pad.buttons.length <= 7) {
        setPadState('unsupported')
      } else {
        const l = Math.min(1, Math.max(0, pad.buttons[6]?.value ?? 0))
        const r = Math.min(1, Math.max(0, pad.buttons[7]?.value ?? 0))
        maxLeft = Math.max(maxLeft ?? 0, l)
        maxRight = Math.max(maxRight ?? 0, r)
        setPadState('standard')
        setLeft({ value: l, max: maxLeft })
        setRight({ value: r, max: maxRight })
      }
      frame = requestAnimationFrame(sample)
    }
    frame = requestAnimationFrame(sample)
    return () => { active = false; cancelAnimationFrame(frame) }
  }, [])

  const pct = (value: number) => `${Math.round(value * 100)}%`
  const row = (name: string, id: string, trigger: TriggerValue) => (
    <div className="grid2" data-testid={id}>
      <span>{name}</span>
      <span>Live: {padState === 'standard' ? pct(trigger.value) : '--'}</span>
      <span>Maximum observed: {trigger.max == null ? '--' : pct(trigger.max)}</span>
      <span>{padState !== 'standard' ? 'Unknown' : trigger.value > 0.05 ? 'Hold' : 'Released'}</span>
    </div>
  )

  return (
    <Frame title="Trigger diagnostics" hint="Read-only live input from standard-mapped controllers." testid="trigger-diagnostics">
      {padState === 'none' && <p className="muted" role="status">No controller detected</p>}
      {padState === 'unsupported' && <p className="muted" role="status">Controller mapping unsupported; L2/R2 values are unavailable.</p>}
      {row('L2', 'trigger-left', left)}
      {row('R2', 'trigger-right', right)}
      <p className="muted">Press and hold L2 or R2, then release to see its live value and maximum observed. Only standard controller mapping is supported. This panel is read-only.</p>
    </Frame>
  )
}
