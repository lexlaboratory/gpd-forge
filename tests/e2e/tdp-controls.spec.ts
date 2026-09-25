// GPD Forge — the TDP controls open on what is in force and say when a write fails. GPL-3.0-or-later.
//
// Since 2026-09-24 a manual TDP is an override the daemon REMEMBERS until the mode changes. The
// controls never read it back: the Dashboard opened on a hardcoded 20 W and the overlay on a preset,
// while 12 W was in force. Errors were swallowed (`.catch(() => {})`), so a refused value left the
// control on a number that was never applied. And the badge defaulted a null `tdpVerified` — nothing
// written, nothing verified — to "verified". These pin all three (audit, 2026-09-24).
import { test, expect, type Page } from './fixtures'
import { DashboardPage } from './pages/DashboardPage'

const API = 'http://127.0.0.1:8799'

/** Fails POST /tdp the way the daemon does for an out-of-band value; GET /tdp passes through. */
const refuseTdpWrites = async (page: Page) => {
  await page.route('**/tdp', (route) =>
    route.request().method() === 'POST'
      ? route.fulfill({ status: 400, json: { error: { code: 'bad_tdp', message: 'stapmW must be 5..40' } } })
      : route.continue())
}

test.describe('TDP controls', () => {
  // Every test leaves the mock in `windows` with no override: picking a mode ends it, as in the daemon.
  test.afterEach(async ({ request }) => {
    await request.post(`${API}/mode`, { data: { name: 'windows' } })
  })

  test('the Dashboard opens on the remembered manual override', async ({ page, request }) => {
    await request.post(`${API}/mode`, { data: { name: 'windows' } })
    await request.post(`${API}/tdp`, { data: { stapmW: 13 } })

    await new DashboardPage(page).goto()

    await expect(page.getByTestId('tdp-value')).toHaveText('13 W')
  })

  test('the overlay opens on it too, not on the preset', async ({ page, request }) => {
    await request.post(`${API}/mode`, { data: { name: 'windows' } })   // preset 15 W
    await request.post(`${API}/tdp`, { data: { stapmW: 11 } })

    await page.goto('/overlay.html')

    await expect(page.getByTestId('qam-tdp')).toContainText('11')
  })

  test('without an override the controls open on the last write', async ({ page, request }) => {
    await request.post(`${API}/mode`, { data: { name: 'windows' } })   // writes the 15 W preset

    await new DashboardPage(page).goto()

    await expect(page.getByTestId('tdp-value')).toHaveText('15 W')
  })

  test('a refused write is reported and the Dashboard control goes back', async ({ page, request }) => {
    await request.post(`${API}/mode`, { data: { name: 'windows' } })
    await refuseTdpWrites(page)
    await new DashboardPage(page).goto()
    await expect(page.getByTestId('tdp-value')).toHaveText('15 W')

    await page.getByTestId('tdp-inc').click()

    await expect(page.getByTestId('toast-error')).toContainText('not applied')
    await expect(page.getByTestId('tdp-value')).toHaveText('15 W')
  })

  test('a refused write is reported and the overlay stepper goes back', async ({ page, request }) => {
    await request.post(`${API}/mode`, { data: { name: 'windows' } })
    await refuseTdpWrites(page)
    await page.goto('/overlay.html')
    await expect(page.getByTestId('qam-tdp')).toContainText('15')

    await page.getByTestId('qam-tdp-inc').click()

    await expect(page.getByTestId('toast-error')).toContainText('not applied')
    await expect(page.getByTestId('qam-tdp')).toContainText('15')
  })

  test('an unreadable readback keeps the requested value in the overlay stepper', async ({ page, request }) => {
    // POST /tdp answers `observed: null` when the closed loop cannot read the limit back. That null
    // used to be written straight into the stepper.
    await request.post(`${API}/mode`, { data: { name: 'windows' } })
    await page.route('**/tdp', (route) => {
      if (route.request().method() !== 'POST') return route.continue()
      const { stapmW } = route.request().postDataJSON() as { stapmW: number }
      return route.fulfill({ json: { requested: stapmW, observed: null, verified: false } })
    })
    await page.goto('/overlay.html')
    await expect(page.getByTestId('qam-tdp')).toContainText('15')

    await page.getByTestId('qam-tdp-inc').click()

    await expect(page.getByTestId('qam-tdp')).toContainText('16')
    await expect(page.getByTestId('qam-verified')).toHaveCount(0)
  })

  // Audit round 2 (2026-09-24): with nothing written yet — the startup apply yielded to MotionAssistant
  // or GPD Tool — GET /tdp answers nulls, and the Dashboard kept a hardcoded 20 W on screen as if it
  // were in force while the overlay showed the mode preset. The mock always has a write, so this path
  // had never run.
  test('with no TDP write yet the Dashboard opens on the active mode preset, like the overlay', async ({ page, request }) => {
    await request.post(`${API}/mode`, { data: { name: 'windows' } })   // preset 15 W
    await page.route('**/tdp', (route) => route.request().method() === 'GET'
      ? route.fulfill({ json: { stapmW: null, owner: null, verified: null, backend: null, observedStapmW: null,
          observedPptW: null, attempts: null, atUtc: null, note: 'No TDP write has happened since the daemon started.',
          manualStapmW: null } })
      : route.continue())

    await new DashboardPage(page).goto()

    await expect(page.getByTestId('tdp-value')).toHaveText('15 W')
    await expect(page.getByTestId('tdp-value')).not.toHaveText('20 W')
  })

  test('with nothing known the Dashboard shows the TDP as unknown, not as a number', async ({ page }) => {
    await page.route('**/tdp', (route) => route.request().method() === 'GET'
      ? route.fulfill({ status: 503, json: { error: { code: 'down', message: 'unavailable' } } })
      : route.continue())
    await page.route('**/profiles', (route) => route.fulfill({ status: 503, json: { error: { code: 'down', message: 'unavailable' } } }))

    await new DashboardPage(page).goto()

    await expect(page.getByTestId('tdp-value')).toHaveText('--')
    await expect(page.getByTestId('tdp-slider')).toBeDisabled()
    await expect(page.getByTestId('tdp-inc')).toBeDisabled()
    await expect(page.getByTestId('tdp-dec')).toBeDisabled()
  })

  test('with nothing verified the Dashboard badge is unknown, not verified', async ({ page }) => {
    await page.route('**/telemetry', async (route) => {
      const res = await route.fetch()
      return route.fulfill({ response: res, json: { ...(await res.json()), tdpVerified: null } })
    })

    await new DashboardPage(page).goto()

    await expect(page.getByTestId('tdp-badge')).toHaveText('unknown')
  })

  // --- Audit round 3 (2026-09-24) -------------------------------------------------------------------

  test('switching mode on the Dashboard re-reads the TDP: the ended override is not left on the slider', async ({ page, request }) => {
    // The seed ran once per mount. Picking a mode ends a manual override in the daemon and applies the
    // mode's preset, and the slider went on showing the override.
    await request.post(`${API}/mode`, { data: { name: 'windows' } })
    await request.post(`${API}/tdp`, { data: { stapmW: 12 } })
    await new DashboardPage(page).goto()
    await expect(page.getByTestId('tdp-value')).toHaveText('12 W')

    await page.getByTestId('mode-gaming').click()

    await expect(page.getByTestId('tdp-value')).toHaveText('25 W')   // gaming's preset
  })

  test('a 36-40 W override in force is shown as it is, on the Dashboard and the overlay alike', async ({ page }) => {
    // The daemon accepts manual 5..40 W, and the Dashboard seeded through a 35 W clamp: a 38 W
    // override read "35 W" there and "38W" in the overlay.
    await page.route('**/tdp', (route) => route.request().method() === 'GET'
      ? route.fulfill({ json: { stapmW: 38, owner: 'manual', verified: false, backend: 'ryzenadj', observedStapmW: 30,
          observedPptW: null, attempts: 3, atUtc: new Date().toISOString(), manualStapmW: 38 } })
      : route.continue())

    await new DashboardPage(page).goto()
    await expect(page.getByTestId('tdp-value')).toHaveText('38 W')
    await expect(page.getByTestId('tdp-slider')).toHaveAttribute('max', '40')

    await page.goto('/overlay.html')
    await expect(page.getByTestId('qam-tdp')).toContainText('38')
  })

  test('a seed that failed at mount says so and recovers on its own', async ({ page }) => {
    // Ran once: a daemon unreachable at mount left the control disabled at '--' for the life of the
    // page, with nothing on screen saying why.
    const down = (route: import('@playwright/test').Route) => route.abort()
    await page.route('**/tdp', down)
    await page.route('**/profiles', down)
    await new DashboardPage(page).goto()
    await expect(page.getByTestId('tdp-value')).toHaveText('--')
    await expect(page.getByTestId('tdp-unavailable')).toContainText('retrying')

    await page.unroute('**/tdp', down)
    await page.unroute('**/profiles', down)

    await expect(page.getByTestId('tdp-slider')).toBeEnabled({ timeout: 8000 })
    await expect(page.getByTestId('tdp-value')).toHaveText('15 W')
    await expect(page.getByTestId('tdp-unavailable')).toHaveCount(0)
  })

  test('the overlay stepper is unknown, not 20 W, until the seed resolves, and then recovers', async ({ page }) => {
    const down = (route: import('@playwright/test').Route) => route.abort()
    await page.route('**/tdp', down)
    await page.route('**/profiles', down)
    await page.goto('/overlay.html')
    await expect(page.getByTestId('qam')).toBeVisible()

    await expect(page.getByTestId('qam-tdp')).toContainText('--')
    await expect(page.getByTestId('qam-tdp')).not.toContainText('20')
    await expect(page.getByTestId('qam-tdp-inc')).toBeDisabled()

    await page.unroute('**/tdp', down)
    await page.unroute('**/profiles', down)
    await expect(page.getByTestId('qam-tdp')).toContainText('15', { timeout: 8000 })
    await expect(page.getByTestId('qam-tdp-inc')).toBeEnabled()
  })

  test('the Dashboard badge follows telemetry after a write, so a reassert that failed shows', async ({ page, request }) => {
    // The badge was pinned to the last POST /tdp result for the life of the page.
    await request.post(`${API}/mode`, { data: { name: 'windows' } })
    await new DashboardPage(page).goto()
    await expect(page.getByTestId('tdp-value')).toHaveText('15 W')
    await page.getByTestId('tdp-inc').click()
    await expect(page.getByTestId('tdp-badge')).toHaveText('verified')

    // Later, the daemon reports the limit no longer verified (the 30 s reassert could not hold it).
    await page.route('**/telemetry', async (route) => {
      const res = await route.fetch()
      return route.fulfill({ response: res, json: { ...(await res.json()), tdpVerified: false, sampledAtMs: Date.now(), sampleAgeMs: 0 } })
    })

    await expect(page.getByTestId('tdp-badge')).toHaveText('unverified')
  })

  test('the overlay verified mark follows telemetry too', async ({ page, request }) => {
    await request.post(`${API}/mode`, { data: { name: 'windows' } })
    await page.goto('/overlay.html')
    await expect(page.getByTestId('qam-tdp')).toContainText('15')
    await page.getByTestId('qam-tdp-inc').click()
    await expect(page.getByTestId('qam-verified')).toBeVisible()

    await page.route('**/telemetry', async (route) => {
      const res = await route.fetch()
      return route.fulfill({ response: res, json: { ...(await res.json()), tdpVerified: false, sampledAtMs: Date.now(), sampleAgeMs: 0 } })
    })

    await expect(page.getByTestId('qam-verified')).toHaveCount(0)
  })

  test('a failed mode switch in the overlay is reported and changes neither the mode nor the stepper', async ({ page, request }) => {
    // The failure was swallowed, then the stepper showed the new mode's preset, recorded it as
    // applied, and toasted "Mode: Gaming".
    await request.post(`${API}/mode`, { data: { name: 'windows' } })
    await page.route('**/mode', (route) => route.request().method() === 'POST'
      ? route.fulfill({ status: 500, json: { error: { code: 'internal', message: 'boom' } } })
      : route.continue())
    await page.goto('/overlay.html')
    await expect(page.getByTestId('qam-tdp')).toContainText('15')

    await page.getByTestId('qam-mode-gaming').click()

    await expect(page.getByTestId('toast-error')).toContainText('Mode was not changed')
    await expect(page.getByTestId('qam-mode-gaming')).toHaveAttribute('aria-pressed', 'false')
    await expect(page.getByTestId('qam-mode-windows')).toHaveAttribute('aria-pressed', 'true')
    await expect(page.getByTestId('qam-tdp')).toContainText('15')
    await expect(page.getByTestId('qam-tdp')).not.toContainText('25')
  })
})
