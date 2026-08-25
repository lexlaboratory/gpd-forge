// GPD Forge UI — Standby Doctor panel. GPL-3.0-or-later.
import { useEffect, useState } from 'react'
import type { Standby } from './types'
import { getStandby, restoreStandby } from './api'

export function StandbyPanel() {
  const [info, setInfo] = useState<Standby | null>(null)
  const [restored, setRestored] = useState<string[] | null>(null)

  useEffect(() => { getStandby().then(setInfo).catch(() => {}) }, [])

  const onRestore = async () => {
    const r = await restoreStandby().catch(() => null)
    if (r) setRestored(r.restored)
  }

  return (
    <section className="panel" data-testid="standby-panel" aria-label="Standby Doctor">
      <h2 className="section-title">Standby Doctor</h2>
      <p className="panel-note">
        Modern Standby on the Win 4 loses TDP, fan and HID state on resume and can drain overnight.
        The daemon restores state automatically on resume; you can also trigger it here.
      </p>

      <div className="standby-grid">
        <div className="standby-metric" data-testid="standby-drain">
          <span className="tile-label">Overnight drain</span>
          <span className="tile-value">{info ? info.lastDrainPctPerHour : '--'}<span className="tile-unit">%/h</span></span>
        </div>
        <div className="standby-metric">
          <span className="tile-label">Top wake reason</span>
          <span className="standby-wake" data-testid="standby-wake">{info?.topWakeReason ?? '--'}</span>
        </div>
        <div className="standby-metric">
          <span className="tile-label">Sleep blockers</span>
          <span className="standby-wake">{info?.blockers?.join(', ') || 'none'}</span>
        </div>
      </div>

      <div className="standby-actions">
        <button className="btn btn-accent" data-testid="standby-restore" onClick={onRestore}>Run resume restore</button>
        {restored && (
          <span className="standby-restored" data-testid="standby-restored">
            Restored: {restored.map((r) => <span key={r} className="chip">{r}</span>)}
          </span>
        )}
      </div>
    </section>
  )
}
