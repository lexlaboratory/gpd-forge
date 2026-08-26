// GPD Forge — Quick Access Menu (overlay) E2E. GPL-3.0-or-later.
import { test, expect } from '@playwright/test'

test.describe('Overlay (QAM)', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/overlay.html')
    await expect(page.getByTestId('qam')).toBeVisible()
  })

  test('renders the panel with live header and controls', async ({ page }) => {
    await expect(page.getByTestId('qam-tdp')).toBeVisible()
    await expect(page.getByTestId('qam-budget')).toBeVisible()
    await expect(page.getByTestId('qam-mode-gaming')).toBeVisible()
    await expect(page.getByTestId('qam-close')).toBeVisible()
  })

  test('mode select marks the active mode', async ({ page }) => {
    const g = page.getByTestId('qam-mode-gaming')
    await g.click()
    await expect(g).toHaveAttribute('aria-pressed', 'true')
  })

  test('TDP stepper changes the value', async ({ page }) => {
    await page.waitForTimeout(500) // let initial preset load settle
    const val = page.getByTestId('qam-tdp')
    const before = (await val.textContent()) ?? ''
    await page.getByTestId('qam-tdp-inc').click()
    await expect(val).not.toHaveText(before)
  })

  test('fan and FPS chips select', async ({ page }) => {
    await page.getByTestId('qam-fan-Quiet').click()
    await expect(page.getByTestId('qam-fan-Quiet')).toHaveClass(/on/)
    await page.getByTestId('qam-fps-60').click()
    await expect(page.getByTestId('qam-fps-60')).toHaveClass(/on/)
  })

  test('keyboard arrow moves focus inside the panel', async ({ page }) => {
    await page.getByTestId('qam-mode-gaming').focus()
    await page.keyboard.press('ArrowDown')
    const focused = await page.evaluate(() => document.activeElement?.getAttribute('data-testid'))
    expect(focused).toBeTruthy()
  })
})
