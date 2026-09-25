// GPD Forge — adaptive density E2E. GPL-3.0-or-later.
//
// The audit found every interactive target under the 44px accessibility floor: steppers at 34px,
// chips at 31px, the switch 22px tall — on a touchscreen handheld. Nothing enforced it, so nothing
// stopped it happening again.
import { test, expect, type Page } from './fixtures'

const setDensity = (page: Page, density: 'pad' | 'mouse') =>
  page.evaluate((d) => { document.documentElement.dataset.density = d }, density)

/** Every visible, enabled control on the page — the same set a d-pad or a thumb can reach. */
async function targets(page: Page) {
  return page.locator('button:not([disabled]), a[href], input[type="range"]').all()
}

test.describe('Adaptive density', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('forge-setup-done', '1'))
    await page.goto('/', { waitUntil: 'domcontentloaded' })
    await expect(page.getByTestId('device')).toBeVisible({ timeout: 15_000 })
  })

  test('pad density meets the 44px touch floor on every control', async ({ page }) => {
    await setDensity(page, 'pad')
    await page.waitForTimeout(200)

    const small: string[] = []
    for (const el of await targets(page)) {
      if (!(await el.isVisible())) continue
      const box = await el.boundingBox()
      if (!box) continue
      // Height is the binding dimension: a wide, short control is still hard to hit with a thumb.
      if (box.height < 44) {
        small.push(`${(await el.getAttribute('data-testid')) ?? (await el.innerText()).slice(0, 24)} = ${Math.round(box.height)}px`)
      }
    }
    expect(small, `controls below the 44px touch floor:\n${small.join('\n')}`).toEqual([])
  })

  test('mouse density is denser than pad density', async ({ page }) => {
    const navItem = page.getByTestId('nav-power')

    await setDensity(page, 'pad')
    await page.waitForTimeout(150)
    const padBox = await navItem.boundingBox()

    await setDensity(page, 'mouse')
    await page.waitForTimeout(150)
    const mouseBox = await navItem.boundingBox()

    expect(padBox!.height).toBeGreaterThan(mouseBox!.height)
  })

  // Found 2026-09-25 while adding the Games page: with a gamepad connected the app is in pad density,
  // and a mouse press switched it to mouse density on pointerdown — shrinking every rail entry from 45
  // to 37 px BEFORE the release. The release landed on a different entry and the browser dropped the
  // click: the first touchpad click after using the pad did nothing. The switch now waits for the click.
  test('the click that switches pad density to mouse still lands', async ({ page }) => {
    await page.addInitScript(() => {
      localStorage.removeItem('forge-density')
      const buttons = Array.from({ length: 17 }, () => ({ pressed: false, touched: false, value: 0 }))
      const pad = { id: 'fake', index: 0, connected: true, mapping: 'standard', axes: [0, 0, 0, 0], buttons, timestamp: 0 }
      Object.defineProperty(navigator, 'getGamepads', { value: () => [pad, null, null, null] })
    })
    await page.goto('/')
    await expect(page.locator('html')).toHaveAttribute('data-density', 'pad')

    await page.getByTestId('nav-profiles').click()
    await expect(page.getByTestId('page-profiles')).toBeVisible()
    await expect(page.locator('html')).toHaveAttribute('data-density', 'mouse')
  })

  test('density is a token change, not a second layout', async ({ page }) => {
    // The same controls must exist in both densities — if a control disappears, we have grown two
    // layouts to maintain, which is exactly what this design avoids.
    await setDensity(page, 'pad')
    await page.waitForTimeout(150)
    const padCount = await page.locator('button:visible').count()

    await setDensity(page, 'mouse')
    await page.waitForTimeout(150)
    expect(await page.locator('button:visible').count()).toBe(padCount)
  })
})
