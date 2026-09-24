// GPD Forge UI — toast notification system. GPL-3.0-or-later.
//
// Usage:
//
//   import { ToastProvider, useToast } from './Toast'
//
//   // once, near the root (wrap <App />):
//   ReactDOM.createRoot(document.getElementById('root')!).render(
//     <ToastProvider>
//       <App />
//     </ToastProvider>,
//   )
//
//   // anywhere under <ToastProvider>:
//   function SomePanel() {
//     const { push } = useToast()
//     const onRevert = () => push({ kind: 'warn', message: 'TDP revertido por firmware' })
//     return <button onClick={onRevert}>Apply</button>
//   }
//
// Renders a fixed bottom-right stack of toasts, each with role="status" and
// aria-live="polite" (announced by screen readers as they appear), auto-dismissing
// after ~4s with a manual close button and a soft enter/exit animation. No external
// dependencies — only React. Styles are scoped via a <style> tag injected by this
// component (not styles.css) and read the app's existing --good/--warn/--danger/
// --accent/--bg-elev/--border theme variables, with literal fallbacks for safety
// if rendered somewhere those variables aren't defined.

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { Icon, type IconName } from './components/Icon'

export type ToastKind = 'info' | 'success' | 'warn' | 'error'

export interface ToastInput {
  kind?: ToastKind
  message: string
  /** Auto-dismiss delay in ms. Defaults to 4000. Pass 0 to disable auto-dismiss (sticky toast). */
  duration?: number
}

export interface ToastContextValue {
  /** Queue a toast; returns its id (pass to `dismiss` to close it early). */
  push: (toast: ToastInput) => string
  /** Dismiss a toast before its timer fires. */
  dismiss: (id: string) => void
}

interface ToastRecord {
  id: string
  kind: ToastKind
  message: string
  leaving: boolean
}

const ToastContext = createContext<ToastContextValue | null>(null)

const DEFAULT_DURATION_MS = 4000
// Exit faster than entry: the system is responding, the user is not waiting to read anything.
const EXIT_ANIMATION_MS = 150

const KIND_META: Record<ToastKind, { icon: IconName; label: string }> = {
  info: { icon: 'info', label: 'Info' },
  success: { icon: 'check', label: 'Success' },
  warn: { icon: 'warn', label: 'Warning' },
  error: { icon: 'close', label: 'Error' },
}

let seq = 0
function nextId(): string {
  seq += 1
  return `toast-${Date.now()}-${seq}`
}

/** Wrap the app (or any subtree) once; use `useToast()` anywhere below it. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastRecord[]>([])
  const timers = useRef(new Map<string, ReturnType<typeof setTimeout>>())

  const clearTimer = useCallback((id: string) => {
    const timer = timers.current.get(id)
    if (timer !== undefined) {
      clearTimeout(timer)
      timers.current.delete(id)
    }
  }, [])

  const remove = useCallback((id: string) => {
    clearTimer(id)
    setToasts((prev) => prev.filter((t) => t.id !== id))
  }, [clearTimer])

  const dismiss = useCallback((id: string) => {
    setToasts((prev) => {
      const target = prev.find((t) => t.id === id)
      if (!target || target.leaving) return prev
      return prev.map((t) => (t.id === id ? { ...t, leaving: true } : t))
    })
    clearTimer(id)
    const timer = setTimeout(() => remove(id), EXIT_ANIMATION_MS)
    timers.current.set(id, timer)
  }, [clearTimer, remove])

  const push = useCallback((toast: ToastInput): string => {
    const id = nextId()
    const kind = toast.kind ?? 'info'
    const duration = toast.duration ?? DEFAULT_DURATION_MS
    setToasts((prev) => [...prev, { id, kind, message: toast.message, leaving: false }])
    if (duration > 0) {
      const timer = setTimeout(() => dismiss(id), duration)
      timers.current.set(id, timer)
    }
    return id
  }, [dismiss])

  // Flush any pending timers if the provider unmounts.
  useEffect(() => {
    const map = timers.current
    return () => {
      map.forEach((timer) => clearTimeout(timer))
      map.clear()
    }
  }, [])

  const value = useMemo<ToastContextValue>(() => ({ push, dismiss }), [push, dismiss])

  return (
    <ToastContext.Provider value={value}>
      {children}
      <ToastViewport toasts={toasts} onClose={dismiss} />
    </ToastContext.Provider>
  )
}

/** Read `push`/`dismiss` from the nearest `<ToastProvider>`. Throws if none is mounted. */
export function useToast(): ToastContextValue {
  const ctx = useContext(ToastContext)
  if (!ctx) throw new Error('useToast() must be called within a <ToastProvider>')
  return ctx
}

