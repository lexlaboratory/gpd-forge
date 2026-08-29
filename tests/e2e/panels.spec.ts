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

    // Scoped by its own command text, not .first() — the shared mock daemon can already hold
    // jobs created by other AI-mode specs (e.g. ai.spec.ts) that ran earlier in the suite.
    const row = page.getByTestId('job-row').filter({ hasText: 'llama-70b eval' })
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

  test('Standby mode surfaces the night the machine did not wake up', async ({ page }) => {
    // The whole point of parsing the sleep study: the System event log routinely records no standby
    // transition at all for exactly the nights that end in a hand power-cycle, so if this does not
    // reach the panel the user has no way to see it short of a console probe.
    await dash.pickMode('standby')

    const findings = page.getByTestId('standby-sleepstudy')
    await expect(findings).toBeVisible()
    await expect(findings).toContainText('did not wake up')
    await expect(findings).toContainText('0x133')

    // "Not sampled yet" and "sampled, nothing found" must not be showing at the same time.
    await expect(page.getByTestId('standby-sleepstudy-pending')).toBeHidden()
    await expect(page.getByTestId('standby-sleepstudy-clean')).toBeHidden()
  })

  test('panels are hidden in other modes', async ({ page }) => {
    await dash.pickMode('windows')
    await expect(page.getByTestId('jobs-panel')).toBeHidden()
    await expect(page.getByTestId('standby-panel')).toBeHidden()
  })
})
