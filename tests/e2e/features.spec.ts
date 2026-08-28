// GPD Forge — new-feature E2E (auto-FPS, freezer, live charts). GPL-3.0-or-later.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('Features', () => {
  test.beforeEach(async ({ page }) => { await new DashboardPage(page).goto() })

  test('auto-TDP-to-FPS toggle round-trips', async ({ page }) => {
    await page.getByTestId('nav-power').click()
    const t = page.getByTestId('autofps-toggle')
    await expect(t).toHaveAttribute('aria-pressed', 'false')
    await t.click()
    await expect(t).toHaveAttribute('aria-pressed', 'true')
  })

  test('freezer freezes and lists a process', async ({ page }) => {
    await page.getByTestId('nav-system').click()
    await page.getByTestId('freezer-name').fill('chrome')
    await page.getByTestId('freezer-freeze').click()
    await expect(page.getByTestId('frozen-list')).toContainText('chrome')
  })

  test('monitor shows live sparkline charts', async ({ page }) => {
    await page.getByTestId('nav-monitor').click()
    await expect(page.getByTestId('chart-cpu')).toBeVisible()
    await expect(page.getByTestId('chart-watt')).toBeVisible()
    await expect(page.getByTestId('chart-fps')).toBeVisible()
  })

  test('manual fan duty round-trips through the daemon', async ({ page, request }) => {
    await page.getByTestId('nav-fan').click()

    const manualResponse = page.waitForResponse((response) =>
      response.url().endsWith('/fan') && response.request().method() === 'POST',
    )
    await page.getByTestId('fan-manual').click()
    await manualResponse

    const duty = page.getByTestId('fan-manual-duty')
    await expect(duty).toBeVisible()
    await duty.fill('173')

    const dutyResponse = page.waitForResponse((response) =>
      response.url().endsWith('/fan') && response.request().method() === 'POST',
    )
    // pointerup, not mouseup: the slider commits on pointerup so that a touch drag on the handheld
    // also applies. A real mouse fires both; only a synthesised event has to pick one.
    await duty.dispatchEvent('pointerup')
    await dutyResponse

    const persisted = await request.get('http://127.0.0.1:8799/fan')
    expect(await persisted.json()).toMatchObject({ mode: 'Manual', manualDuty: 173, controllable: true })
  })
})
