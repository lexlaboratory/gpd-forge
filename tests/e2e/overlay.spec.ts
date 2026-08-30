// GPD Forge — Quick Access Menu (overlay) E2E. GPL-3.0-or-later.
import { test, expect } from '@playwright/test'

test.describe('Overlay (QAM)', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/overlay.html')
    await expect(page.getByTestId('qam')).toBeVisible()
  })

  test('renders the panel with live header and controls', async ({ page }) => {
    await expect(page.getByTestId('qam-tdp')).toBeVisible()
    await expect(page.getByTestId('qam-budget')).toBeVisible()
    await expect(page.getByTestId('qam-mode-gaming')).toBeVisible()
    await expect(page.getByTestId('qam-close')).toBeVisible()
  })

  test('mode select marks the active mode', async ({ page }) => {
    const g = page.getByTestId('qam-mode-gaming')
    await g.click()
    await expect(g).toHaveAttribute('aria-pressed', 'true')
  })

  test('TDP stepper changes the value', async ({ page }) => {
    await page.waitForTimeout(500) // let initial preset load settle
    const val = page.getByTestId('qam-tdp')
    const before = (await val.textContent()) ?? ''
    await page.getByTestId('qam-tdp-inc').click()
    await expect(val).not.toHaveText(before)
  })

  test('fan and FPS chips select', async ({ page }) => {
    await page.getByTestId('qam-fan-Quiet').click()
    await expect(page.getByTestId('qam-fan-Quiet')).toHaveClass(/on/)
    await page.getByTestId('qam-fps-60').click()
    await expect(page.getByTestId('qam-fps-60')).toHaveClass(/on/)
  })

  test('keyboard arrow moves focus inside the panel', async ({ page }) => {
    await page.getByTestId('qam-mode-gaming').focus()
    await page.keyboard.press('ArrowDown')
    const focused = await page.evaluate(() => document.activeElement?.getAttribute('data-testid'))
    expect(focused).toBeTruthy()
  })
})

// The overlay used to label auto-FPS as "FPS cap": a control that promised a ceiling and delivered a
// goal. Those are now two rows, and these pin the distinction so it cannot quietly collapse again.
test.describe('Overlay frame rate controls', () => {
  /** The mock reports the GPU unavailable by default (that is the shipped default and the path the
   *  UI must handle by rendering nothing). These tests need the other path, so they say so. */
  const withFrtc = async (page: import('@playwright/test').Page) => {
    await page.route('**/gpu', (route) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        available: true, status: 'Ready', adapter: 'AMD Radeon(TM) 890M Graphics', detail: 'ok',
        settings: { frameRateCap: { supported: true, enabled: false, value: 60, min: 15, max: 1000 } },
      }),
    }))
  }

  test('the cap row is absent when the driver offers no cap', async ({ page }) => {
    // The default mock state. On a gamepad-first overlay an unusable row is one more thing to skip
    // past with the D-pad, so it is hidden rather than disabled.
    await page.goto('/overlay.html')

    await expect(page.getByText('FPS target', { exact: true })).toBeVisible()
    await expect(page.getByTestId('qam-cap-60')).toHaveCount(0)
  })

  test('auto-FPS is labelled a target, and the real cap is its own row', async ({ page }) => {
    await withFrtc(page)
    await page.goto('/overlay.html')

    // The renamed control. "FPS cap" is no longer the label on the auto-TDP one.
    await expect(page.getByText('FPS target', { exact: true })).toBeVisible()
    await expect(page.getByTestId('qam-cap-60')).toBeVisible()
  })

  test('choosing a cap sends it to the driver endpoint, not to auto-FPS', async ({ page }) => {
    const posts: { url: string; body: string }[] = []
    page.on('request', (r) => {
      if (r.method() === 'POST') posts.push({ url: r.url(), body: r.postData() ?? '' })
    })

    await withFrtc(page)
    await page.goto('/overlay.html')
    await page.getByTestId('qam-cap-45').click()

    await expect.poll(() => posts.some((p) => p.url.includes('/gpu/frame-cap'))).toBeTruthy()

    const cap = posts.find((p) => p.url.includes('/gpu/frame-cap'))!
    expect(JSON.parse(cap.body).fps).toBe(45)

    // Assert the daemon ANSWERED, not merely that the request left. The first version of this test
    // checked only that the POST was sent and passed while the mock crashed on every one of them.
    //
    // auto-FPS is turned off first because the tests share one mock daemon: another spec leaving a
    // 60 FPS target enabled would make a 45 FPS cap conflict legitimately, and this test would fail
    // for a reason that has nothing to do with what it is checking.
    await page.request.post('http://127.0.0.1:8799/auto-fps', { data: { enable: false, targetFps: 60 } })
    const res = await page.request.post('http://127.0.0.1:8799/gpu/frame-cap', { data: { fps: 45 } })
    expect(res.ok()).toBeTruthy()
    expect((await res.json()).pending).toBe(true)
    // The point of the split: picking a cap must not touch the auto-TDP target.
    expect(posts.some((p) => p.url.includes('/auto-fps'))).toBeFalsy()
  })
})
