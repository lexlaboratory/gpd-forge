// GPD Forge — the suite's connect retry (fixtures.ts) retries what never left, and nothing else.
// GPL-3.0-or-later.
//
// F1 audit round 3 (2026-09-25): new loopback connects on the dev handheld fail for seconds at a time,
// whatever this suite does (fixtures.ts has the measurement). The retry is what keeps one of those
// windows from failing a random test — and it must never turn into sending a POST twice.
import { test, expect, rideOut, API_CONNECT_FAILED, NAV_CONNECT_FAILED } from './fixtures'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'

test.describe('connect retry', () => {
  test('retries a connect failure until it clears, and nothing that may have been sent', async () => {
    let calls = 0
    const flaky = () => {
      calls++
      return calls < 3 ? Promise.reject(new Error('apiRequestContext.post: connect ETIMEDOUT 127.0.0.1:8799')) : Promise.resolve('ok')
    }
    expect(await rideOut(flaky, API_CONNECT_FAILED)).toBe('ok')
    expect(calls).toBe(3)

    // A reset on an open socket may have carried the request: not retried.
    calls = 0
    const reset = () => { calls++; return Promise.reject(new Error('apiRequestContext.post: read ECONNRESET')) }
    await expect(rideOut(reset, API_CONNECT_FAILED)).rejects.toThrow('ECONNRESET')
    expect(calls).toBe(1)

    // The budget ends it: a daemon that is really down still fails.
    calls = 0
    const down = () => { calls++; return Promise.reject(new Error('connect ECONNREFUSED 127.0.0.1:1')) }
    await expect(rideOut(down, API_CONNECT_FAILED, 1_200)).rejects.toThrow('ECONNREFUSED')
    expect(calls).toBeGreaterThan(1)

    expect(NAV_CONNECT_FAILED.test('page.goto: net::ERR_CONNECTION_RESET at http://127.0.0.1:4173/')).toBe(true)
    expect(NAV_CONNECT_FAILED.test('page.goto: net::ERR_ABORTED')).toBe(false)
  })

  test('the request fixture reaches a server that was not listening yet at the first attempt', async ({ request }) => {
    // Reserve a free port, release it, and listen on it only after the first connect has been refused.
    const probe = createServer()
    await new Promise<void>((r) => probe.listen(0, '127.0.0.1', r))
    const port = (probe.address() as AddressInfo).port
    await new Promise<void>((r) => probe.close(() => r()))

    let posts = 0
    let server: Server | null = null
    const late = setTimeout(() => {
      server = createServer((req, res) => { if (req.method === 'POST') posts++; res.end('up') })
      server.listen(port, '127.0.0.1')
    }, 1_500)
    try {
      const res = await request.post(`http://127.0.0.1:${port}/`, { data: {} })
      expect(await res.text()).toBe('up')
      expect(posts).toBe(1)
    } finally {
      clearTimeout(late)
      await new Promise<void>((r) => (server ? server.close(() => r()) : r()))
    }
  })
})
