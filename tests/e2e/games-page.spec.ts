// GPD Forge — the Games page: detected games and their per-game profiles. GPL-3.0-or-later.
//
// F1 (2026-09-25). Built against the mock daemon's play history (cyberpunk2077: two sessions, hades2:
// one with no FPS reading) and its seeded rules, none of which claims either game. Every test that
// writes a rule removes it again: the suite shares one mock, and visual.spec reads the same list.
import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

const API = 'http://127.0.0.1:8799'
type Rule = { id: string; match: string; mode: string; enabled: boolean; overrides: Record<string, unknown> | null }

async function rulesNow(request: APIRequestContext): Promise<Rule[]> {
  return (await (await request.get(`${API}/app-rules`)).json()).rules
}
async function removeRules(request: APIRequestContext, ...matches: string[]) {
  for (const r of await rulesNow(request)) {
    if (matches.includes(r.match)) await request.delete(`${API}/app-rules/${r.id}`)
  }
}

async function openGames(page: Page, density: 'pad' | 'mouse' = 'mouse') {
  await page.addInitScript((d) => {
    localStorage.setItem('forge-setup-done', '1')
    localStorage.setItem('forge-density', d)
  }, density)
  await page.goto('/#games')
  await expect(page.getByTestId('page-games')).toBeVisible()
  await expect(page.getByTestId('games-card-cyberpunk2077')).toBeVisible()
}

