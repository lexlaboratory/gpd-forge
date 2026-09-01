// GPD Forge — what the panel shows for a sensor it cannot read. GPL-3.0-or-later.
//
// Until 2026-09-01 an unreadable sensor arrived as 0, so with the hardware gate closed the app
// displayed a CPU at 0 °C, a fan at 0 rpm and a package drawing 0 W. Every one of those is a
// plausible, confident, wrong number, and nothing on screen distinguished it from a cold, idle
// machine. The daemon now sends null, and these tests are the only place that checks the UI does
// the right thing with it.
//
// They need the mock's `_test-blind` seam: the mock otherwise always produces numbers, so no other
// spec in this suite ever renders the placeholder. A guard nobody can exercise is not a guard.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

const API = process.env.VITE_FORGE_API ?? 'http://127.0.0.1:8799'

test.describe('Unmeasured sensors', () => {
  test.afterEach(async ({ request }) => {
    // Restore before the next spec: this suite is serial and shares one mock daemon, so leaving it
    // blind would make every later test look at a machine with no sensors.
    await request.post(`${API}/telemetry/_test-blind`, { data: { blind: false } })
  })

  test('a sensor with no reading shows a placeholder, never a zero', async ({ page, request }) => {
    await request.post(`${API}/telemetry/_test-blind`, { data: { blind: true } })

    await new DashboardPage(page).goto()

    for (const tile of ['stat-cpu', 'stat-pkg', 'stat-fan', 'stat-fps', 'stat-batt']) {
      const el = page.getByTestId(tile)
      await expect(el).toContainText('--')
      // The assertion that actually matters. A tile reading "0 °C" passes any check written against
      // the unit alone — which is how "-- °C" survived for hours on 2026-08-28.
      await expect(el).not.toContainText(/(^|\s)0(\s|$)/)
    }
  })

  test('real readings still render as numbers', async ({ page, request }) => {
    // The regression guard for the guard: if the placeholder path swallowed the normal one, the test
    // above would still pass and the app would show '--' forever.
    await request.post(`${API}/telemetry/_test-blind`, { data: { blind: false } })

    await new DashboardPage(page).goto()

    await expect(page.getByTestId('stat-cpu')).toContainText(/\d/)
    await expect(page.getByTestId('stat-cpu')).not.toContainText('--')
  })
})
