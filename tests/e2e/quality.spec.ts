// GPD Forge — quality/onboarding E2E: system health check, panic cool, large-text a11y. GPL-3.0-or-later.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('System health & panic cool', () => {
  let dash: DashboardPage
  test.beforeEach(async ({ page }) => { dash = new DashboardPage(page); await dash.goto() })

  test('health card shows the mock daemon\'s warn issue with severity', async ({ page }) => {
    await page.getByTestId('nav-system').click()
    await expect(page.getByTestId('health-card')).toBeVisible()
    await expect(page.getByTestId('health-status')).toHaveText('warn')
    await expect(page.getByTestId('health-issue-fan_not_spinning')).toBeVisible()
    await expect(page.getByTestId('health-issue-fan_not_spinning')).toContainText('Fan not spinning while warm')
  })

  test('panic cool applies the floor and toasts', async ({ page }) => {
    await page.getByTestId('nav-system').click()
    await page.getByTestId('panic-cool').click()
    await expect(page.getByTestId('toast-success')).toBeVisible()
    await expect(page.getByTestId('toast-success')).toContainText('8 W')

    // The mock daemon's telemetry now jitters packageW around the 8W floor — visible on Dashboard
    // once a tick lands (App.tsx polls GET /telemetry every 1s).
    await page.getByTestId('nav-dashboard').click()
    await expect.poll(async () => {
      const text = (await page.getByTestId('stat-pkg').textContent()) ?? ''
      return Number(text.replace(/\D+/g, ''))
    }, { timeout: 5_000 }).toBeLessThanOrEqual(11)

    // The mock daemon is one server shared by the whole suite (see playwright.config.ts) — restore
    // the normal preset for specs that run after this one, mirroring api.spec.ts's own mode reset.
    await dash.pickMode('windows')
  })
})

test.describe('Accessibility — large text', () => {
  test.beforeEach(async ({ page }) => { await new DashboardPage(page).goto() })

  test('toggling large text scales the root font size and persists', async ({ page }) => {
    const before = await page.evaluate(() => getComputedStyle(document.documentElement).fontSize)

    await page.getByTestId('nav-settings').click()
    const toggle = page.getByTestId('settings-textscale')
    await expect(toggle).toHaveAttribute('aria-pressed', 'false')
    await toggle.click()
    await expect(toggle).toHaveAttribute('aria-pressed', 'true')

    const after = await page.evaluate(() => getComputedStyle(document.documentElement).fontSize)
    expect(after).not.toBe(before)
    expect(await page.evaluate(() => localStorage.getItem('forge-textscale'))).toBe('large')
    expect(await page.evaluate(() => document.documentElement.dataset.textscale)).toBe('large')

    // Persists across reload.
    await page.reload()
    await expect(page.getByTestId('device')).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.dataset.textscale)).toBe('large')
  })
})
