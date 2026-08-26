// GPD Forge — first-run setup wizard E2E. GPL-3.0-or-later.
//
// Deliberately does NOT use DashboardPage.goto() (which pre-sets localStorage['forge-setup-done']
// for every other spec) — these tests need the flag genuinely unset/set to exercise the wizard itself.
import { test, expect } from '@playwright/test'

test.describe('First-run setup wizard', () => {
  test('appears on a clean install, walks through, and hides after finishing', async ({ page }) => {
    // A fresh Playwright test gets a fresh browser context (no localStorage) — navigate directly.
    await page.goto('/', { waitUntil: 'domcontentloaded' })
    await expect(page.getByTestId('wizard')).toBeVisible()

    await page.getByTestId('wizard-next').click() // welcome -> incumbents
    await expect(page.getByTestId('wizard-incumbents-status')).toContainText('Clear')

    await page.getByTestId('wizard-next').click() // incumbents -> mode
    await page.getByTestId('wizard-mode-gaming').click()
    await expect(page.getByTestId('wizard-mode-gaming')).toHaveClass(/on/)

    await page.getByTestId('wizard-next').click() // mode -> finish
    await page.getByTestId('wizard-finish').click()

    await expect(page.getByTestId('wizard')).toHaveCount(0)
    await expect(page.getByTestId('device')).toBeVisible() // the normal app underneath is usable
    expect(await page.evaluate(() => localStorage.getItem('forge-setup-done'))).toBe('1')
  })

  test('is skippable from any step and still marks setup done', async ({ page }) => {
    await page.goto('/', { waitUntil: 'domcontentloaded' })
    await expect(page.getByTestId('wizard')).toBeVisible()

    await page.getByTestId('wizard-skip').click()

    await expect(page.getByTestId('wizard')).toHaveCount(0)
    expect(await page.evaluate(() => localStorage.getItem('forge-setup-done'))).toBe('1')
  })

  test('does not appear once the flag is already set', async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('forge-setup-done', '1'))
    await page.goto('/', { waitUntil: 'domcontentloaded' })

    await expect(page.getByTestId('device')).toBeVisible({ timeout: 15_000 })
    await expect(page.getByTestId('wizard')).toHaveCount(0)
  })
})
