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

  test('a repeated condition shows as one row with a count', async ({ page, request }) => {
    // The daemon coalesces: 62 firings of the same thermal condition become one alert with
    // count = 62. Before that, the alert centre was 62 near-identical rows and unreadable.
    await request.post('http://127.0.0.1:8799/alerts/_test-seed', {
      data: { alerts: [{ count: 62, severity: 'Aviso', message: 'CPU 91°C — easing to 24 W' }] },
    })

    await page.addInitScript(() => localStorage.setItem('forge-setup-done', '1'))
    await page.goto('/#alerts')
    await expect(page.getByTestId('page-alerts')).toBeVisible()

    await expect(page.locator('.alert-card')).toHaveCount(1)
    await expect(page.getByTestId('alert-count-seed-0')).toContainText('62')

    // Clean up so the empty-state test is not order-dependent.
    await request.post('http://127.0.0.1:8799/alerts/_test-seed', { data: { alerts: [] } })
  })
})
