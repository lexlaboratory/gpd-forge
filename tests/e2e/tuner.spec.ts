// GPD Forge — Auto-tuner E2E (against the mock's simulated sweep). GPL-3.0-or-later.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('Auto-tuner', () => {
  test('starting a sweep on Power shows a best-result readout', async ({ page }) => {
    await new DashboardPage(page).goto()
    await page.getByTestId('nav-power').click()

    await page.getByTestId('tuner-goal-BestEfficiency').click()
    await page.getByTestId('tuner-start').click()

    await expect(page.getByTestId('tuner-best')).toBeVisible()
  })
})
