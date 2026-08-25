// GPD Forge UI — Jobs panel (Agents / AI mode). GPL-3.0-or-later.
// Enqueue local-AI jobs with constraints; the daemon runs them only while constraints hold.
import { useEffect, useState, type FormEvent } from 'react'
import type { Job } from './types'
import { getJobs, createJob } from './api'

export function JobsPanel() {
  const [jobs, setJobs] = useState<Job[]>([])
  const [cmd, setCmd] = useState('infer batch')
  const [requireAC, setRequireAC] = useState(true)

  const refresh = () => getJobs().then(setJobs).catch(() => {})
  useEffect(() => { refresh() }, [])

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    if (!cmd.trim()) return
    await createJob(cmd.trim(), { requireAC }).catch(() => {})
    await refresh()
  }

  return (
    <section className="panel" data-testid="jobs-panel" aria-label="Local-AI job queue">
      <h2 className="section-title">Agents / AI — job queue</h2>
      <p className="panel-note">
        Queue an inference/batch. The daemon runs it only while its constraints hold (AC, temp, time window),
        so a long run won't fight Modern Standby or drain the battery. External agents use the same
        <code> POST /jobs</code> endpoint.
      </p>

      <form className="job-form" onSubmit={onSubmit}>
        <input
          className="job-input" data-testid="job-cmd" aria-label="Job command"
          value={cmd} onChange={(e) => setCmd(e.target.value)} placeholder="command"
        />
        <label className="job-check">
          <input type="checkbox" data-testid="job-requireac" checked={requireAC} onChange={(e) => setRequireAC(e.target.checked)} />
          require AC
        </label>
        <button type="submit" className="btn" data-testid="job-submit">Queue</button>
      </form>

      <ul className="job-list" data-testid="job-list">
        {jobs.length === 0 && <li className="job-empty">No jobs queued.</li>}
        {jobs.map((j) => (
          <li key={j.id} className="job-row" data-testid="job-row">
            <span className="job-id">{j.id}</span>
            <span className="job-cmd-text">{j.cmd}</span>
            <span className={`job-status job-status-${j.status}`} data-testid="job-status">{j.status}</span>
          </li>
        ))}
      </ul>
    </section>
  )
}
