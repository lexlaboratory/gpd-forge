import { test, expect } from './fixtures'

const gotoSection = async (page: import('./fixtures').Page, section: string) => {
  await page.addInitScript(() => localStorage.setItem('forge-setup-done', '1'))
  await page.goto('/', { waitUntil: 'domcontentloaded' })
  await page.getByTestId(`nav-${section}`).click()
}

test.describe('thermal controls', () => {
  test('fan mode stays on the confirmed value when a write fails', async ({ page }) => {
    await page.route('**/fan', (route) => {
      if (route.request().method() === 'POST') return route.fulfill({ status: 503, json: { error: 'Fan write refused' } })
      return route.fulfill({ json: { mode: 'Auto', manualDuty: 128, controllable: true } })
    })
    await gotoSection(page, 'fan')
    await expect(page.getByTestId('fan-auto')).toHaveAttribute('aria-checked', 'true')

    await page.getByTestId('fan-balanced').click()

    await expect(page.getByRole('alert')).toContainText('Fan write refused')
    await expect(page.getByTestId('fan-auto')).toHaveAttribute('aria-checked', 'true')
    await expect(page.getByTestId('fan-balanced')).toHaveAttribute('aria-checked', 'false')
    await expect(page.getByTestId('fan-auto')).toBeEnabled()
  })

  test('fan status distinguishes requested duty, readback, and verification', async ({ page }) => {
    await page.route('**/fan', (route) => route.request().method() === 'GET'
      ? route.fulfill({ json: { mode: 'Manual', manualDuty: 128, controllable: true,
          status: { requestedDuty: 128, observedDuty: 102, verified: false, error: null, atUtc: '2026-10-06T12:00:00Z', mode: 'Manual' } } })
      : route.continue())
    await gotoSection(page, 'fan')

    await expect(page.getByTestId('fan-requested-duty')).toContainText('50%')
    await expect(page.getByTestId('fan-readback-duty')).toContainText('40%')
    await expect(page.getByTestId('fan-verification')).toContainText('Not verified')
    await expect(page.getByTestId('fan-manual-duty')).toHaveAttribute('min', '15.7')
    await expect(page.getByTestId('fan-manual-duty')).toHaveAttribute('max', '100')
  })

  test('controllable fan with missing verification remains unknown', async ({ page }) => {
    await page.route('**/fan', (route) => route.request().method() === 'GET'
      ? route.fulfill({ json: { mode: 'Auto', manualDuty: 128, controllable: true } })
      : route.continue())
    await gotoSection(page, 'fan')

    await expect(page.getByTestId('fan-verification')).toContainText('Unknown')
  })

  test('manual duty is shown as a percentage and sends the minimum effective raw value', async ({ page }) => {
    await page.route('**/fan', (route) => {
      if (route.request().method() === 'GET') {
        return route.fulfill({ json: { mode: 'Manual', manualDuty: 128, controllable: true,
          status: { requestedDuty: 128, observedDuty: 128, verified: true, error: null, atUtc: null, mode: 'Manual' } } })
      }
      const body = route.request().postDataJSON() as { manualDuty: number }
      return route.fulfill({ json: { mode: 'Manual', manualDuty: body.manualDuty, controllable: true,
        status: { requestedDuty: body.manualDuty, observedDuty: body.manualDuty, verified: true, error: null, atUtc: null, mode: 'Manual' } } })
    })
    await gotoSection(page, 'fan')
    const duty = page.getByTestId('fan-manual-duty')
    await expect(duty).toBeVisible()
    await expect(duty).toHaveAttribute('max', '100')
    await expect(duty).toHaveAttribute('min', '15.7')

    const write = page.waitForRequest((request) => request.url().endsWith('/fan') && request.method() === 'POST')
    await duty.focus()
    await duty.press('Home')

    expect((await write).postDataJSON()).toMatchObject({ manualDuty: 40 })
  })

  test('fan keeps an old verified snapshot pending until the requested duty is read back', async ({ page }) => {
    let writeSeen = false
    let postWriteReads = 0
    const status = (requestedDuty: number, observedDuty: number, verified: boolean | null) => ({
      requestedDuty, observedDuty, verified, error: null, atUtc: '2026-10-06T12:00:00Z', mode: 'Manual',
    })
    await page.route('**/fan', (route) => {
      if (route.request().method() === 'POST') {
        writeSeen = true
        return route.fulfill({ json: { mode: 'Manual', manualDuty: 173, controllable: true,
          status: status(128, 128, true) } })
      }
      if (!writeSeen) return route.fulfill({ json: { mode: 'Manual', manualDuty: 128, controllable: true,
        status: status(128, 128, true) } })
      postWriteReads++
      return route.fulfill({ json: { mode: 'Manual', manualDuty: 173, controllable: true,
        status: postWriteReads === 1 ? status(128, 128, true) : status(173, 173, true) } })
    })
    await gotoSection(page, 'fan')
    const duty = page.getByTestId('fan-manual-duty')
    await expect(page.getByTestId('fan-verification')).toHaveText('Verified')

    await duty.fill('68')
    await duty.dispatchEvent('pointerup')

    await expect(page.getByTestId('fan-verification')).toHaveText('Awaiting readback')
    await expect(page.getByTestId('fan-requested-duty')).toContainText('68%')
    await expect(page.getByTestId('fan-verification')).toHaveText('Verified', { timeout: 5000 })
    await expect(page.getByTestId('fan-readback-duty')).toContainText('68%')
  })

  test('preset save failure is visible and does not claim the preset was saved', async ({ page }) => {
    await page.route('**/profiles/**', (route) => route.request().method() === 'POST'
      ? route.fulfill({ status: 503, json: { error: 'Preset save refused' } })
      : route.continue())
    await gotoSection(page, 'power')
    await expect(page.getByTestId('preset-gaming')).toBeVisible()
    await page.getByTestId('preset-apply').click()

    await expect(page.getByRole('alert')).toContainText('Preset save refused')
    await expect(page.getByTestId('preset-saved')).toHaveCount(0)
  })

  test('Auto-FPS failure is visible and keeps the confirmed toggle state', async ({ page }) => {
    await page.route('**/auto-fps', (route) => {
      if (route.request().method() === 'POST') return route.fulfill({ status: 503, json: { error: 'Auto-FPS write refused' } })
      return route.fulfill({ json: { enabled: false, targetFps: 60 } })
    })
    await gotoSection(page, 'power')
    const toggle = page.getByTestId('autofps-toggle')
    await expect(toggle).toHaveAttribute('aria-pressed', 'false')

    await toggle.click()

    await expect(page.getByRole('alert')).toContainText('Auto-FPS write refused')
    await expect(toggle).toHaveAttribute('aria-pressed', 'false')
    await expect(toggle).toBeEnabled()
  })

  test('TDP status separates desired limit, readback, measured watts, owner, and last attempt', async ({ page }) => {
    await page.route('**/tdp', (route) => route.request().method() === 'GET'
      ? route.fulfill({ json: { stapmW: 19, owner: 'guardian', verified: false, backend: 'test', observedStapmW: 18,
          observedPptW: 20, attempts: 2, atUtc: '2026-10-06T12:00:00Z', note: null, manualStapmW: 22,
          intentStapmW: 21, error: 'Readback differs', verificationStatus: 'mismatch' } })
      : route.continue())
    await page.route('**/telemetry', async (route) => {
      const response = await route.fetch()
      return route.fulfill({ response, json: { ...(await response.json()), packageW: 24 } })
    })
    await gotoSection(page, 'power')

    const status = page.getByTestId('tdp-status')
    await expect(status).toContainText('Desired: 21 W')
    await expect(status).toContainText('Read back: 18 W')
    await expect(status).toContainText('Measured: 24 W')
    await expect(status).toContainText('Not verified')
    await expect(status).toContainText('Owner: guardian')
    await expect(status).toContainText('2 attempts')
    await expect(page.getByTestId('tdp-error')).toContainText('Readback differs')
  })

  test('TDP desired value falls back to the active mode preset when the daemon omits intent', async ({ page }) => {
    await page.route('**/mode', (route) => route.request().method() === 'GET'
      ? route.fulfill({ json: { active: 'windows' } }) : route.continue())
    await page.route('**/tdp', (route) => route.request().method() === 'GET'
      ? route.fulfill({ json: { stapmW: 13, owner: 'guardian', verified: false, backend: null, observedStapmW: 12,
          observedPptW: null, attempts: 1, atUtc: null, note: null, manualStapmW: null } })
      : route.continue())
    await gotoSection(page, 'power')

    await expect(page.getByTestId('tdp-status')).toContainText('Desired: 15 W')
    await expect(page.getByTestId('tdp-status')).toContainText('Requested now: 13 W')
    await expect(page.getByTestId('tdp-status')).toContainText('Read back: 12 W')
  })

  test('TDP status separates desired target from the current request and readback', async ({ page }) => {
    await page.route('**/tdp', (route) => route.request().method() === 'GET'
      ? route.fulfill({ json: { stapmW: 12, owner: 'thermal', verified: true, backend: 'test', observedStapmW: 12,
          observedPptW: 14, attempts: 1, atUtc: '2026-10-06T12:00:00Z', note: null, manualStapmW: null,
          intentStapmW: 15, error: null, verificationStatus: 'verified' } })
      : route.continue())
    await gotoSection(page, 'power')

    const status = page.getByTestId('tdp-status')
    await expect(status).toContainText('Desired: 15 W')
    await expect(status).toContainText('Requested now: 12 W')
    await expect(status).toContainText('Read back: 12 W')
    await expect(status).toContainText('Verification (last request): Verified')
    await expect(status).toContainText('Owner: thermal')
  })

  test('a stub backend cannot claim hardware verification from an echoed value', async ({ page }) => {
    await page.route('**/tdp', (route) => route.request().method() === 'GET'
      ? route.fulfill({ json: { stapmW: 15, owner: 'mode', verified: true, backend: 'stub', observedStapmW: 15,
          observedPptW: 15, attempts: 1, atUtc: '2026-10-06T12:00:00Z', note: null, manualStapmW: null,
          intentStapmW: 15, error: null, verificationStatus: 'verified' } })
      : route.continue())
    await gotoSection(page, 'power')

    const status = page.getByTestId('tdp-status')
    await expect(status).toContainText('Requested now: 15 W')
    await expect(status).toContainText('Read back: 15 W')
    await expect(status).toContainText('Verification (last request): Not verified')
    await expect(status).not.toContainText('Verification (last request): Verified')
  })

  test('trigger diagnostic explains missing controllers', async ({ page }) => {
    await page.addInitScript(() => {
      Object.defineProperty(navigator, 'getGamepads', { configurable: true, value: () => [] })
    })
    await gotoSection(page, 'hardware')

    await expect(page.getByTestId('trigger-diagnostics')).toContainText('No controller detected')
    await expect(page.getByTestId('trigger-left')).toContainText('--')
    await expect(page.getByTestId('trigger-right')).toContainText('--')
  })

  test('thermal pages fit narrow and desktop viewports without horizontal overflow', async ({ page }) => {
    for (const width of [1280, 380]) {
      await page.setViewportSize({ width, height: 800 })
      await gotoSection(page, 'fan')
      await expect(page.getByRole('radiogroup', { name: 'Fan mode' })).toBeVisible()
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy()
      await page.screenshot({ path: `test-results/fan-${width}x800.png`, fullPage: true })
      await gotoSection(page, 'power')
      await expect(page.getByTestId('tdp-status')).toBeVisible()
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy()
      await page.screenshot({ path: `test-results/power-${width}x800.png`, fullPage: true })
    }
  })
})
