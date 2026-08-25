// GPD Forge — E2E against the local API (mock daemon). GPL-3.0-or-later.
// Verifies the UI ↔ daemon contract from docs/api.md: live connection, mode round-trip,
// and the honest TDP closed-loop (verified vs. reverted).
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('Local API integration', () => {
  let dash: DashboardPage

  test.beforeEach(async ({ page }) => {
    dash = new DashboardPage(page)
    await dash.goto()
  })

  test('shows a live connection to the daemon', async ({ page }) => {
    await expect(page.getByTestId('conn')).toHaveText('Live')
  })

  test('mode selection round-trips through the daemon (persists across reload)', async ({ page }) => {
    await dash.pickMode('ai')
    await expect(dash.mode('ai')).toHaveAttribute('aria-selected', 'true')

    // Reload: the UI reads GET /mode on load, so the daemon must have persisted it.
    await page.reload()
    await expect(dash.device).toBeVisible()
    await expect(dash.mode('ai')).toHaveAttribute('aria-selected', 'true')
    await expect(dash.activeMode).toContainText('Agents / AI')

    // reset for the next test
    await dash.pickMode('windows')
  })

  test('TDP closed loop: high request is reported unverified', async ({ page }) => {
    await dash.tdpSlider.fill('34') // above the firmware cap → daemon reverts
    await expect(page.getByTestId('tdp-badge')).toHaveText('unverified')
  })

  test('TDP closed loop: safe request is reported verified', async ({ page }) => {
    await dash.tdpSlider.fill('18')
    await expect(page.getByTestId('tdp-badge')).toHaveText('verified')
  })
})