function ToastViewport({ toasts, onClose }: { toasts: ToastRecord[]; onClose: (id: string) => void }) {
  return (
    <>
      <style>{TOAST_CSS}</style>
      <div className="gpd-toast-viewport" data-testid="toast-viewport">
        {toasts.map((t) => (
          <div
            key={t.id}
            role="status"
            aria-live="polite"
            aria-atomic="true"
            data-testid={`toast-${t.kind}`}
            className={`gpd-toast gpd-toast-${t.kind} ${t.leaving ? 'gpd-toast-leaving' : 'gpd-toast-entering'}`}
          >
            <span className="gpd-toast-icon"><Icon name={KIND_META[t.kind].icon} size={18} /></span>
            <p className="gpd-toast-msg">
              <span className="gpd-toast-sr-only">{KIND_META[t.kind].label}: </span>
              {t.message}
            </p>
            <button
              type="button"
              className="gpd-toast-close"
              aria-label="Dismiss notification"
              onClick={() => onClose(t.id)}
            >
              <Icon name="close" size={16} />
            </button>
          </div>
        ))}
      </div>
    </>
  )
}

// Inline rather than in styles.css because the overlay (a separate entry that does not load
// styles.css) renders toasts too. Every value is a token from tokens.css, which both entries load.
const TOAST_CSS = `
.gpd-toast-viewport {
  position: fixed; right: var(--space-4); bottom: var(--space-4); z-index: var(--z-toast);
  display: flex; flex-direction: column; align-items: flex-end; gap: var(--space-2);
  width: min(24rem, calc(100vw - 2 * var(--space-4)));
  pointer-events: none;
}
.gpd-toast {
  pointer-events: auto; width: 100%;
  display: flex; align-items: flex-start; gap: var(--space-3);
  background: var(--bg-elev-2);
  border: var(--hairline) solid var(--border-bright);
  border-radius: var(--radius-lg);
  box-shadow: var(--shadow-pop);
  padding: var(--space-3) var(--space-3) var(--space-3) var(--space-4);
  color: var(--text);
  font: var(--text-sm) / var(--leading-normal) var(--font-body);
}
.gpd-toast-entering { animation: gpd-toast-in var(--dur-base) var(--ease-out); }
.gpd-toast-leaving { animation: gpd-toast-out ${EXIT_ANIMATION_MS}ms var(--ease-out) forwards; }
.gpd-toast-icon { flex: none; display: inline-flex; margin-top: 0.1rem; }
.gpd-toast-success .gpd-toast-icon { color: var(--good); }
.gpd-toast-warn .gpd-toast-icon { color: var(--warn); }
.gpd-toast-error .gpd-toast-icon { color: var(--danger); }
.gpd-toast-info .gpd-toast-icon { color: var(--accent); }
.gpd-toast-msg { flex: 1 1 auto; min-width: 0; margin: 0; overflow-wrap: anywhere; }
.gpd-toast-sr-only {
  position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px;
  overflow: hidden; clip: rect(0, 0, 0, 0); white-space: nowrap; border: 0;
}
.gpd-toast-close {
  flex: none; display: inline-flex; align-items: center; justify-content: center;
  width: 2rem; height: 2rem; margin: -0.25rem -0.25rem 0 0;
  cursor: pointer; border: none; background: transparent; color: var(--text-dim);
  border-radius: var(--radius-sm);
  -webkit-tap-highlight-color: transparent;
}
.gpd-toast-close:focus-visible { outline: var(--focus-width) solid var(--accent); outline-offset: 2px; }
@media (hover: hover) and (pointer: fine) {
  .gpd-toast-close:hover { color: var(--text); background: var(--bg-hover); }
}
@keyframes gpd-toast-in {
  from { opacity: 0; transform: translateY(0.5rem) scale(0.97); }
  to { opacity: 1; transform: translateY(0) scale(1); }
}
@keyframes gpd-toast-out {
  from { opacity: 1; transform: translateY(0); }
  to { opacity: 0; transform: translateY(0.25rem); }
}
@media (prefers-reduced-motion: reduce) {
  .gpd-toast-entering, .gpd-toast-leaving { animation: none; }
}
`
