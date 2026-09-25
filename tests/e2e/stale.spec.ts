// GPD Forge — a stalled sampler shows as stale, not as live. GPL-3.0-or-later.
//
// Since 2026-09-24 the daemon answers GET /telemetry from its sampler's cache. Before, a hung hardware
// read hung the request and the panel went Offline; now a sampler that stops publishing serves the
// same temperature, watts and FPS forever with a normal 200. `sampleAgeMs` is the only thing that
// tells a frozen reading from a live one, and until the audit of that day no client read it.
//
// The mock's `_test_stale_ms` seam is asked for PER REQUEST, like unmeasured.spec's blind mode, so no
// state outlives the test that wanted it.
import { test, expect, type Page } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

const staleRoute = async (page: Page, ms: number) => {
  await page.route('**/telemetry', (route) => {
    const u = new URL(route.request().url())
    u.searchParams.set('_test_stale_ms', String(ms))
    return route.continue({ url: u.toString() })
  })
}

test.describe('Stalled telemetry', () => {
  test('the main window says how old the reading is and still counts as connected', async ({ page }) => {
    await staleRoute(page, 12_000)
    await new DashboardPage(page).goto()

    const badge = page.getByTestId('telemetry-stale')
    await expect(badge).toBeVisible()
    await expect(badge).toContainText('12 s')
    await expect(badge).toHaveAttribute('aria-label', /Telemetry stalled/)
    // The daemon IS answering — "Offline" would send the user hunting for the wrong fault.
    await expect(page.getByTestId('conn')).toHaveText('Live')
    // ...and the numbers are dimmed rather than passed off as current.
    await expect(page.locator('.shell')).toHaveAttribute('data-stale', 'true')
  })

  test('a fresh reading shows no stale badge', async ({ page }) => {
    // The guard for the guard: if every reading counted as stale, the test above would still pass.
    await new DashboardPage(page).goto()
    await expect(page.getByTestId('stat-cpu')).toContainText(/\d/)
    await expect(page.getByTestId('telemetry-stale')).toHaveCount(0)
  })

  test('a reading exactly at the bound is not stale', async ({ page }) => {
    // Three 1 Hz sampler ticks, the bound GET /health/check uses. One late tick is noise.
    await staleRoute(page, 3000)
    await new DashboardPage(page).goto()
    await expect(page.getByTestId('stat-cpu')).toContainText(/\d/)
    await expect(page.getByTestId('telemetry-stale')).toHaveCount(0)
  })

  test('the overlay shows it too', async ({ page }) => {
    await staleRoute(page, 9000)
    await page.goto('/overlay.html')

    const badge = page.getByTestId('qam-stale')
    await expect(badge).toBeVisible()
    await expect(badge).toContainText('9 s')
  })
})