test.describe('Games page', () => {
  test.afterEach(async ({ request }) => { await removeRules(request, 'cyberpunk2077', 'cyber', 'hades2') })

  test('is in the rail between Profiles and Monitor', async ({ page }) => {
    await openGames(page)
    const order = await page.locator('.nav-item').evaluateAll((els) => els.map((e) => e.getAttribute('data-testid')))
    const at = order.indexOf('nav-games')
    expect(order[at - 1]).toBe('nav-profiles')
    expect(order[at + 1]).toBe('nav-monitor')
    await expect(page.getByTestId('nav-games')).toHaveAttribute('aria-current', 'page')
  })

  test('lists each detected game with its numbers, and no invented zeros', async ({ page }) => {
    await openGames(page)
    const cp = page.getByTestId('games-card-cyberpunk2077')
    await expect(cp.getByTestId('games-sessions-cyberpunk2077')).toContainText('2')
    // Duration-weighted, as the daemon rolls it up: (61.8 x 1 h + 52.4 x 1.5 h) / 2.5 h.
    await expect(cp.getByTestId('games-fps-cyberpunk2077')).toContainText('56.2')
    await expect(cp.getByTestId('games-low-cyberpunk2077')).toContainText('40.5')
    // Duration-weighted package power: (31.4 W x 1 h + 24.6 W x 1.5 h) / 2.5 h.
    await expect(cp.getByTestId('games-watts-cyberpunk2077')).toContainText('27.3')
    await expect(cp.getByTestId('games-state-cyberpunk2077')).toHaveText('No profile')
    // hades2's only session never produced a frame-rate reading: a dash, not "0".
    await expect(page.getByTestId('games-fps-hades2')).toContainText('—')
    await expect(page.getByTestId('games-fps-hades2')).not.toContainText('0')
  })

  test('saving a profile stores it on the game\'s own rule and marks the game', async ({ page, request }) => {
    await openGames(page)
    await page.getByTestId('games-card-cyberpunk2077').click()
    const sheet = page.getByTestId('game-sheet')
    await expect(sheet).toBeVisible()
    await expect(sheet.getByTestId('game-sheet-title')).toHaveText('cyberpunk2077')

    // TDP is off by default: the mode decides until the user says otherwise.
    await expect(page.getByTestId('game-tdp-toggle')).toHaveAttribute('aria-pressed', 'false')
    await expect(page.getByTestId('game-tdp')).toHaveCount(0)
    await page.getByTestId('game-tdp-toggle').click()
    await expect(page.getByTestId('game-tdp')).toBeVisible()
    await page.getByTestId('game-tdp-slider').fill('21')
    await page.getByTestId('game-tdp-inc').click()
    await expect(page.getByTestId('game-tdp')).toContainText('22')

    await page.getByTestId('game-mode-gaming').click()
    await page.getByTestId('game-cap-60').click()
    await page.getByTestId('game-fan-Aggressive').click()
    await page.getByTestId('game-antilag').click()
    await page.getByTestId('game-save').click()

    await expect(page.getByTestId('toast-success')).toContainText('cyberpunk2077')
    await expect(page.getByTestId('games-state-cyberpunk2077')).toHaveText('Profile')
    await expect(page.getByTestId('games-summary-cyberpunk2077')).toHaveText('22 W · 60 FPS · Aggressive · Anti-Lag')

    const rule = (await rulesNow(request)).find((r) => r.match === 'cyberpunk2077')!
    expect(rule.mode).toBe('gaming')
    expect(rule.enabled).toBe(true)
    expect(rule.overrides).toMatchObject({ stapmW: 22, frameCapFps: 60, fanMode: 'Aggressive', gpu: { antiLag: true, chill: null } })
  })

  test('Chill and Anti-Lag cannot both be on: the driver refuses the pair', async ({ page }) => {
    await openGames(page)
    await page.getByTestId('games-card-cyberpunk2077').click()
    await page.getByTestId('game-antilag').click()
    await expect(page.getByTestId('game-antilag')).toHaveAttribute('aria-pressed', 'true')
    await page.getByTestId('game-chill').click()
    await expect(page.getByTestId('game-chill')).toHaveAttribute('aria-pressed', 'true')
    await expect(page.getByTestId('game-antilag')).toHaveAttribute('aria-pressed', 'false')
  })

  test('removing a profile clears its settings and keeps the rule that picks the mode', async ({ page, request }) => {
    await request.post(`${API}/app-rules`, {
      data: { match: 'hades2', mode: 'gaming', overrides: { stapmW: 18, frameCapFps: 45, fanMode: 'Quiet' } },
    })
    await openGames(page)
    await expect(page.getByTestId('games-state-hades2')).toHaveText('Profile')
    await page.getByTestId('games-card-hades2').click()
    // The editor opens on what is stored, not on defaults.
    await expect(page.getByTestId('game-tdp')).toContainText('18')
    await expect(page.getByTestId('game-cap-45')).toHaveAttribute('aria-checked', 'true')
    await expect(page.getByTestId('game-fan-Quiet')).toHaveAttribute('aria-checked', 'true')

    await page.getByTestId('game-remove').click()
    await expect(page.getByTestId('toast-success')).toContainText('hades2')
    await expect(page.getByTestId('games-state-hades2')).toHaveText('Mode only')
    const rule = (await rulesNow(request)).find((r) => r.match === 'hades2')!
    expect(rule.overrides).toBeNull()
    expect(rule.mode).toBe('gaming')
  })

  test('a new profile is moved above a broader rule that would otherwise claim the game', async ({ page, request }) => {
    await request.post(`${API}/app-rules`, { data: { match: 'cyber', mode: 'windows' } })
    await openGames(page)
    await expect(page.getByTestId('games-state-cyberpunk2077')).toHaveText('Mode only')
    await page.getByTestId('games-card-cyberpunk2077').click()
    await expect(page.getByTestId('game-sheet-inherited')).toContainText('cyber')
    await page.getByTestId('game-fan-Quiet').click()
    await page.getByTestId('game-save').click()
    await expect(page.getByTestId('games-state-cyberpunk2077')).toHaveText('Profile')

    const order = (await rulesNow(request)).map((r) => r.match)
    expect(order.indexOf('cyberpunk2077')).toBeLessThan(order.indexOf('cyber'))
  })

  test('a refused save shows the daemon\'s sentence and keeps the editor open', async ({ page }) => {
    await page.route(`${API}/app-rules`, (route) => route.request().method() === 'POST'
      ? route.fulfill({ status: 400, json: { error: 'Process name is too long (max 120 characters).', code: 'bad_rule' } })
      : route.continue())
    await openGames(page)
    await page.getByTestId('games-card-cyberpunk2077').click()
    await page.getByTestId('game-save').click()
    await expect(page.getByTestId('toast-error')).toContainText('Process name is too long')
    await expect(page.getByTestId('game-sheet')).toBeVisible()
  })

  test('keyboard and pad: the editor takes focus, Escape closes it and returns to the game', async ({ page }) => {
    await openGames(page, 'pad')
    const card = page.getByTestId('games-card-cyberpunk2077')
    await card.focus()
    await page.keyboard.press('Enter')
    await expect(page.getByTestId('game-sheet')).toBeVisible()
    await expect.poll(() => page.evaluate(() => !!document.activeElement?.closest('[data-testid="game-sheet"]'))).toBe(true)
    // Arrow keys walk the sheet (spatial nav), they do not fall back into the page behind it.
    await page.keyboard.press('ArrowDown')
    await expect.poll(() => page.evaluate(() => !!document.activeElement?.closest('[data-testid="game-sheet"]'))).toBe(true)
    await page.keyboard.press('Escape')
    await expect(page.getByTestId('game-sheet')).toHaveCount(0)
    await expect(card).toBeFocused()
  })

  test('pad density: every control in the editor is a 44 px target', async ({ page }) => {
    await openGames(page, 'pad')
    await page.getByTestId('games-card-cyberpunk2077').click()
    await page.getByTestId('game-tdp-toggle').click()
    const small = await page.getByTestId('game-sheet').evaluate((sheet) =>
      Array.from(sheet.querySelectorAll<HTMLElement>('button, input'))
        .filter((el) => el.offsetParent !== null)
        .map((el) => ({ id: el.dataset.testid ?? el.getAttribute('aria-label') ?? el.textContent, h: el.getBoundingClientRect().height }))
        .filter((b) => b.h < 43.5))
    expect(small).toEqual([])
  })
})

