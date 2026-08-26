// GPD Forge — multi-page navigation E2E. GPL-3.0-or-later.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('Multi-page UI', () => {
  test.beforeEach(async ({ page }) => { await new DashboardPage(page).goto() })

  test('navigates across the sections', async ({ page }) => {
    await expect(page.getByTestId('page-dashboard')).toBeVisible()
    for (const id of ['power', 'fan', 'controller', 'display', 'profiles', 'monitor', 'system', 'settings']) {
      await page.getByTestId(`nav-${id}`).click()
      await expect(page.getByTestId(`page-${id}`)).toBeVisible()
    }
  })

  test('editing a power preset saves it through the daemon', async ({ page }) => {
    await page.getByTestId('nav-power').click()
    await page.getByTestId('preset-gaming').click()
    await page.getByTestId('p-stapm').fill('28')
    await page.getByTestId('preset-apply').click()
    await expect(page.getByTestId('preset-saved')).toBeVisible()
  })

  test('display page shows a working brightness slider', async ({ page }) => {
    await page.getByTestId('nav-display').click()
    const b = page.getByTestId('brightness')
    await expect(b).toBeVisible()
    await b.fill('55')
    await expect(b).toHaveValue('55')
  })
})
