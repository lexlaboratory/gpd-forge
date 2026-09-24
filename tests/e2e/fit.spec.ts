// GPD Forge — everything fits the small screens it runs on. GPL-3.0-or-later.
//
// The app runs on a 6" 1280x800 handheld, in windows as small as 720x600, and the Quick Access
// Menu opens in a 380px-wide window. Visual baselines catch a changed pixel but not a control
// pushed off the edge, and the previous overlay did exactly that with large text: a fixed 23rem
// panel became 402px in a 380px window. These checks are geometric, so they hold across re-skins.
import { test, expect, type Page } from '@playwright/test'

const PAGES = ['dashboard', 'power', 'fan', 'hardware', 'display', 'profiles', 'monitor',
  'sessions', 'system', 'settings', 'alerts'] as const

async function prime(page: Page, textscale: 'normal' | 'large', density: 'pad' | 'mouse' = 'pad') {
  await page.addInitScript(([t, d]) => {
    localStorage.setItem('forge-setup-done', '1')
    localStorage.setItem('forge-textscale', t)
    localStorage.setItem('forge-density', d)
  }, [textscale, density])
}

/** Visible elements (with a testid) whose box leaves the viewport horizontally. */
async function offscreen(page: Page) {
  return page.evaluate(() => {
    const vw = document.documentElement.clientWidth
    return Array.from(document.querySelectorAll<HTMLElement>('[data-testid]'))
      .filter((el) => {
        const r = el.getBoundingClientRect()
        return r.width > 0 && r.height > 0 && (r.left < -1 || r.right > vw + 1)
      })
      .map((el) => el.dataset.testid)
  })
}

const noHorizontalScroll = (page: Page) =>
  page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1)

for (const vp of [{ width: 1280, height: 800 }, { width: 720, height: 600 }]) {
  for (const scale of ['normal', 'large'] as const) {
    test.describe(`main window ${vp.width}x${vp.height}, ${scale} text`, () => {
      test.use({ viewport: vp })

      test('no page scrolls sideways or pushes a control off screen', async ({ page }) => {
        await prime(page, scale)
        await page.goto('/')
        await expect(page.getByTestId('page-dashboard')).toBeVisible()
        for (const id of PAGES) {
          await page.getByTestId(`nav-${id}`).click()
          await expect(page.getByTestId(`page-${id}`)).toBeVisible()
          expect(await noHorizontalScroll(page), `${id} scrolls horizontally`).toBe(true)
          expect(await offscreen(page), `${id} has controls off screen`).toEqual([])
        }
      })
    })
  }
}

for (const scale of ['normal', 'large'] as const) {
  test.describe(`overlay in its 380px window, ${scale} text`, () => {
    test.use({ viewport: { width: 380, height: 800 } })

    test('the whole panel and every control stay inside the window', async ({ page }) => {
      await prime(page, scale)
      await page.goto('/overlay.html')
      const qam = page.getByTestId('qam')
      await expect(qam).toBeVisible()
      // The panel slides in from 1rem to the right; measure where it lands, not mid-flight.
      await page.waitForFunction(() => document.getAnimations().every((a) => a.playState !== 'running'))
      const box = await qam.boundingBox()
      expect(box!.x).toBeGreaterThanOrEqual(-1)
      expect(box!.x + box!.width).toBeLessThanOrEqual(381)
      expect(await noHorizontalScroll(page)).toBe(true)
      expect(await offscreen(page)).toEqual([])
      expect(await qam.evaluate((el) => el.scrollWidth <= el.clientWidth + 1)).toBe(true)
    })
  })
}
