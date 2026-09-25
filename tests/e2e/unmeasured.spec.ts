// GPD Forge — what the panel shows for a sensor it cannot read. GPL-3.0-or-later.
//
// Until 2026-09-01 an unreadable sensor arrived as 0, so with the hardware gate closed the app
// displayed a CPU at 0 °C, a fan at 0 rpm and a package drawing 0 W. Every one of those is a
// plausible, confident, wrong number, and nothing on screen distinguished it from a cold, idle
// machine. The daemon now sends null, and these tests are the only place that checks the UI does
// the right thing with it.
//
// They need the mock's blind seam: the mock otherwise always produces numbers, so no other
// spec in this suite ever renders the placeholder. A guard nobody can exercise is not a guard.
import { test, expect } from './fixtures'
import { DashboardPage } from './pages/DashboardPage'

// The blind mode is asked for PER REQUEST, via a query parameter the UI appends. It was a server
// flag for one day and leaked into the next spec twice over — this suite is serial and shares one
// mock daemon, and this file sorts immediately before visual.spec.ts. There is now no state to
// restore, because there is no state.
test.describe('Unmeasured sensors', () => {
  const blindRoute = async (page: import('@playwright/test').Page) => {
    await page.route('**/telemetry', (route) => {
      const u = new URL(route.request().url())
      u.searchParams.set('_test_blind', '1')
      return route.continue({ url: u.toString() })
    })
  }

  test('a sensor with no reading shows a placeholder, never a zero', async ({ page }) => {
    await blindRoute(page)

    await new DashboardPage(page).goto()

    for (const tile of ['stat-cpu', 'stat-pkg', 'stat-fan', 'stat-fps', 'stat-batt']) {
      const el = page.getByTestId(tile)
      await expect(el).toContainText('--')
      // The assertion that actually matters. A tile reading "0 °C" passes any check written against
      // the unit alone — which is how "-- °C" survived for hours on 2026-08-28.
      await expect(el).not.toContainText(/(^|\s)0(\s|$)/)
    }
  })

  // Audit round 2 (2026-09-24): a failed battery query arrives as `acConnected: false` with no
  // battery percentage. The pill read that as a confident "Battery --%" on a plugged-in machine, and
  // nothing on screen said the AC/battery switch and the per-app rules had paused.
  const unknownPowerRoute = async (page: import('@playwright/test').Page) => {
    await page.route('**/telemetry', async (route) => {
      const res = await route.fetch()
      return route.fulfill({ response: res, json: { ...(await res.json()), acConnected: false, acKnown: false, batteryPct: null } })
    })
  }

  test('a power source the daemon could not read is unknown, not battery', async ({ page }) => {
    await unknownPowerRoute(page)

    await new DashboardPage(page).goto()

    const pill = page.getByTestId('power-source')
    await expect(pill).toHaveText('Power --')
    await expect(pill).toHaveAttribute('data-state', 'unknown')
    await expect(pill).not.toContainText('Battery')
  })

  test('the per-app rules say they are paused while the power source is unknown', async ({ page }) => {
    await unknownPowerRoute(page)

    await new DashboardPage(page).goto()
    await page.getByTestId('nav-profiles').click()

    await expect(page.getByTestId('rules-paused')).toContainText('paused')
  })

  test('a known power source is still shown as AC or battery', async ({ page }) => {
    await new DashboardPage(page).goto()

    await expect(page.getByTestId('power-source')).toHaveText(/^(AC|Battery \d+%)$/)
    await expect(page.getByTestId('rules-paused')).toHaveCount(0)
  })

  test('real readings still render as numbers', async ({ page }) => {
    // The regression guard for the guard: if the placeholder path swallowed the normal one, the test
    // above would still pass and the app would show '--' forever. No route override here, so this
    // sees exactly what every other spec sees.
    await new DashboardPage(page).goto()

    await expect(page.getByTestId('stat-cpu')).toContainText(/\d/)
    await expect(page.getByTestId('stat-cpu')).not.toContainText('--')
  })
})
