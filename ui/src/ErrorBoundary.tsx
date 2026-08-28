// GPD Forge — keep one broken page from taking the whole app down. GPL-3.0-or-later.
//
// React unmounts the entire tree on an uncaught render error. On 2026-08-28 a single unexpected
// field type on the Alerts page did exactly that: the window went blank, every control disappeared,
// and the app looked dead rather than broken. A tuning tool must degrade to "this panel failed"
// while the sidebar, live telemetry and every other page keep working.
import { Component, type ErrorInfo, type ReactNode } from 'react'

interface Props {
  children: ReactNode
  /** Changing this resets the boundary — pass the current route so navigating away recovers. */
  resetKey?: string
}
interface State { error: Error | null }

export class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null }

  static getDerivedStateFromError(error: Error): State {
    return { error }
  }

  componentDidUpdate(prev: Props) {
    // Navigating to another section clears the failure, so one bad page is never a dead end.
    if (this.state.error && prev.resetKey !== this.props.resetKey) this.setState({ error: null })
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('[forge] panel crashed:', error, info.componentStack)
  }

  render() {
    const { error } = this.state
    if (!error) return this.props.children

    return (
      <section className="card" role="alert" data-testid="panel-error">
        <h2>This panel stopped working</h2>
        <p className="muted">
          The rest of GPD Forge is unaffected — pick another section in the sidebar to carry on.
          Your device is not at risk: the daemon keeps running independently of this window.
        </p>
        <pre className="mono" data-testid="panel-error-detail">{error.message}</pre>
        <button type="button" className="btn" data-testid="panel-error-retry"
                onClick={() => this.setState({ error: null })}>
          Try again
        </button>
      </section>
    )
  }
}
