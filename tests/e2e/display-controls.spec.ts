// GPD Forge — display controls (refresh rate + night mode) E2E. GPL-3.0-or-later.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('Display controls', () => {
  test('refresh-rate control switches and night mode round-trips against the mock', async ({ page }) => {
    await new DashboardPage(page).goto()
    await page.getByTestId('nav-display').click()

    // Refresh rate: click both supported rates (mock: [48, 60]) and confirm the selection follows —
    // self-contained (doesn't assume which one is active first), so it's safe under CI retries.
    const hz48 = page.getByTestId('refresh-48')
    const hz60 = page.getByTestId('refresh-60')
    await hz48.click()
    await expect(hz48).toHaveClass(/on/)
    await expect(hz60).not.toHaveClass(/on/)

    await hz60.click()
    await expect(hz60).toHaveClass(/on/)
    await expect(hz48).not.toHaveClass(/on/)

    // Night mode: round-trip the toggle, persisted through the daemon each time. Reads the current
    // state first rather than assuming off, so a CI retry against already-mutated mock state is safe.
    const toggle = page.getByTestId('night-toggle')
    const wasOn = (await toggle.getAttribute('aria-pressed')) === 'true'

    await toggle.click()
    await expect(toggle).toHaveAttribute('aria-pressed', wasOn ? 'false' : 'true')

    await toggle.click()
    await expect(toggle).toHaveAttribute('aria-pressed', wasOn ? 'true' : 'false')
  })
})
