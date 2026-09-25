// GPD Forge — "automatic with notice": a game profile that takes effect is announced. GPL-3.0-or-later.
//
// F1 (2026-09-25). The mock reports as active the profile of the rule claiming its fixed foreground
// (`steam`, a seeded rule), so giving that rule overrides is how these tests make a profile "apply".
// It is always cleared again: every other spec expects the seeded rule to carry none.
import { test, expect, type APIRequestContext } from '@playwright/test'

const API = 'http://127.0.0.1:8799'
const PROFILE = { stapmW: 22, frameCapFps: 60, fanMode: 'Aggressive' }
const NOTICE = 'Profile steam applied: 22 W · 60 FPS · Aggressive'

async function setSteamOverrides(request: APIRequestContext, overrides: Record<string, unknown> | null) {
  const { rules } = await (await request.get(`${API}/app-rules`)).json()
  const steam = rules.find((r: { match: string }) => r.match === 'steam')
  const res = await request.put(`${API}/app-rules/${steam.id}`, { data: { match: 'steam', mode: steam.mode, enabled: true, overrides } })
  expect(res.ok()).toBeTruthy()
}

test.describe('Profile notice', () => {
  test.beforeEach(async ({ request }) => {
    await setSteamOverrides(request, null)
    // A manual TDP an earlier spec left behind (the overlay's stepper) outranks the game's watts, and
    // the notice then rightly says "TDP changed by you" — seen in the first full run of F1 audit
    // round 2. Picking a mode ends the override, as it does on the daemon.
    await request.post(`${API}/mode`, { data: { name: 'windows' } })
  })
  test.afterEach(async ({ request }) => { await setSteamOverrides(request, null) })

  test('the main window toasts a profile the moment it applies', async ({ page, request }) => {
    await page.addInitScript(() => localStorage.setItem('forge-setup-done', '1'))
    await page.goto('/')
    await expect(page.getByTestId('conn')).toHaveText('Live')
    // Let the first poll land as the baseline, then make the profile apply.
    await page.waitForResponse((r) => r.url().endsWith('/profiles/active'))
    await setSteamOverrides(request, PROFILE)
    await expect(page.getByTestId('toast-info')).toContainText(NOTICE, { timeout: 8000 })
  })

  test('a profile already in force when the window opens is not announced as new', async ({ page, request }) => {
    await setSteamOverrides(request, PROFILE)
    await page.addInitScript(() => localStorage.setItem('forge-setup-done', '1'))
    await page.goto('/')
    // Two polls: the baseline and one more. Neither may toast.
    await page.waitForResponse((r) => r.url().endsWith('/profiles/active'))
    await page.waitForResponse((r) => r.url().endsWith('/profiles/active'), { timeout: 8000 })
    await expect(page.getByText(NOTICE)).toHaveCount(0)
  })

  test('in the overlay\'s 380 px window a long game name gives way, the applied values do not', async ({ page }) => {
    await page.setViewportSize({ width: 380, height: 800 })
    await page.route('**/profiles/active', (route) => route.fulfill({
      json: {
        active: true, game: 'League of Legends.exe', ruleId: 'r', match: 'league of legends', mode: 'gaming',
        applied: { stapmW: 22, frameCapFps: 60, fanMode: 'Aggressive', gpu: { antiLag: null, chill: null } },
        skipped: [], freeze: [], sinceUtc: '2026-09-25T10:00:00Z',
      },
    }))
    await page.goto('/overlay.html')
    const line = page.getByTestId('qam-profile')
    await expect(line).toHaveText('Profile League of Legends applied: 22 W · 60 FPS · Aggressive')
    const fits = (sel: string) => line.locator(sel).evaluate((el) => el.scrollWidth <= el.clientWidth + 1)
    expect(await fits('.qam-profile-detail')).toBe(true)
    expect(await fits('.qam-profile-lead')).toBe(false)   // the name is what was cut
    const box = (await line.boundingBox())!
    expect(box.height).toBeLessThan(40)                   // still one line
  })

  // F1 audit round 2 (2026-09-25): a refusal reason is wider than the 380 px window, and the one-line
  // layout cut it with an ellipsis — the full text only in a title tooltip, which a pad or a thumb
  // cannot open. With something skipped the line wraps, whole, in the warning tone.
  test('in the 380 px overlay a refusal is shown whole, wrapped, and marked as a warning', async ({ page }) => {
    await page.setViewportSize({ width: 380, height: 800 })
    await page.route('**/profiles/active', (route) => route.fulfill({
      json: {
        active: true, game: 'eldenring.exe', ruleId: 'r', match: 'eldenring', mode: 'gaming',
        applied: { stapmW: null, frameCapFps: 60, fanMode: null, gpu: { antiLag: null, chill: null } },
        skipped: [{ field: 'stapmW', reason: 'held at 18 W by the thermal guardian until the device cools down.' }],
        superseded: [], freeze: [], sinceUtc: '2026-09-25T10:00:00Z',
      },
    }))
    await page.goto('/overlay.html')
    const line = page.getByTestId('qam-profile')
    await expect(line).toHaveText('Profile eldenring applied: 60 FPS — TDP not applied: held at 18 W by the thermal guardian until the device cools down.')
    await expect(line).toHaveAttribute('data-skipped', 'true')
    // Nothing clipped: no part of the text overflows the line, in either direction.
    expect(await line.evaluate((el) => el.scrollWidth <= el.clientWidth + 1 && el.scrollHeight <= el.clientHeight + 1)).toBe(true)
    expect((await line.boundingBox())!.height).toBeGreaterThan(40)   // wrapped onto more than one line
  })

  test('the overlay header carries the profile in force as one line', async ({ page, request }) => {
    await page.goto('/overlay.html')
    await expect(page.getByTestId('qam')).toBeVisible()
    await expect(page.getByTestId('qam-profile')).toHaveCount(0)
    await setSteamOverrides(request, PROFILE)
    await expect(page.getByTestId('qam-profile')).toHaveText(NOTICE, { timeout: 8000 })
    await setSteamOverrides(request, null)
    await expect(page.getByTestId('qam-profile')).toHaveCount(0, { timeout: 8000 })
  })
})
