// GPD Forge UI — Standby Doctor panel. GPL-3.0-or-later.
//
// This panel used to print hardcoded literals the daemon invented. It now renders only what
// GET /standby actually measured, and says "not measured" — never a zero, never a bare dash —
// wherever the daemon returned null.
import { useEffect, useState } from 'react'
import type { Standby, StandbyRestoreOutcome } from './types'
import { getStandby, restoreStandby } from './api'
import { Frame, Readout, Button, Badge, Unavailable } from './components'

const NOT_MEASURED = 'not measured'

export function StandbyPanel() {
  const [info, setInfo] = useState<Standby | null>(null)
  const [offline, setOffline] = useState(false)
  const [restore, setRestore] = useState<StandbyRestoreOutcome | null>(null)

  useEffect(() => {
    getStandby().then(setInfo).catch(() => setOffline(true))
  }, [])

  const onRestore = async () => {
    const r = await restoreStandby().catch(() => null)
    if (r) setRestore(r)
  }

  const drain = info?.lastDrainPctPerHour ?? null
  const shown = restore ?? info?.lastRestore ?? null
  const steps = shown?.steps ?? []
  const blockersKnown = info?.diagnosticsAvailable === true

  return (
    <Frame
      as="panel"
      testid="standby-panel"
      title="Standby Doctor"
      hint={<Badge tone={drain === null ? 'muted' : 'ok'}>{drain === null ? NOT_MEASURED : 'measured'}</Badge>}
    >
      <p className="muted">
        Modern Standby on the Win 4 loses TDP and fan state on resume and can drain overnight. The
        drain figure below is only shown once the daemon has seen a real suspend on battery.
      </p>

      {offline && <Unavailable testid="standby-offline" reason="The daemon did not answer GET /standby." />}
      {info && !info.diagnosticsAvailable && (
        <Unavailable
          testid="standby-diagnostics-unavailable"
          reason={info.diagnosticsError ?? 'powercfg diagnostics are unavailable, so the wake reason and sleep blockers are unknown.'}
        />
      )}

      <div className="standby-grid">
        <Readout
          testid="standby-drain"
          label="Overnight drain"
          value={drain === null ? NOT_MEASURED : drain.toFixed(1)}
          unit={drain === null ? undefined : '%/h'}
          footer={
            <span className="muted">
              {drain === null
                ? 'No suspend on battery observed yet.'
                : `${info?.lastDrainAt?.slice(0, 16).replace('T', ' ')} — ${info?.lastDrainSleptHours} h asleep.`}
            </span>
          }
        />
        <Readout
          testid="standby-wake"
          label="Top wake reason"
          value={info?.topWakeReason ?? (blockersKnown ? 'none recorded' : NOT_MEASURED)}
        />
        <Readout
          testid="standby-blockers"
          label="Sleep blockers"
          value={!blockersKnown ? NOT_MEASURED : info!.blockers.length === 0 ? 'none' : String(info!.blockers.length)}
          footer={
            blockersKnown && info!.blockers.length > 0
              ? <ul className="muted">{info!.blockers.map((b) => <li key={b}>{b}</li>)}</ul>
              : undefined
          }
        />
      </div>

      <div className="standby-actions">
        <Button variant="accent" testid="standby-restore" onClick={onRestore}>Run resume restore</Button>
      </div>

      {shown && (
        // Reuses the job list's layout: every step gets its own line with the reason it did or did
        // not happen, instead of the old row of bare chips that implied success.
        <ul className="job-list" data-testid="standby-restored" aria-label="Resume restore steps">
          {steps.length === 0
            ? <li className="muted">The daemon reported no restore steps.</li>
            : steps.map((s) => (
                <li key={s.name} className="job-row">
                  <Badge tone={s.restored ? 'ok' : 'warn'}>{s.name}</Badge>
                  <span className="muted">{s.detail}</span>
                </li>
              ))}
        </ul>
      )}
      {shown && steps.length > 0 && !shown.anyRestored && (
        <Unavailable
          testid="standby-restore-nothing"
          reason="Nothing was restored — each step above says why."
        />
      )}

      <SleepStudySection info={info} />
    </Frame>
  )
}

const KIND_LABEL: Record<string, string> = {
  'failed-resume': 'did not wake up',
  bugcheck: 'bugcheck',
  'worst-drain': 'worst drain',
}

/**
 * The sleep study is the only thing here that can explain a machine that slept and never came back —
 * the System event log routinely records no standby transition at all for exactly those nights.
 *
 * It deliberately renders four different things, because "we never looked", "we were not allowed to
 * look", "we looked and all is well" and "we looked and here is what killed it" are four different
 * answers and only the last one is a finding.
 */
function SleepStudySection({ info }: { info: Standby | null }) {
  if (!info) return null

  // `?? null` rather than a strict !== null test below: a daemon that predates these fields omits
  // them entirely, and `undefined !== null` is true — which would render an error with no reason in
  // it, inventing a failure out of an older build. An absent field means "not sampled yet".
  const error = info.sleepStudyError ?? null
  const study = info.sleepStudy ?? null
  const findings = study?.findings ?? []

  return (
    <div className="standby-sleepstudy">
      <h4>Sleep study</h4>

      {error !== null ? (
        <Unavailable testid="standby-sleepstudy-unavailable" reason={error} />
      ) : study === null ? (
        <p className="muted" data-testid="standby-sleepstudy-pending">
          Not sampled yet — the daemon generates the report shortly after it starts, and then twice a
          day.
        </p>
      ) : findings.length === 0 ? (
        <p className="muted" data-testid="standby-sleepstudy-clean">
          {study.sessions} session(s) examined, nothing to report.
        </p>
      ) : (
        <ul className="job-list" data-testid="standby-sleepstudy" aria-label="Sleep study findings">
          {findings.map((f) => (
            <li key={`${f.kind}-${f.at}`} className="job-row">
              <Badge tone={f.kind === 'worst-drain' ? 'muted' : 'warn'}>
                {KIND_LABEL[f.kind] ?? f.kind}
              </Badge>
              <span className="muted">
                {f.at.slice(0, 16).replace('T', ' ')} — {f.detail}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
