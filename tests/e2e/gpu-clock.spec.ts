// GPD Forge — the fixed GPU clock on the Power page, against the mock. GPL-3.0-or-later.
//
// Measured on the device 2026-10-13: the 890M offers no ADLX clock tuning; PSMU 0x89 fixes the clock and
// only a restart hands it back. The card has to say that plainly — "fixed until restart", never "auto".
import { test, expect } from './fixtures'

const API = 'http://127.0.0.1:8799'

test.describe('GPU clock', () => {
  test.beforeEach(async ({ page, request }) => {
    await request.post(`${API}/gpu/clock/_test-reset`)
    await page.addInitScript(() => localStorage.setItem('forge-setup-done', '1'))
    await page.goto('/#power')
  })
  test.afterEach(async ({ request }) => {
    await request.post(`${API}/gpu/clock/_test-reset`)
  })

  test('reads automatic until a clock is fixed', async ({ page }) => {
    await expect(page.getByTestId('gpu-clock-state')).toContainText('Automatic')
    await expect(page.getByTestId('gpu-clock')).toContainText('restart')
  })

  test('fixing a clock reaches the daemon and says it lasts until a restart', async ({ page, request }) => {
    await page.getByTestId('gpu-clock-mhz').fill('1200')
    await page.getByTestId('gpu-clock-apply').click()

    await expect(page.getByTestId('gpu-clock-state')).toContainText('Fixed at 1200 MHz')
    await expect(page.getByTestId('gpu-clock-state')).toContainText('restart')
    expect((await (await request.get(`${API}/gpu/clock`)).json()).forcedMhz).toBe(1200)
  })

  test('a clock the daemon refused shows its reason and claims nothing', async ({ page }) => {
    await page.route(/\/gpu\/clock$/, (route) => route.request().method() === 'POST'
      ? route.fulfill({ status: 503, json: { error: { code: 'smu_failed', message: 'The SMU did not take the GPU clock: simulated' } } })
      : route.continue())

    await page.getByTestId('gpu-clock-mhz').fill('1500')
    await page.getByTestId('gpu-clock-apply').click()

    await expect(page.getByTestId('gpu-clock-error')).toContainText('simulated')
    await expect(page.getByTestId('gpu-clock-state')).toContainText('Automatic')
  })

  test('without a backend that can do it the control is disabled and says why', async ({ page }) => {
    await page.route(/\/gpu\/clock$/, (route) => route.request().method() === 'GET'
      ? route.fulfill({ json: { supported: false, forcedMhz: null, sinceUtc: null, minMhz: 400, maxMhz: 2900, resetsOnRestart: true, note: 'n' } })
      : route.continue())
    await page.reload()

    await expect(page.getByTestId('gpu-clock-apply')).toBeDisabled()
    await expect(page.getByTestId('gpu-clock-unsupported')).toBeVisible()
  })
})