test.describe('Games page layout', () => {
  test.afterEach(async ({ request }) => { await removeRules(request, 'cyberpunk2077') })

  test('side sheet beside the list at 1280x800', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 })
    await openGames(page)
    await page.getByTestId('games-card-cyberpunk2077').click()
    const list = (await page.getByTestId('games-list').boundingBox())!
    const sheet = (await page.getByTestId('game-sheet').boundingBox())!
    expect(sheet.x).toBeGreaterThanOrEqual(list.x + list.width - 1)
    expect(sheet.x + sheet.width).toBeLessThanOrEqual(1281)

    // Tallest state, large text: Save must still be on screen without scrolling the sheet. The first
    // baseline had it below the fold of a panel that scrolls on its own.
    await page.evaluate(() => { document.documentElement.dataset.textscale = 'large' })
    await page.getByTestId('game-tdp-toggle').click()
    const save = (await page.getByTestId('game-save').boundingBox())!
    expect(save.y + save.height).toBeLessThanOrEqual(800)
    expect(save.y).toBeGreaterThanOrEqual(0)
  })

  test('full width, stacked above the list, at 720x600', async ({ page }) => {
    await page.setViewportSize({ width: 720, height: 600 })
    await openGames(page)
    await page.getByTestId('games-card-cyberpunk2077').click()
    const list = (await page.getByTestId('games-list').boundingBox())!
    const sheet = (await page.getByTestId('game-sheet').boundingBox())!
    expect(Math.abs(sheet.width - list.width)).toBeLessThanOrEqual(2)
    expect(sheet.y + sheet.height).toBeLessThanOrEqual(list.y + 1)

    // Opening scrolls the editor into view — below the sticky topbar, not under it. The first
    // baseline caught its title and close button hidden behind the bar.
    const bar = (await page.locator('.topbar').boundingBox())!
    const title = (await page.getByTestId('game-sheet-title').boundingBox())!
    expect(title.y).toBeGreaterThanOrEqual(bar.y + bar.height - 1)
  })
})
