// GPD Forge — telemetry history + CSV export E2E. GPL-3.0-or-later.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

// Mock daemon's fixed E2E port (see playwright.config.ts webServer) — the same origin the built UI
// talks to via VITE_FORGE_API, so this hits the real contract directly, not just the UI's rendering of it.
const DAEMON = 'http://127.0.0.1:8799'

test.describe('Telemetry history', () => {
  test('monitor shows the export control and the daemon has history to export', async ({ page }) => {
    const dash = new DashboardPage(page)
    await dash.goto()
    await page.getByTestId('nav-monitor').click()

    const exportBtn = page.getByTestId('history-export')
    await expect(exportBtn).toBeVisible()
    await expect(exportBtn).toHaveAttribute('href', /\/history\/export\.csv$/)

    // The dashboard has been polling GET /telemetry since goto(), which is what feeds the mock's
    // ring buffer, so /history should already show at least one sample.
    await expect(page.getByTestId('history-count')).toContainText('sample')

    const res = await page.request.get(`${DAEMON}/history?minutes=5`)
    expect(res.ok()).toBeTruthy()
    const body = await res.json()
    expect(Array.isArray(body.samples)).toBe(true)
    expect(body.samples.length).toBeGreaterThan(0)
  })
})
