// GPD Forge — the overlay's "Save as profile for this game". GPL-3.0-or-later.
//
// F1 (2026-09-25). Mid-game, with a controller in hand, the settings that feel right are already in
// force: this captures them (TDP, frame cap, fan) into the game's rule in one press. The game is the
// one the daemon's focus loop judged — `lastMatch.process`, the game under the overlay — which the
// mock fixes as `steam`, a seeded rule. That rule's overrides are cleared after every test.
import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

const API = 'http://127.0.0.1:8799'

async function steamRule(request: APIRequestContext) {
  const { rules } = await (await request.get(`${API}/app-rules`)).json()
  return rules.find((r: { match: string }) => r.match === 'steam')
}
async function clearSteam(request: APIRequestContext) {
  const steam = await steamRule(request)
  await request.put(`${API}/app-rules/${steam.id}`, { data: { match: 'steam', mode: steam.mode, enabled: true, overrides: null } })
}

/** The mock's GPU is unavailable by default; the cap case needs a driver that offers FRTC. */
async function withFrtc(page: Page) {
  await page.route('**/gpu', (route) => route.fulfill({
    json: {
      available: true, status: 'Ready', adapter: 'AMD Radeon(TM) 890M Graphics', detail: 'ok',
      settings: { frameRateCap: { supported: true, enabled: false, value: 60, min: 15, max: 1000 } },
    },
  }))
}

async function openOverlay(page: Page) {
  await page.goto('/overlay.html')
  await expect(page.getByTestId('qam')).toBeVisible()
  const save = page.getByTestId('qam-save-profile')
  await expect(save).toBeEnabled()
  await expect(save).toContainText('steam')
  // The stepper must hold the value in force before a capture means anything.
  await expect(page.getByTestId('qam-tdp')).not.toContainText('--')
}

test.describe('Overlay: save as profile for this game', () => {
  test.beforeEach(async ({ request }) => {
    await clearSteam(request)
    await request.post(`${API}/fan`, { data: { mode: 'Auto' } })
    await request.post(`${API}/auto-fps`, { data: { enable: false, targetFps: 60 } })
  })
  test.afterEach(async ({ request }) => {
    await clearSteam(request)
    await request.post(`${API}/fan`, { data: { mode: 'Auto' } })
  })

  test('captures the TDP and fan in force into the game\'s rule, and says so', async ({ page, request }) => {
    await openOverlay(page)
    await page.getByTestId('qam-fan-Quiet').click()
    await page.getByTestId('qam-tdp-inc').click()
    const tdp = Number((await page.getByTestId('qam-tdp').textContent())!.replace(/\D/g, ''))

    await page.getByTestId('qam-save-profile').click()
    await expect(page.getByTestId('toast-success')).toContainText(`${tdp} W`)

    const saved = await steamRule(request)
    expect(saved.overrides).toMatchObject({ stapmW: tdp, fanMode: 'Quiet', frameCapFps: null })
    // The mode a seeded rule picks is not the overlay's to change.
    expect(saved.mode).toBe('gaming')
    // And the profile now in force shows in the header.
    await expect(page.getByTestId('qam-profile')).toContainText(`${tdp} W`, { timeout: 8000 })
  })

  test('captures the driver frame cap when the GPU offers one', async ({ page, request }) => {
    await withFrtc(page)
    await openOverlay(page)
    await page.getByTestId('qam-cap-45').click()
    await expect(page.getByTestId('qam-cap-45')).toHaveAttribute('aria-checked', 'true')
    await page.getByTestId('qam-save-profile').click()
    await expect(page.getByTestId('toast-success')).toBeVisible()
    expect((await steamRule(request)).overrides).toMatchObject({ frameCapFps: 45 })
  })

  test('a refused save is reported with the daemon\'s sentence', async ({ page, request }) => {
    await page.route(`${API}/app-rules/*`, (route) => route.request().method() === 'PUT'
      ? route.fulfill({ status: 400, json: { error: 'stapmW must be between 5 and 40 W.', code: 'bad_stapm' } })
      : route.continue())
    await openOverlay(page)
    await page.getByTestId('qam-save-profile').click()
    await expect(page.getByTestId('toast-error')).toContainText('stapmW must be between 5 and 40 W.')
    expect((await steamRule(request)).overrides).toBeNull()
  })

  test('with no game in front the button says so instead of saving', async ({ page }) => {
    // A static body, not route.fetch() + edit: the overlay re-reads this every 5 s, and a proxied fetch
    // still in flight when the test ends is aborted and fails the test after it has passed (seen in the
    // first full run).
    await page.route(`${API}/app-rules`, (route) => route.fulfill({
      json: {
        rules: [], modes: ['battery', 'windows', 'gaming', 'ai'], autoProfiles: true,
        lastMatch: { ruleId: null, match: null, mode: 'windows', process: null, acConnected: true, atUtc: '2026-09-25T10:00:00Z' },
      },
    }))
    await page.route(`${API}/sessions*`, (route) => route.fulfill({ json: { fpsAvailable: true, current: null, sessions: [] } }))
    await page.goto('/overlay.html')
    await expect(page.getByTestId('qam')).toBeVisible()
    await expect(page.getByTestId('qam-save-profile')).toBeDisabled()
    await expect(page.getByTestId('qam-save-profile')).toContainText('No game in front')
  })
})
