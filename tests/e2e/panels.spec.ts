// GPD Forge — E2E for the mode panels (Jobs / Standby). GPL-3.0-or-later.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('Mode panels', () => {
  let dash: DashboardPage

  test.beforeEach(async ({ page }) => {
    dash = new DashboardPage(page)
    await dash.goto()
  })

  test('AI mode shows the job queue and blocks a require-AC job on battery', async ({ page }) => {
    await dash.pickMode('ai')
    await expect(page.getByTestId('jobs-panel')).toBeVisible()

    // requireAC is checked by default; on battery the daemon must report the job blocked.
    await page.getByTestId('job-cmd').fill('llama-70b eval')
    await page.getByTestId('job-submit').click()

    const row = page.getByTestId('job-row').first()
    await expect(row).toBeVisible()
    await expect(row.getByTestId('job-status')).toHaveText('blocked')
  })

  test('Standby mode shows diagnostics and runs a resume restore', async ({ page }) => {
    await dash.pickMode('standby')
    await expect(page.getByTestId('standby-panel')).toBeVisible()
    await expect(page.getByTestId('standby-wake')).toContainText('Fingerprint')

    await page.getByTestId('standby-restore').click()
    await expect(page.getByTestId('standby-restored')).toContainText('tdp')
    await expect(page.getByTestId('standby-restored')).toContainText('fan')
    await expect(page.getByTestId('standby-restored')).toContainText('hid')
  })

  test('panels are hidden in other modes', async ({ page }) => {
    await dash.pickMode('windows')
    await expect(page.getByTestId('jobs-panel')).toBeHidden()
    await expect(page.getByTestId('standby-panel')).toBeHidden()
  })
})
