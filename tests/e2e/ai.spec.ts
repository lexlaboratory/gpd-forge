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

  // P3.2 — the keep-awake for inference GPD Forge did NOT start. The shipped default is observe-only,
  // so that is what the panel must communicate: "we can see it, we are not acting on it". A previous
  // release taught this repo that a panel which silently implies enforcement is worse than no panel.
  test('the inference keep-awake says it is observing, not enforcing, by default', async ({ page }) => {
    const dash = new DashboardPage(page)
    await dash.goto()
    await dash.pickMode('ai')

    await expect(page.getByTestId('ai-inference-hold')).toContainText('Observing only')
    // Nothing is working in the default mock state, so no process may be attributed a hold.
    await expect(page.getByTestId('ai-inference-workers')).toHaveCount(0)
  })

  // P3.3 — the confirmation half. The summary must be rendered rather than a verdict re-derived in the
  // UI from reportedMb: that number saturates at the uint32 4095/4096 MB ceiling, and a client that
  // subtracts two of them will invent a change that never happened.
  test('the VRAM panel shows the persisted-observation summary alongside the advisory', async ({ page }) => {
    const dash = new DashboardPage(page)
    await dash.goto()
    await dash.pickMode('ai')

    await expect(page.getByTestId('ai-vram-history')).toContainText('Baseline recorded')
  })

  // The contract, checked against the DAEMON rather than the rendered page. This repo shipped alert
  // severities as ints in production while the mock emitted strings, and every UI test stayed green;
  // asserting the wire shape directly is the cheap half of not repeating that.
  test('GET /ai/inference-hold answers with the documented shape and honest nulls', async ({ request }) => {
    const res = await request.get('http://127.0.0.1:8799/ai/inference-hold')
    expect(res.ok()).toBeTruthy()
    const body = await res.json()

    expect(typeof body.enforcing).toBe('boolean')
    expect(typeof body.holding).toBe('boolean')
    expect(Array.isArray(body.workers)).toBeTruthy()
    expect(Array.isArray(body.watchedNames)).toBeTruthy()
    // Not held => holdingSince MUST be null, never a plausible timestamp.
    if (!body.holding) expect(body.holdingSince).toBeNull()
  })

  test('GET /ai carries the inference-hold summary and the VRAM history', async ({ request }) => {
    const res = await request.get('http://127.0.0.1:8799/ai')
    expect(res.ok()).toBeTruthy()
    const body = await res.json()

    expect(body.inferenceHold).toBeDefined()
    expect(Array.isArray(body.inferenceHold.workers)).toBeTruthy()
    expect(body.vram.history).toBeDefined()
    expect(typeof body.vram.history.summary).toBe('string')
    // "not established" is not "did not happen" — the flag must exist and be boolean either way.
    expect(typeof body.vram.history.rebootConfirmed).toBe('boolean')
  })
})
