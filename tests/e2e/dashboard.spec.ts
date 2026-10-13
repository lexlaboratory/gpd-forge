// GPD Forge — dashboard smoke E2E. GPL-3.0-or-later.
import { test, expect } from './fixtures'
import { DashboardPage } from './pages/DashboardPage'

const API = 'http://127.0.0.1:8799'

test.describe('Dashboard', () => {
  let dash: DashboardPage

  test.beforeEach(async ({ page }) => {
    dash = new DashboardPage(page)
    await dash.goto()
  })

  test('renders the shell, telemetry tiles and all five modes', async ({ page }) => {
    await expect(page).toHaveTitle(/GPD Forge/)
    await expect(dash.device).toContainText('GPD Win 4')

    // exactly the five telemetry tiles
    await expect(dash.stats).toHaveCount(5)

    // Every tile must carry a real reading. Asserting on the unit alone is not enough: when the
    // client cannot reach the daemon each tile renders the placeholder '--' next to its unit, so
    // `toContainText('°C')` passed happily while the dashboard showed nothing at all.
    for (const id of ['stat-cpu', 'stat-pkg', 'stat-fan', 'stat-fps', 'stat-batt']) {
      await expect(page.getByTestId(id), `${id} shows a number`).toContainText(/\d/)
      await expect(page.getByTestId(id), `${id} is not the placeholder`).not.toContainText('--')
    }
    await expect(page.getByTestId('stat-cpu')).toContainText('°C')

    // and the connection chip must say so, rather than silently sitting on stale nulls
    await expect(page.getByTestId('conn')).toContainText('Live')

    for (const id of ['gaming', 'ai', 'windows', 'battery', 'standby']) {
      await expect(dash.mode(id)).toBeVisible()
    }
  })

  test('the TDP stepper moves the value one watt at a time, within bounds', async ({ page, request }) => {
    // The d-pad path: a gamepad can press these, it cannot drag the slider beside them.
    // It starts from the value IN FORCE (GET /tdp) — here the `windows` preset — not from the 20 W
    // placeholder this test used to pin, which was the bug: a remembered 12 W opened as 20 W.
    await request.post('http://127.0.0.1:8799/mode', { data: { name: 'windows' } })
    await page.reload()
    await expect(dash.tdpValue).toHaveText('15 W')
    await page.getByTestId('tdp-inc').click()
    await expect(dash.tdpValue).toHaveText('16 W')
    await page.getByTestId('tdp-dec').click()
    await page.getByTestId('tdp-dec').click()
    await expect(dash.tdpValue).toHaveText('14 W')
    await dash.tdpSlider.fill('5')
    await expect(page.getByTestId('tdp-dec')).toBeDisabled()
    // Leave no manual override behind for the next spec: picking a mode ends it.
    await request.post('http://127.0.0.1:8799/mode', { data: { name: 'windows' } })
  })

  test('selecting a mode marks it active', async () => {
    await dash.pickMode('ai')
    await expect(dash.mode('ai')).toHaveAttribute('aria-selected', 'true')
    await expect(dash.mode('windows')).toHaveAttribute('aria-selected', 'false')
    await expect(dash.activeMode).toContainText('Agents / AI')
  })

  test('a mode read that was in flight when you picked cannot undo the pick', async ({ page }) => {
    // Found by the full suite flaking ai.spec: picking a mode turns auto off, which re-runs the
    // shell's GET /mode, and that read raced the POST. When the GET was served first it answered
    // the OLD mode and flipped the card back. Holding the POST makes the race certain.
    let posted = false
    await page.route(/\/mode$/, async (route) => {
      if (route.request().method() !== 'POST') return route.continue()
      await new Promise((r) => setTimeout(r, 600))
      posted = true
      return route.continue()
    })
    await dash.pickMode('ai')
    await expect.poll(() => posted).toBe(true)
    await page.waitForTimeout(300) // let the stale read land, if the shell still takes it
    await expect(dash.mode('ai')).toHaveAttribute('aria-selected', 'true')
    await expect(dash.mode('windows')).toHaveAttribute('aria-selected', 'false')
  })

  test('auto mode is on by default and a manual pick turns it off', async ({ page }) => {
    const auto = page.getByTestId('auto-toggle')
    await expect(auto).toHaveAttribute('aria-pressed', 'true')
    await expect(page.getByTestId('modes-hint')).toContainText('Auto')

    await dash.pickMode('gaming')
    await expect(auto).toHaveAttribute('aria-pressed', 'false')
    await expect(page.getByTestId('modes-hint')).toContainText('Manual')
  })

  // 2026-10-10 on the device: Auto lived only in the browser. The user picked Windows (12 W), the
  // screen said "Manual: Windows" and stopped asking, and auto-profiles moved the handheld to gaming at
  // 25 W behind it. Auto is the daemon's now, and the screen keeps reading the mode either way.
  test('the Auto switch reaches the daemon', async ({ page, request }) => {
    const auto = page.getByTestId('auto-toggle')
    await auto.click()
    await expect(auto).toHaveAttribute('aria-pressed', 'false')
    await expect.poll(async () => (await (await request.get(`${API}/mode`)).json()).auto).toBe(false)

    await auto.click()
    await expect(auto).toHaveAttribute('aria-pressed', 'true')
    await expect.poll(async () => (await (await request.get(`${API}/mode`)).json()).auto).toBe(true)
  })

  test('picking a mode turns Auto off in the daemon too', async ({ request }) => {
    await dash.pickMode('windows')
    await expect.poll(async () => (await (await request.get(`${API}/mode`)).json()).auto).toBe(false)
  })

  test('with Auto off the screen still follows a mode changed elsewhere', async ({ page, request }) => {
    await dash.pickMode('windows')
    await expect(dash.mode('windows')).toHaveAttribute('aria-selected', 'true')

    // The overlay, the MCP server or an agent switches it; the dashboard must not keep showing Windows.
    await request.post(`${API}/mode`, { data: { name: 'battery' } })
    try {
      await expect(dash.mode('battery')).toHaveAttribute('aria-selected', 'true', { timeout: 5000 })
      await expect(page.getByTestId('active-mode')).toContainText('Manual')
    } finally {
      await request.post(`${API}/mode`, { data: { name: 'windows' } })   // the mock is shared
    }
  })

  test('a pick the daemon refused shows what the daemon holds, not the pick', async ({ page, request }) => {
    await request.post(`${API}/mode`, { data: { name: 'windows' } })
    await request.post(`${API}/mode/auto`, { data: { enabled: true } })
    // The 2 s poll is frozen, so only an immediate re-read can put the screen right.
    await page.clock.install()
    await page.reload()
    await expect(dash.mode('windows')).toHaveAttribute('aria-selected', 'true')
    await page.clock.pauseAt(Date.now() + 500)
    await page.route(/\/mode$/, (route) => route.request().method() === 'POST'
      ? route.fulfill({ status: 500, json: { error: 'simulated' } })
      : route.continue())

    await dash.mode('gaming').click()

    await expect(dash.mode('windows')).toHaveAttribute('aria-selected', 'true')
    await expect(page.getByTestId('auto-toggle')).toHaveAttribute('aria-pressed', 'true')
  })

  test('an Auto switch the daemon refused is put back', async ({ page }) => {
    await page.clock.install()
    await page.reload()
    await expect(page.getByTestId('auto-toggle')).toHaveAttribute('aria-pressed', 'true')
    await page.clock.pauseAt(Date.now() + 500)
    await page.route(/\/mode\/auto$/, (route) => route.fulfill({ status: 500, json: { error: 'simulated' } }))

    await page.getByTestId('auto-toggle').click()

    await expect(page.getByTestId('auto-toggle')).toHaveAttribute('aria-pressed', 'true')
  })

  test('a daemon from before Auto was its own still reads as Auto', async ({ page }) => {
    // Up to 0.4.0 GET /mode answered { active } alone, and auto-profiles always ran: that is Auto.
    await page.route(/\/mode$/, (route) => route.request().method() === 'GET'
      ? route.fulfill({ json: { active: 'windows' } })
      : route.continue())
    await page.reload()
    await expect(page.getByTestId('auto-toggle')).toHaveAttribute('aria-pressed', 'true')
    await expect(page.getByTestId('active-mode')).toContainText('Auto')
  })

  test('a mode an app rule chose says which rule', async ({ page, request }) => {
    // The mock's foreground is steam, and the shipped steam rule asks for gaming.
    const switched = await request.post(`${API}/mode/_test-auto-switch`, { data: { name: 'gaming' } })
    expect(switched.ok()).toBe(true)
    try {
      await expect(dash.mode('gaming')).toHaveAttribute('aria-selected', 'true', { timeout: 5000 })
      await expect(page.getByTestId('modes-hint')).toContainText('“steam” rule', { timeout: 5000 })
      await expect(page.getByTestId('active-mode')).toContainText('Auto')
    } finally {
      // The mock is shared: leave it in the user's windows, not in a rule's gaming (Auto comes back on
      // before the next test through the modeAutoOn fixture).
      await request.post(`${API}/mode`, { data: { name: 'windows' } })
    }
  })

  test('TDP slider updates its readout', async () => {
    await dash.tdpSlider.fill('28')
    await expect(dash.tdpValue).toHaveText('28 W')
  })

  test('no console errors on load', async ({ page }) => {
    const errors: string[] = []
    page.on('console', (m) => m.type() === 'error' && errors.push(m.text()))
    await page.reload()
    await expect(dash.device).toBeVisible()
    expect(errors, errors.join('\n')).toHaveLength(0)
  })
})
