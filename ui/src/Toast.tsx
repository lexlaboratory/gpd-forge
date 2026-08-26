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
const EXIT_ANIMATION_MS = 220

const KIND_META: Record<ToastKind, { icon: string; label: string }> = {
  info: { icon: 'ℹ', label: 'Info' },
  success: { icon: '✓', label: 'Success' },
  warn: { icon: '⚠', label: 'Warning' },
  error: { icon: '✕', label: 'Error' },
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
            <span className="gpd-toast-icon" aria-hidden="true">{KIND_META[t.kind].icon}</span>
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
              <span aria-hidden="true">×</span>
            </button>
          </div>
        ))}
      </div>
    </>
  )
}

const TOAST_CSS = `
.gpd-toast-viewport {
  position: fixed;
  right: 20px;
  bottom: 20px;
  z-index: 9999;
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 10px;
  max-width: min(360px, calc(100vw - 40px));
  pointer-events: none;
}
.gpd-toast {
  pointer-events: auto;
  width: 100%;
  display: flex;
  align-items: flex-start;
  gap: 10px;
  background: var(--bg-elev, #131824);
  border: 1px solid var(--border, #232b3d);
  border-left: 3px solid var(--accent, #4cc2ff);
  border-radius: var(--radius, 14px);
  box-shadow: var(--shadow, 0 6px 24px rgba(0, 0, 0, 0.35));
  padding: 12px 12px 12px 14px;
  color: var(--text, #e6e9f0);
  font: 13.5px/1.45 "Segoe UI", system-ui, -apple-system, sans-serif;
}
.gpd-toast-entering { animation: gpd-toast-in 200ms ease-out; }
.gpd-toast-leaving { animation: gpd-toast-out 220ms ease-in forwards; }
.gpd-toast-success { border-left-color: var(--good, #37d67a); }
.gpd-toast-warn { border-left-color: var(--warn, #ffb020); }
.gpd-toast-error { border-left-color: var(--danger, #ff5c6c); }
.gpd-toast-info { border-left-color: var(--accent, #4cc2ff); }
.gpd-toast-icon { flex: 0 0 auto; font-size: 14px; line-height: 1.4; }
.gpd-toast-success .gpd-toast-icon { color: var(--good, #37d67a); }
.gpd-toast-warn .gpd-toast-icon { color: var(--warn, #ffb020); }
.gpd-toast-error .gpd-toast-icon { color: var(--danger, #ff5c6c); }
.gpd-toast-info .gpd-toast-icon { color: var(--accent, #4cc2ff); }
.gpd-toast-msg { flex: 1 1 auto; margin: 0; word-break: break-word; }
.gpd-toast-sr-only {
  position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px;
  overflow: hidden; clip: rect(0, 0, 0, 0); white-space: nowrap; border: 0;
}
.gpd-toast-close {
  flex: 0 0 auto;
  cursor: pointer;
  border: none;
  background: transparent;
  color: var(--text-dim, #8a93a6);
  font-size: 16px;
  line-height: 1;
  padding: 2px;
  margin: -2px -2px 0 0;
  border-radius: 4px;
}
.gpd-toast-close:hover { color: var(--text, #e6e9f0); }
.gpd-toast-close:focus-visible { outline: 2px solid var(--accent, #4cc2ff); outline-offset: 2px; }
@keyframes gpd-toast-in {
  from { opacity: 0; transform: translateY(8px) scale(.98); }
  to { opacity: 1; transform: translateY(0) scale(1); }
}
@keyframes gpd-toast-out {
  from { opacity: 1; transform: translateY(0) scale(1); }
  to { opacity: 0; transform: translateY(6px) scale(.98); }
}
@media (prefers-reduced-motion: reduce) {
  .gpd-toast-entering, .gpd-toast-leaving { animation: none; }
}
`
