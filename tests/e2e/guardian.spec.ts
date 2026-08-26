// GPD Forge — thermal/battery guardian E2E. GPL-3.0-or-later.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('Guardian', () => {
  test('settings shows the guardian and its toggles work', async ({ page }) => {
    await new DashboardPage(page).goto()
    await page.getByTestId('nav-settings').click()
    await expect(page.getByTestId('guardian-status')).toBeVisible()
    await expect(page.getByTestId('guardian-autothrottle')).toBeVisible()
    const en = page.getByTestId('guardian-enabled')
    await expect(en).toHaveAttribute('aria-pressed', 'true')
    await en.click()
    await expect(en).toHaveAttribute('aria-pressed', 'false')
  })
})
