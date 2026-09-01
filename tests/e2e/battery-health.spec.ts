// GPD Forge — the battery health card. GPL-3.0-or-later.
//
// The card's whole job is to be honest about a board that will not answer two of the four questions
// anyone asks about a battery. So these tests are mostly about what it says when it does NOT know:
// a blank row looks like a bug, and an invented number is worse than either.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('Battery health', () => {
  test.beforeEach(async ({ page }) => {
    // Navigated the way the rest of the suite does, rather than by typing a hash: the app writes the
    // hash itself and a direct '/#/system' races the boot that reads it.
    await new DashboardPage(page).goto()
    await page.getByTestId('nav-system').click()
    await expect(page.getByTestId('page-system')).toBeVisible()
  })

  test('shows the pack capacity as a percentage of design, with the watt-hours behind it', async ({ page }) => {
    const card = page.getByTestId('battery-health')
    await expect(card).toBeVisible()

    // A digit, and specifically NOT the placeholder. Asserting on the unit alone is how a tile that
    // reads "-- %" passed for hours on 2026-08-28.
    const pct = page.getByTestId('battery-health-pct')
    await expect(pct).toHaveText(/\d+\.\d\s*% of design/)
    await expect(pct).not.toHaveText(/--/)

    // The raw numbers are shown too: a percentage with nothing behind it cannot be checked against
    // powercfg, which is the one independent source a user has.
    await expect(page.getByTestId('battery-health-capacity')).toHaveText(/\d+\.\d of \d+\.\d Wh/)
  })

  test('says cycle count is not reported, rather than showing zero', async ({ page }) => {
    // The reason this feature is careful. Both powercfg and the WMI class return 0 for a pack that
    // has demonstrably lost capacity, so 0 means "the EC does not keep this number". Printing it
    // would put "0 cycles" beside "91 % health" and leave the user to resolve the contradiction.
    const note = page.getByTestId('battery-health-cycles-unavailable')
    await expect(note).toBeVisible()
    await expect(note).toContainText('not reported')

    // And the honest row must not coexist with a fabricated one.
    await expect(page.getByTestId('battery-health-cycles')).toHaveCount(0)
  })

  test('renders the degradation trend once there is history from two days', async ({ page }) => {
    // The mock carries two samples eight months apart, which is what the real store will hold after
    // the daily sampler has run twice.
    const trend = page.getByTestId('battery-health-trend')
    await expect(trend).toBeVisible()
    await expect(trend).toHaveText(/[−+]\d+\.\d pts over \d+ samples/)

    // With a trend present, the "waiting for history" explanation must be gone — showing both would
    // say the trend is simultaneously available and not.
    await expect(page.getByTestId('battery-health-trend-pending')).toHaveCount(0)
  })
})
