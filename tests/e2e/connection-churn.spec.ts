// GPD Forge — the suite must not churn loopback TCP connections. GPL-3.0-or-later.
//
// Why this is a test and not a comment: on the dev handheld, Windows stops completing NEW connections
// to 127.0.0.1 — every listener at once, for 10–35 s — once a process has opened roughly a thousand
// of them in a couple of minutes. Established sockets keep working; only the SYN of a new one goes
// unanswered, which Node reports as `connect ETIMEDOUT` after ~310 ms and Chromium as a page that
// never finishes loading. Reproduced 2026-09-25 with a bare `net.connect` loop against a trivial
// listener, no Playwright and no mock involved: bursts of 10 connects every 700 ms stalled at 130 s,
// 5 connects/s for 5 min never did (docs/ROADMAP.md has the numbers).
//
// With a fresh browser context per test, the suite opened ~600 sockets to the mock and ~400 to the
// preview server per run — right on that threshold, which is why roughly one run in three failed a
// different handful of tests. playwright.config.ts now reuses one context per worker, and the whole
// run opens ~20. This spec reads the mock's own socket log and fails if that regresses, because the
// failure it prevents does not look like churn: it looks like random tests timing out.
import { test, expect } from '@playwright/test'
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
