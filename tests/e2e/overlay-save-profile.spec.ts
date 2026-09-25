// GPD Forge — the overlay's "Save as profile for this game". GPL-3.0-or-later.
//
// F1 (2026-09-25). Mid-game, with a controller in hand, the settings that feel right are already in
// force: this captures them (TDP, frame cap, fan) into the game's rule in one press. The game is the
// app presenting frames, else the app a rule decided on (`lastMatch.process` with a `ruleId`) — which
// the mock fixes as `steam`, a seeded rule. That rule's overrides are cleared after every test.
import { test, expect, type APIRequestContext, type Page } from './fixtures'

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

/** GET /tdp as the daemon answers it with `watts` in force and no manual override. */
const tdpBody = (watts: number) => ({
  stapmW: watts, owner: 'profile', verified: true, backend: 'stub', observedStapmW: watts, observedPptW: null,
  attempts: 1, atUtc: '2026-09-25T10:00:00Z', note: null, manualStapmW: null, intentStapmW: watts,
})

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
    // Each pick lands before the next step: Save reads the fan and TDP back from the daemon, and a GET
    // on another connection can overtake a POST still in flight (seen once in audit round 2's runs).
    const posted = (path: string) => page.waitForResponse((r) => r.url().endsWith(path) && r.request().method() === 'POST')
    await Promise.all([posted('/fan'), page.getByTestId('qam-fan-Quiet').click()])
    await Promise.all([posted('/tdp'), page.getByTestId('qam-tdp-inc').click()])
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

  // F1 audit round 1 (2026-09-25): with an UNRULED game in front, opening the overlay (an Edge --app
  // window) made the focus loop's app `msedge`, and the button saved `msedge -> gaming` with the game's
  // watts. The app presenting frames is the game; a listed non-game window no rule decided on is not.
  test("an unruled game under the overlay is the one saved, never the overlay's own Edge window", async ({ page, request }) => {
    const real = await (await request.get(`${API}/app-rules`)).json()
    await page.route(`${API}/app-rules`, (route) => route.request().method() === 'GET'
      ? route.fulfill({ json: { ...real, lastMatch: { ...real.lastMatch, ruleId: null, match: null, mode: 'windows', process: 'msedge' } } })
      : route.continue())
    await page.route(`${API}/sessions*`, (route) => route.fulfill({ json: { fpsAvailable: true, current: 'eldenring.exe', sessions: [] } }))
    try {
      await page.goto('/overlay.html')
      const save = page.getByTestId('qam-save-profile')
      await expect(save).toBeEnabled()
      await expect(save).toContainText('eldenring')
      await expect(save).not.toContainText('msedge')
      await save.click()
      await expect(page.getByTestId('toast-success')).toContainText('eldenring')
      const { rules } = await (await request.get(`${API}/app-rules`)).json()
      expect(rules.map((r: { match: string }) => r.match)).toContain('eldenring')
      expect(rules.map((r: { match: string }) => r.match)).not.toContain('msedge')
    } finally {
      const { rules } = await (await request.get(`${API}/app-rules`)).json()
      for (const r of rules) if (r.match === 'eldenring' || r.match === 'msedge') await request.delete(`${API}/app-rules/${r.id}`)
    }
  })

  test("only the overlay's window in front, with nothing presenting: nothing to save", async ({ page }) => {
    await page.route(`${API}/app-rules`, (route) => route.fulfill({
      json: {
        rules: [], modes: ['battery', 'windows', 'gaming', 'ai'], autoProfiles: true,
        lastMatch: { ruleId: null, match: null, mode: 'windows', process: 'msedge', acConnected: true, atUtc: '2026-09-25T10:00:00Z' },
      },
    }))
    await page.route(`${API}/sessions*`, (route) => route.fulfill({ json: { fpsAvailable: true, current: null, sessions: [] } }))
    await page.goto('/overlay.html')
    await expect(page.getByTestId('qam')).toBeVisible()
    await expect(page.getByTestId('qam-save-profile')).toBeDisabled()
    await expect(page.getByTestId('qam-save-profile')).toContainText('No game in front')
  })

  // F1 audit round 1: the capture took the stepper's value, seeded once from the last write by ANY
  // owner — opened mid-throttle, it stored the guardian's ceiling as the game's permanent watts.
  test("a guardian throttle in force is not captured: the user's own TDP is", async ({ page, request }) => {
    await page.route(`${API}/tdp`, (route) => route.request().method() === 'GET'
      ? route.fulfill({
        json: {
          stapmW: 12, owner: 'thermal-guardian', verified: true, backend: 'stub', observedStapmW: 12, observedPptW: null,
          attempts: 1, atUtc: '2026-09-25T10:00:00Z', note: null, manualStapmW: null, intentStapmW: 25,
        },
      })
      : route.continue())
    await openOverlay(page)
    await expect(page.getByTestId('qam-tdp')).toContainText('12')   // what is in force now: the throttle
    await page.getByTestId('qam-save-profile').click()
    await expect(page.getByTestId('toast-success')).toContainText('25 W')
    expect((await steamRule(request)).overrides).toMatchObject({ stapmW: 25 })
  })

  test('the fan the daemon holds is captured, not a placeholder, and a refused pick is undone', async ({ page, request }) => {
    await request.post(`${API}/fan`, { data: { mode: 'Balanced' } })
    await page.route(`${API}/fan`, (route) => route.request().method() === 'POST'
      ? route.fulfill({ status: 500, json: { error: 'EC write failed' } })
      : route.continue())
    await openOverlay(page)
    await expect(page.getByTestId('qam-fan-Balanced')).toHaveAttribute('aria-checked', 'true')

    await page.getByTestId('qam-fan-Quiet').click()
    await expect(page.getByTestId('toast-error')).toContainText('Fan was not changed')
    await expect(page.getByTestId('qam-fan-Balanced')).toHaveAttribute('aria-checked', 'true')

    await page.getByTestId('qam-save-profile').click()
    await expect(page.getByTestId('toast-success')).toBeVisible()
    expect((await steamRule(request)).overrides).toMatchObject({ fanMode: 'Balanced' })
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

  // F1 audit round 2 (2026-09-25): the recorder's `current` is whatever FrameTarget chose, and with no
  // game presenting that is a non-game presenter — on the desktop, dwm; with a browser in front, chrome.
  // The button offered to save `dwm -> gaming` with the watts in force.
  test("a non-game presenter in the recorder's `current` is never offered as the game", async ({ page }) => {
    await page.route(`${API}/app-rules`, (route) => route.fulfill({
      json: {
        rules: [], modes: ['battery', 'windows', 'gaming', 'ai'], autoProfiles: true,
        lastMatch: { ruleId: null, match: null, mode: 'windows', process: 'msedge', acConnected: true, atUtc: '2026-09-25T10:00:00Z' },
      },
    }))
    for (const current of ['dwm.exe', 'chrome.exe']) {
      await page.unroute(`${API}/sessions*`)
      await page.route(`${API}/sessions*`, (route) => route.fulfill({ json: { fpsAvailable: true, current, sessions: [] } }))
      await page.goto('/overlay.html')
      await expect(page.getByTestId('qam')).toBeVisible()
      await expect(page.getByTestId('qam-save-profile')).toBeDisabled()
      await expect(page.getByTestId('qam-save-profile')).toContainText('No game in front')
    }
  })

  // F1 audit round 2: a profile settles ~4.5 s after focus, often after the overlay opened. The cap row
  // and the TDP stepper kept the values read at open (cap Off, the preset's 20 W) under a header saying
  // "22 W · 60 FPS", the stepper's + then stepped DOWN to 21 W, and Save wrote "no FPS cap" over the
  // game's 60. Now both rows re-read when the profile changes, and Save reads the cap at the press.
  test('a profile that goes on after the overlay opened: the rows follow it, and Save keeps its cap', async ({ page, request }) => {
    const steam = await steamRule(request)
    let on = false
    await page.route(`${API}/profiles/active`, (route) => route.fulfill({
      json: on
        ? {
          active: true, game: 'steam', ruleId: steam.id, match: 'steam', mode: 'gaming',
          applied: { stapmW: 22, frameCapFps: 60, fanMode: null, gpu: { antiLag: null, chill: null } },
          skipped: [], superseded: [], freeze: [], sinceUtc: '2026-09-25T10:00:00Z',
        }
        : {
          active: false, game: null, ruleId: null, match: null, mode: null, applied: null,
          skipped: [], superseded: [], freeze: [], sinceUtc: null,
        },
    }))
    await page.route('**/gpu', (route) => route.fulfill({
      json: {
        available: true, status: 'Ready', adapter: 'AMD Radeon(TM) 890M Graphics', detail: 'ok',
        settings: { frameRateCap: { supported: true, enabled: on, value: 60, min: 15, max: 1000 } },
      },
    }))
    // The daemon's request, which the agent reconciles a tick later: the answer Save must take first.
    await page.route(`${API}/gpu/desired`, (route) => route.fulfill({
      json: { requested: on, frameCapFps: on ? 60 : null, requestedAtUtc: null, antiLag: null, chill: null },
    }))
    await page.route(`${API}/tdp`, (route) => route.request().method() === 'GET'
      ? route.fulfill({ json: tdpBody(on ? 22 : 20) })
      : route.continue())

    await openOverlay(page)
    await expect(page.getByTestId('qam-tdp')).toContainText('20')
    await expect(page.getByTestId('qam-cap-0')).toHaveAttribute('aria-checked', 'true')

    on = true
    await expect(page.getByTestId('qam-profile')).toContainText('60 FPS', { timeout: 8000 })
    await expect(page.getByTestId('qam-cap-60')).toHaveAttribute('aria-checked', 'true')
    await expect(page.getByTestId('qam-tdp')).toContainText('22')

    await page.getByTestId('qam-save-profile').click()
    await expect(page.getByTestId('toast-success')).toContainText('60 FPS')
    expect((await steamRule(request)).overrides).toMatchObject({ stapmW: 22, frameCapFps: 60 })
  })

  // F1 audit round 2: with the GPU agent silent or stale the cap row is hidden and the cap unknown —
  // and Save sent frameCapFps null, which erased the game's stored cap. Unknown now keeps it, as the
  // Radeon toggles and the freeze list already were.
  test("a cap that cannot be read at the press keeps the game's stored cap", async ({ page, request }) => {
    const steam = await steamRule(request)
    await request.put(`${API}/app-rules/${steam.id}`, {
      data: { match: 'steam', mode: steam.mode, enabled: true, overrides: { stapmW: 20, frameCapFps: 60, fanMode: null, gpu: null, freeze: null } },
    })
    await openOverlay(page)   // the mock's GPU agent has never reported
    await page.getByTestId('qam-save-profile').click()
    await expect(page.getByTestId('toast-success')).toContainText('60 FPS')
    expect((await steamRule(request)).overrides).toMatchObject({ frameCapFps: 60 })
  })

  // F1 audit round 2: the rule's mode came from panel state seeded once at open; a mode picked in the
  // main window afterwards saved the new rule under the old one.
  test('a new rule takes the mode in force at the press, not the one at open', async ({ page, request }) => {
    await request.post(`${API}/mode`, { data: { name: 'windows' } })
    await page.route(`${API}/sessions*`, (route) => route.fulfill({ json: { fpsAvailable: true, current: 'hades2.exe', sessions: [] } }))
    try {
      await page.goto('/overlay.html')
      const save = page.getByTestId('qam-save-profile')
      await expect(save).toContainText('hades2')
      await expect(page.getByTestId('qam-mode-windows')).toHaveAttribute('aria-pressed', 'true')
      await expect(page.getByTestId('qam-tdp')).not.toContainText('--')
      await request.post(`${API}/mode`, { data: { name: 'ai' } })
      await save.click()
      await expect(page.getByTestId('toast-success')).toContainText('hades2')
      const { rules } = await (await request.get(`${API}/app-rules`)).json()
      expect(rules.find((r: { match: string }) => r.match === 'hades2')?.mode).toBe('ai')
    } finally {
      await request.post(`${API}/mode`, { data: { name: 'windows' } })
      const { rules } = await (await request.get(`${API}/app-rules`)).json()
      for (const r of rules) if (r.match === 'hades2') await request.delete(`${API}/app-rules/${r.id}`)
    }
  })
})
