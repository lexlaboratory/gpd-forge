// GPD Forge — multi-page navigation E2E. GPL-3.0-or-later.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('Multi-page UI', () => {
  test.beforeEach(async ({ page }) => { await new DashboardPage(page).goto() })

  test('navigates across the sections', async ({ page }) => {
    await expect(page.getByTestId('page-dashboard')).toBeVisible()
    for (const id of ['power', 'fan', 'hardware', 'display', 'profiles', 'monitor', 'system', 'settings']) {
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

  test('the AI preset offers no boost sliders, and says why', async ({ page }) => {
    // AI mode runs at a flat sustained ceiling; the daemon collapses fast/slow onto STAPM. Leaving
    // the sliders on screen would be two controls the user can move that change nothing.
    await page.getByTestId('nav-power').click()

    await page.getByTestId('preset-gaming').click()
    await expect(page.getByTestId('p-fast')).toBeVisible()

    await page.getByTestId('preset-ai').click()
    await expect(page.getByTestId('p-fast')).toBeHidden()
    await expect(page.getByTestId('p-slow')).toBeHidden()
    await expect(page.getByTestId('p-stapm')).toBeVisible()
    await expect(page.getByTestId('preset-sustained-note')).toContainText('flat ceiling')
  })

  test('saving the AI preset shows the flattened values the daemon kept', async ({ page }) => {
    await page.getByTestId('nav-power').click()
    await page.getByTestId('preset-ai').click()
    await page.getByTestId('p-stapm').fill('18')
    await page.getByTestId('preset-apply').click()

    await expect(page.getByTestId('preset-saved')).toBeVisible()
    // The note reads back from the draft, which is re-seeded from the daemon's reply — so this
    // fails if the UI ever goes back to echoing what it posted.
    await expect(page.getByTestId('preset-sustained-note')).toContainText('18 W')
  })

  test('display page shows a working brightness slider', async ({ page }) => {
    await page.getByTestId('nav-display').click()
    const b = page.getByTestId('brightness')
    await expect(b).toBeVisible()
    await b.fill('55')
    await expect(b).toHaveValue('55')
  })
})

