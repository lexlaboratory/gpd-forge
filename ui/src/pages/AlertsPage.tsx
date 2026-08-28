// GPD Forge UI — Alerts page (alert center). GPL-3.0-or-later.
import { useEffect, useState } from 'react'
import type { AlertEvent } from '../types'
import { getAlerts, acknowledgeAlert, acknowledgeAllAlerts, deleteAlert } from '../api'
import { Frame, Segmented, Button, Badge } from '../components'
import type { Tone } from '../components'

const FILTERS = [
  { id: 'all', label: 'All' },
  { id: 'info', label: 'Info' },
  { id: 'aviso', label: 'Aviso' },
  { id: 'critica', label: 'Critica' },
] as const
type FilterId = typeof FILTERS[number]['id']

const TONES: Record<string, Tone> = { critica: 'danger', aviso: 'warn', info: 'info' }

export function AlertsPage({ onChanged }: { onChanged?: () => void }) {
  const [items, setItems] = useState<AlertEvent[]>([])
  const [filter, setFilter] = useState<FilterId>('all')
  const [loading, setLoading] = useState(true)
  const load = () => getAlerts(false, 500).then((r) => setItems(r.alerts)).catch(() => {}).finally(() => setLoading(false))
  useEffect(() => { load() }, [])
  // Never assume the wire type. `severity` is a C# enum on the daemon side, and when it went out as
  // an ordinal instead of a name, calling .toLowerCase() on it threw during render and took the
  // whole app down with it. The daemon now sends names (JsonStringEnumConverter in Program.cs);
  // this keeps a future contract slip from being fatal a second time.
  const sev = (a: AlertEvent) => String(a.severity ?? '').toLowerCase()
  const visible = filter === 'all' ? items : items.filter((x) => sev(x) === filter)
  const ack = async (id: string) => { await acknowledgeAlert(id).catch(() => {}); await load(); onChanged?.() }
  const ackAll = async () => { await acknowledgeAllAlerts().catch(() => {}); await load(); onChanged?.() }
  const remove = async (id: string) => { await deleteAlert(id).catch(() => {}); await load(); onChanged?.() }
  return (
    <div className="alerts-page">
      <Frame title="Alert center" hint="Local events from GPD Forge">
        <div className="row">
          <Segmented options={FILTERS} value={filter} onChange={setFilter} label="Alert severity filter" />
          <Button variant="ghost" onClick={ackAll} disabled={!items.some((x) => !x.acknowledged)}>Mark all read</Button>
        </div>
        {loading && <p className="muted">Loading alerts…</p>}
        {!loading && visible.length === 0 && <p className="muted" data-testid="alerts-empty">No alerts — your system is quiet.</p>}
        <div className="alert-list" aria-live="polite">
          {visible.map((a) => <article key={a.id} className={`alert-card alert-${sev(a)} ${a.acknowledged ? 'read' : 'unread'}`}>
            <div className="alert-card-head">
              {/* Tone comes from the same normalised string as the border colour, so a severity the
                  daemon has never sent degrades to a neutral pill instead of a wrong one. */}
              <Badge tone={TONES[sev(a)] ?? 'muted'}>{sev(a) || 'unknown'}</Badge>
              <span className="muted">{new Date(a.timestampUtc).toLocaleString()}</span>
            </div>
            <h3>{a.title}</h3><p>{a.message}</p>
            {a.technicalData && <details><summary>Technical details</summary><pre>{a.technicalData}</pre></details>}
            <div className="row-end">
              {!a.acknowledged && <Button variant="ghost" onClick={() => ack(a.id)}>Mark read</Button>}
              <Button variant="ghost" onClick={() => remove(a.id)}>Delete</Button>
            </div>
          </article>)}
        </div>
      </Frame>
    </div>
  )
}
