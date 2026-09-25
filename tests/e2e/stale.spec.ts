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

  // Audit round 3 (2026-09-24): when the sampler's FIRST hardware read hangs, the daemon answers with
  // an all-null placeholder, `sampledAtMs: null`. Its null age read as "not stale", and its power
  // source went out as known, so every client showed a live, current "Battery --%".
  const unsampledRoute = async (page: Page) => {
    await page.route('**/telemetry', (route) => {
      const u = new URL(route.request().url())
      u.searchParams.set('_test_unsampled', '1')
      return route.continue({ url: u.toString() })
    })
  }

  test('a reading the daemon never took is "no reading yet", not live and not on battery', async ({ page }) => {
    await unsampledRoute(page)
    await new DashboardPage(page).goto()

    await expect(page.getByTestId('telemetry-unsampled')).toBeVisible()
    await expect(page.getByTestId('telemetry-unsampled')).toContainText('No reading yet')
    await expect(page.getByTestId('telemetry-stale')).toHaveCount(0)
    await expect(page.getByTestId('conn')).toHaveText('Live')   // the daemon IS answering
    await expect(page.locator('.shell')).toHaveAttribute('data-stale', 'true')

    const pill = page.getByTestId('power-source')
    await expect(pill).toHaveAttribute('data-state', 'unknown')
    await expect(pill).not.toContainText('Battery')
  })

  test('the overlay says so too', async ({ page }) => {
    await unsampledRoute(page)
    await page.goto('/overlay.html')

    await expect(page.getByTestId('qam-unsampled')).toBeVisible()
    await expect(page.getByTestId('qam-stale')).toHaveCount(0)
  })

  // Audit round 1 (2026-09-25): the overlay swallowed a failed poll and kept the last good response,
  // whose `sampleAgeMs` was fresh when served and never aged on the client. A daemon that crashed —
  // taking the guardian, the fan loop and TDP control with it — left a green "live" dot over frozen
  // numbers for as long as the overlay stayed open. The main window has its Offline pill; this is
  // the overlay's.
  test('the overlay says Offline when the daemon stops answering after a good reading', async ({ page }) => {
    let served = 0
    await page.route('**/telemetry', (route) => (served++ === 0 ? route.continue() : route.abort()))
    await page.goto('/overlay.html')

    const offline = page.getByTestId('qam-offline')
    await expect(offline).toBeVisible({ timeout: 8000 })
    await expect(offline).toContainText('Offline')
    await expect(page.locator('.qam-dot')).not.toHaveClass(/\bon\b/)
    await expect(page.locator('.qam-dot')).toHaveAttribute('title', 'offline')
    await expect(page.locator('.qam-live')).toHaveAttribute('data-stale', 'true')
    // Offline, not "stalled": the frozen age the daemon last reported is not what is wrong.
    await expect(page.getByTestId('qam-stale')).toHaveCount(0)
  })

  test('the overlay is not Offline while the daemon answers', async ({ page }) => {
    // The guard for the guard: an overlay that always said Offline would pass the test above.
    await page.goto('/overlay.html')
    await expect(page.locator('.qam-dot')).toHaveClass(/\bon\b/)
    await page.waitForTimeout(4500)   // past STALE_AFTER_MS of polling
    await expect(page.getByTestId('qam-offline')).toHaveCount(0)
    await expect(page.locator('.qam-dot')).toHaveClass(/\bon\b/)
  })

  test('a live reading is never "no reading yet"', async ({ page }) => {
    await new DashboardPage(page).goto()
    await expect(page.getByTestId('stat-cpu')).toContainText(/\d/)
    await expect(page.getByTestId('telemetry-unsampled')).toHaveCount(0)
  })
})
