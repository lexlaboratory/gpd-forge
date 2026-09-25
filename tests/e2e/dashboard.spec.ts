// GPD Forge — dashboard smoke E2E. GPL-3.0-or-later.
import { test, expect } from './fixtures'
import { DashboardPage } from './pages/DashboardPage'

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

  test('auto mode is on by default and a manual pick turns it off', async ({ page }) => {
    const auto = page.getByTestId('auto-toggle')
    await expect(auto).toHaveAttribute('aria-pressed', 'true')
    await expect(page.getByTestId('modes-hint')).toContainText('Auto')

    await dash.pickMode('gaming')
    await expect(auto).toHaveAttribute('aria-pressed', 'false')
    await expect(page.getByTestId('modes-hint')).toContainText('Manual')
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
