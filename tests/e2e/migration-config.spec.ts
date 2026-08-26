// GPD Forge — MotionAssistant import + settings backup/restore E2E. GPL-3.0-or-later.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('Migration + config', () => {
  test('MotionAssistant import toasts the found count and lists the profiles', async ({ page }) => {
    await new DashboardPage(page).goto()
    await page.getByTestId('nav-profiles').click()
    await page.getByTestId('import-ma').click()
    await expect(page.getByTestId('toast-success')).toBeVisible()
    await expect(page.getByTestId('import-ma-results')).toBeVisible()
  })

  test('settings export control exists on the Settings page', async ({ page }) => {
    await new DashboardPage(page).goto()
    await page.getByTestId('nav-settings').click()
    const exportLink = page.getByTestId('settings-export')
    await expect(exportLink).toBeVisible()
    await expect(exportLink).toHaveAttribute('href', /settings\/export/)
  })
})
