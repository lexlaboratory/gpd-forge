// GPD Forge UI — daemon offline banner.
// Shown at the top of the shell when GET /telemetry has failed repeatedly. Honest about the reason
// (we report the last fetch error verbatim when the daemon didn't come up on its own) so the user
// knows whether to re-run the installer or whether the service is just slow to start.
import { useState } from 'react'

export interface DaemonOfflineBannerProps {
  reason?: string | null
  onRetry: () => void
  busy?: boolean
}

const INSTALL_HINT =
  'Para recuperarlo: abre PowerShell como Administrador y ejecuta ' +
  '`cd C:\\Users\\Alex\\gpd-forge ; .\\scripts\\install-gpd-forge.ps1`. ' +
  'Esto (re)instala el servicio GPDForge y reinicia la API local.'

export function DaemonOfflineBanner({ reason, onRetry, busy }: DaemonOfflineBannerProps) {
  const [dismissed, setDismissed] = useState(false)
  if (dismissed) return null
  return (
    <div className="daemon-offline" role="alert" data-testid="daemon-offline">
      <div className="daemon-offline-head">
        <strong>GPD Forge service no responde</strong>
        <button type="button" className="link" onClick={() => setDismissed(true)} aria-label="Descartar">×</button>
      </div>
      <p className="daemon-offline-body">
        La API local (<code>http://127.0.0.1:8787</code>) no contesta. Los datos están en caché hasta
        que vuelva. {INSTALL_HINT}
      </p>
      {reason && <p className="daemon-offline-reason" data-testid="daemon-offline-reason">Último error: <code>{reason}</code></p>}
      <div className="daemon-offline-actions">
        <button type="button" className="btn" onClick={onRetry} disabled={busy} data-testid="daemon-offline-retry">
          {busy ? 'Reintentando…' : 'Reintentar'}
        </button>
        <a className="link" href="https://github.com/lexlaboratory/gpd-forge#installation" target="_blank" rel="noreferrer">
          Ver instrucciones →
        </a>
      </div>
    </div>
  )
}
