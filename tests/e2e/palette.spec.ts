// GPD Forge — command palette E2E. GPL-3.0-or-later.
import { test, expect } from '@playwright/test'

test.describe('Command palette', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('forge-setup-done', '1'))
    await page.goto('/', { waitUntil: 'domcontentloaded' })
    await expect(page.getByTestId('device')).toBeVisible({ timeout: 15_000 })
  })

  test('Ctrl+K opens and Escape closes it', async ({ page }) => {
    await expect(page.getByTestId('palette')).toHaveCount(0)
    await page.keyboard.press('Control+k')
    await expect(page.getByTestId('palette')).toBeVisible()
    await page.keyboard.press('Escape')
    await expect(page.getByTestId('palette')).toHaveCount(0)
  })

  test('typing filters commands by prefix', async ({ page }) => {
    await page.keyboard.press('Control+k')
    await page.getByTestId('palette-input').fill('mode ga')
    await expect(page.getByTestId('palette-mode-gaming')).toBeVisible()
    await expect(page.getByTestId('palette-mode-battery')).toHaveCount(0)
  })

  test('a command changes real state through the daemon', async ({ page }) => {
    const modePost = page.waitForResponse(
      (r) => r.url().endsWith('/mode') && r.request().method() === 'POST',
    )
    await page.keyboard.press('Control+k')
    await page.getByTestId('palette-input').fill('mode ai')
    await page.keyboard.press('Enter')
    await modePost

    await expect(page.getByTestId('palette')).toHaveCount(0)
    await expect(page.getByTestId('active-mode')).toContainText('Agents / AI')
  })

  test('a numeric argument is parsed and sent', async ({ page }) => {
    const tdpPost = page.waitForResponse(
      (r) => r.url().endsWith('/tdp') && r.request().method() === 'POST',
    )
    await page.keyboard.press('Control+k')
    await page.getByTestId('palette-input').fill('tdp 18')
    // The trailing number must not stop the command matching.
    await expect(page.getByTestId('palette-tdp')).toBeVisible()
    await page.keyboard.press('Enter')

    const body = (await tdpPost).request().postDataJSON()
    expect(body).toMatchObject({ stapmW: 18 })
  })

  test('an out-of-range argument is refused before it reaches the daemon', async ({ page }) => {
    let posted = false
    page.on('request', (r) => { if (r.url().endsWith('/tdp') && r.method() === 'POST') posted = true })

    await page.keyboard.press('Control+k')
    await page.getByTestId('palette-input').fill('tdp 400')
    await page.keyboard.press('Enter')
    await page.waitForTimeout(600)

    expect(posted, 'a 400 W request must never leave the client').toBe(false)
    // The palette stays open so the mistake can be corrected in place.
    await expect(page.getByTestId('palette')).toBeVisible()
  })

  test('go <section> navigates', async ({ page }) => {
    await page.keyboard.press('Control+k')
    await page.getByTestId('palette-input').fill('go monitor')
    await page.keyboard.press('Enter')
    await expect(page.getByTestId('page-monitor')).toBeVisible()
  })
})
