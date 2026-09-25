// GPD Forge — the suite must not churn loopback TCP connections. GPL-3.0-or-later.
//
// Why this is a test and not a comment: on the dev handheld NEW connections to 127.0.0.1 fail for
// seconds at a time — every listener at once — while established sockets keep working. Node reports
// `connect ETIMEDOUT` after ~305 ms, Chromium a page that never finishes loading. Every new socket a
// test needs is therefore a chance to fail at random, and the failure does not look like churn: it
// looks like random tests timing out.
//
// First diagnosed (2026-09-25) as the suite TRIGGERING the stall by opening ~1,000 connections a run.
// F1 audit round 3 (same day) disproved that: a connect probe beside four full runs logged stalls at
// ~30 suite connections a run, and after the suite had exited (tests/e2e/fixtures.ts has the numbers).
// The stall is the host's. What still holds is the exposure: fewer new sockets, fewer chances. So the
// context is reused (playwright.config.ts), the mock and the preview server keep idle sockets for the
// run, and this spec reads the mock's own socket log and fails if the count regresses.
import { test, expect } from './fixtures'
import { existsSync, readFileSync } from 'node:fs'
import { MOCK_LOG } from './mock-log'

// Per-test contexts cost ~3.3 mock sockets per test, so by the time this file runs (after the ~20
// tests in the specs that sort before it) they would be well past this; a reused context stays at
// a handful for the whole run. Generous on purpose: it guards the order of magnitude, not a count.
const MAX_SOCKETS = 40
// Below this many requests the ratio says nothing (e.g. this spec run on its own).
const MIN_REQUESTS = 100

test('the suite reuses its connections to the mock daemon instead of opening one per test', async () => {
  test.skip(!existsSync(MOCK_LOG), `no mock log at ${MOCK_LOG} — the mock was not started by this config`)
  const log = readFileSync(MOCK_LOG, 'utf8')
  const sockets = (log.match(/ sock\+ /g) ?? []).length
  const requests = (log.match(/ req #/g) ?? []).length
  test.skip(requests < MIN_REQUESTS, `only ${requests} requests so far — run the full suite to measure churn`)

  expect(
    sockets,
    `${sockets} TCP connections for ${requests} requests. Something is opening a connection per test ` +
      '(reuseContext off, video recording on — which disables reuse — or the mock closing idle ' +
      'sockets). On Windows that churn makes new loopback connects stall and the suite flake.',
  ).toBeLessThanOrEqual(MAX_SOCKETS)
})
