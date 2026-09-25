// GPD Forge — the suite's `test`: Playwright's, with new connections that ride out a loopback stall.
// GPL-3.0-or-later.
//
// Why (F1 audit round 3, 2026-09-25). On the dev handheld, NEW TCP connects to 127.0.0.1 — to any
// listener, ours or not — fail for seconds at a time: Node reports `connect ETIMEDOUT` after ~305 ms,
// Chromium a page that never loads or ERR_CONNECTION_RESET. Established sockets keep working. A probe
// that opened one connect a second to its own listener, run beside four full suites, logged 22
// failures in windows of 2–11 s, and kept logging them after the suite had exited. Over those ~7 min
// the whole machine opened ~1,000 loopback connections (other apps' dev servers, the installed
// daemon, the agent) and the suite ~30 of them, at ~2/s — so the stall is a property of the host,
// not something this suite's connection count triggers, and no socket budget can prevent it. The
// earlier explanation ("~1,000 connects in a couple of minutes trips it") was wrong; see
// docs/ROADMAP.md for both measurements.
//
// So the suite does two things. It avoids needing new connections: the mock and the preview server
// keep idle sockets open (tools/mock-daemon/server.mjs, ui/vite.config.ts) and the browser context is
// reused (playwright.config.ts). And the new connections it still makes are retried here while the
// failure is one that happened BEFORE the request was sent — a refused or timed-out connect — so a
// POST is never sent twice. A daemon that is really down still fails, just RIDE_OUT_MS later.
import { test as base, expect } from '@playwright/test'
import type { APIRequestContext, Page } from '@playwright/test'

export { expect }
export type { APIRequestContext, Page }

/** Longer than the longest stall measured (35 s, docs/ROADMAP.md), shorter than the test timeout. */
export const RIDE_OUT_MS = 40_000
const RETRY_EVERY_MS = 500

/** Node's connect-phase errors: the request never left. Not ECONNRESET on an open socket, which may
 *  have been sent — only `connect E...`. */
export const API_CONNECT_FAILED = /\bconnect E[A-Z]+\b/
/** Chromium's for a navigation that could not open its connection. A navigation is a GET: retrying
 *  one that did reach the server costs nothing. */
export const NAV_CONNECT_FAILED = /net::ERR_(CONNECTION_(TIMED_OUT|REFUSED|RESET|CLOSED|FAILED)|EMPTY_RESPONSE)/

export async function rideOut<T>(attempt: () => Promise<T>, retryable: RegExp, budgetMs = RIDE_OUT_MS): Promise<T> {
  const deadline = Date.now() + budgetMs
  for (;;) {
    try {
      return await attempt()
    } catch (e) {
      const message = e instanceof Error ? e.message : String(e)
      if (!retryable.test(message) || Date.now() + RETRY_EVERY_MS > deadline) throw e
      await new Promise((r) => setTimeout(r, RETRY_EVERY_MS))
    }
  }
}

const API_METHODS = new Set(['get', 'post', 'put', 'patch', 'delete', 'head', 'fetch'])

/** The same APIRequestContext, its request methods retried on a connect failure. */
export function withConnectRetry(request: APIRequestContext): APIRequestContext {
  return new Proxy(request, {
    get(target, prop, receiver) {
      const value = Reflect.get(target, prop, receiver)
      if (typeof value !== 'function') return value
      const bound = value.bind(target) as (...args: unknown[]) => Promise<unknown>
      return API_METHODS.has(String(prop))
        ? (...args: unknown[]) => rideOut(() => bound(...args), API_CONNECT_FAILED)
        : bound
    },
  })
}

const patched = new WeakSet<Page>()

/** goto / reload retried on a connection failure. In place and once: with reuseContext the same page
 *  object serves many tests, and wrapping it again each time would nest the retries. */
function patchNavigation(page: Page): void {
  if (patched.has(page)) return
  patched.add(page)
  const goto = page.goto.bind(page)
  const reload = page.reload.bind(page)
  page.goto = (url, options) => rideOut(() => goto(url, options), NAV_CONNECT_FAILED)
  page.reload = (options) => rideOut(() => reload(options), NAV_CONNECT_FAILED)
}

export const test = base.extend({
  request: async ({ request }, use) => { await use(withConnectRetry(request)) },
  page: async ({ page }, use) => { patchNavigation(page); await use(page) },
})
