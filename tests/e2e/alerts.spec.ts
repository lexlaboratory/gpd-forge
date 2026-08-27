import { test, expect } from '@playwright/test'

test.describe('Alert center', () => {
  test('opens from navigation and shows an honest empty state', async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('forge-setup-done', '1'))
    await page.goto('/')
    await page.getByTestId('nav-alerts').click()
    await expect(page.getByTestId('page-alerts')).toBeVisible()
    await expect(page.getByTestId('alerts-empty')).toContainText('No alerts')
  })

  test('deep link from the tray opens the alert center', async ({ page }) => {
    await page.goto('/#alerts')
    await expect(page.getByTestId('page-alerts')).toBeVisible()
  })
})
