// GPD Forge — Agents/AI mode: anti-standby + sustained profile + VRAM/UMA advisory E2E. GPL-3.0-or-later.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('Agents / AI mode', () => {
  test('shows anti-standby status, sustained profile and the VRAM/UMA advisory', async ({ page }) => {
    const dash = new DashboardPage(page)
    await dash.goto()
    await dash.pickMode('ai')

    await expect(page.getByTestId('ai-antistandby-status')).toBeVisible()
    await expect(page.getByTestId('ai-sustained-stapm')).toBeVisible()
    await expect(page.getByTestId('ai-vram-advisory')).toContainText('BIOS')
  })

  test('manual anti-standby toggle round-trips and updates the status line', async ({ page }) => {
    const dash = new DashboardPage(page)
    await dash.goto()
    await dash.pickMode('ai')

    const toggle = page.getByTestId('ai-antistandby-toggle')
    await expect(toggle).toHaveAttribute('aria-pressed', 'false')

    await toggle.click()
    await expect(toggle).toHaveAttribute('aria-pressed', 'true')
    await expect(page.getByTestId('ai-antistandby-status')).toContainText('Holding Windows awake')

    // toggling back off releases the manual hold (idempotent — never double-releases).
    await toggle.click()
    await expect(toggle).toHaveAttribute('aria-pressed', 'false')
  })

  test('a running AI job holds anti-standby', async ({ page }) => {
    const dash = new DashboardPage(page)
    await dash.goto()
    await dash.pickMode('ai')

    // Uncheck "require AC" so the job runs immediately (the mock starts on battery / AC disconnected).
    await page.getByTestId('job-requireac').uncheck()
    await page.getByTestId('job-cmd').fill('embed corpus')
    await page.getByTestId('job-submit').click()

    const row = page.getByTestId('job-row').filter({ hasText: 'embed corpus' })
    await expect(row).toBeVisible()
    await expect(row.getByTestId('job-status')).toHaveText('running')

    // The daemon holds a real anti-standby lock for as long as this job stays "running".
    await expect(page.getByTestId('ai-antistandby-status')).toContainText('Holding Windows awake')
  })
})
