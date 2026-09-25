// GPD Forge — LB/RB switch pages from a gamepad. GPL-3.0-or-later.
//
// On the handheld the sidebar is 12 entries tall (11 before the Games page); reaching Alerts from the Dashboard took ten
// D-pad presses and a confirm. The shoulder buttons now step through the sections, wrapping.
import { test, expect, type Page } from './fixtures'

/** A fake standard-mapping gamepad whose buttons the test can hold and release. */
async function installFakePad(page: Page) {
  await page.addInitScript(() => {
    localStorage.setItem('forge-setup-done', '1')
    const buttons = Array.from({ length: 17 }, () => ({ pressed: false, touched: false, value: 0 }))
    const pad = { id: 'fake', index: 0, connected: true, mapping: 'standard', axes: [0, 0, 0, 0], buttons, timestamp: 0 }
    ;(window as unknown as { __pad: typeof pad }).__pad = pad
    Object.defineProperty(navigator, 'getGamepads', { value: () => [pad, null, null, null] })
  })
}

async function tap(page: Page, button: number) {
  await page.evaluate((i) => { (window as any).__pad.buttons[i].pressed = true }, button)
  await page.waitForTimeout(80)
  await page.evaluate((i) => { (window as any).__pad.buttons[i].pressed = false }, button)
  await page.waitForTimeout(80)
}

const LB = 4
const RB = 5

test.describe('Shoulder-button page switching', () => {
  test.beforeEach(async ({ page }) => {
    await installFakePad(page)
    await page.goto('/')
    await expect(page.getByTestId('page-dashboard')).toBeVisible()
  })

  test('RB goes to the next section, LB back', async ({ page }) => {
    await tap(page, RB)
    await expect(page.getByTestId('page-power')).toBeVisible()
    await tap(page, RB)
    await expect(page.getByTestId('page-fan')).toBeVisible()
    await tap(page, LB)
    await expect(page.getByTestId('page-power')).toBeVisible()
  })

  // F1 (2026-09-25): Games sits between Profiles and Monitor, and the shoulders walk the rail's order.
  test('Games is one RB past Profiles and one LB before Monitor', async ({ page }) => {
    await page.getByTestId('nav-profiles').click()
    await expect(page.getByTestId('page-profiles')).toBeVisible()
    await tap(page, RB)
    await expect(page.getByTestId('page-games')).toBeVisible()
    await tap(page, RB)
    await expect(page.getByTestId('page-monitor')).toBeVisible()
    await tap(page, LB)
    await expect(page.getByTestId('page-games')).toBeVisible()
  })

  test('LB on the first section wraps to the last', async ({ page }) => {
    await tap(page, LB)
    await expect(page.getByTestId('page-alerts')).toBeVisible()
  })
})
