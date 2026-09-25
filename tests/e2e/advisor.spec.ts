// GPD Forge — Forge Advisor: the Suggestions card and the overlay line against the mock (plan F3).
//
// The mock learns no thermal ceiling, so each test seeds one (the mock's live game is cyberpunk2077),
// and every test resets the advisor and removes the rule Apply created: the mock is shared by every
// spec, and a leftover suggestion would appear in the visual baselines.
import { test, expect, type APIRequestContext } from './fixtures'

const API = 'http://127.0.0.1:8799'

async function seed(request: APIRequestContext, ceilings: Record<string, number>) {
  await request.post(`${API}/advisor/_test-seed`, { data: { reset: true, ceilings } })
}
async function ruleFor(request: APIRequestContext, match: string) {
  const rules = (await (await request.get(`${API}/app-rules`)).json()).rules as { id: string; match: string; overrides: { stapmW: number | null } | null }[]
  return rules.find((r) => r.match === match) ?? null
}

test.describe('Forge Advisor', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('forge-setup-done', '1'))
  })
  test.afterEach(async ({ request }) => {
    await request.post(`${API}/advisor/_test-seed`, { data: { reset: true } })
    for (const game of ['cyberpunk2077', 'hades2']) {
      const r = await ruleFor(request, game)
      if (r) await request.delete(`${API}/app-rules/${r.id}`)
    }
  })

  test('nothing to suggest shows no card', async ({ page, request }) => {
    await seed(request, {})
    await page.goto('/')
    await expect(page.getByTestId('stat-cpu')).toBeVisible()
    await expect(page.getByTestId('advisor')).toHaveCount(0)
  })

  test('Apply on the Dashboard writes the suggestion into the game profile and records it', async ({ page, request }) => {
    await seed(request, { cyberpunk2077: 22.4 })
    await page.goto('/')
    const card = page.getByTestId('advisor')
    await expect(card.getByTestId('advisor-game')).toContainText('cyberpunk2077')
    await expect(card.getByTestId('advisor-item-stapm_ceiling')).toContainText('Hold 22 W')

    await card.getByTestId('advisor-apply-stapm_ceiling').click()

    await expect(card.getByTestId('advisor-item-stapm_ceiling')).toHaveCount(0)
    await expect(card.getByTestId('advisor-applied')).toContainText('22 W')
    expect((await ruleFor(request, 'cyberpunk2077'))?.overrides?.stapmW).toBe(22)
  })

  test('Dismiss on the Games page hides the suggestion and changes nothing', async ({ page, request }) => {
    await seed(request, { hades2: 18 })
    await page.goto('/#games')
    await page.getByTestId('games-card-hades2').click()
    const card = page.getByTestId('advisor')
    await expect(card.getByTestId('advisor-game')).toContainText('hades2')
    await card.getByTestId('advisor-dismiss-stapm_ceiling').click()

    await expect(page.getByTestId('advisor')).toHaveCount(0)
    const after = await (await request.get(`${API}/advisor/suggestions?game=hades2`)).json()
    expect(after.suggestions).toEqual([])
    expect(await ruleFor(request, 'hades2')).toBeNull()
  })

  test('the overlay offers the suggestion on one line with Apply', async ({ page, request }) => {
    await seed(request, { cyberpunk2077: 22.4 })
    await page.goto('/overlay.html')
    const line = page.getByTestId('qam-advice')
    await expect(line).toContainText('Hold 22 W')
    await line.getByTestId('qam-advice-apply').click()
    await expect(page.getByTestId('qam-advice')).toHaveCount(0)
    expect((await ruleFor(request, 'cyberpunk2077'))?.overrides?.stapmW).toBe(22)
  })

  test('a stale or malformed id is refused without writing anything', async ({ request }) => {
    await seed(request, {})
    const stale = await request.post(`${API}/advisor/apply`, { data: { id: 'stapm_ceiling:cyberpunk2077:22' } })
    expect(stale.status()).toBe(409)
    expect((await stale.json()).code).toBe('stale_suggestion')
    const bad = await request.post(`${API}/advisor/apply`, { data: { id: 'nope' } })
    expect(bad.status()).toBe(400)
    expect(await ruleFor(request, 'cyberpunk2077')).toBeNull()
  })
})
