// GPD Forge — advanced hardware-gated controls: LED/RGB E2E. GPL-3.0-or-later.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('Advanced hardware — LED', () => {
  test('LED mode chips select against the mock', async ({ page }) => {
    await new DashboardPage(page).goto()
    await page.getByTestId('nav-power').click()

    // Self-contained (doesn't assume which mode is active first), so it's safe under CI retries —
    // same pattern as the refresh-rate check in display-controls.spec.ts.
    const solid = page.getByTestId('led-solid')
    const breathe = page.getByTestId('led-breathe')

    await solid.click()
    await expect(solid).toHaveClass(/on/)
    await expect(breathe).not.toHaveClass(/on/)

    await breathe.click()
    await expect(breathe).toHaveClass(/on/)
    await expect(solid).not.toHaveClass(/on/)
  })
})
