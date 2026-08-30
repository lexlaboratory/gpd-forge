// GPD Forge — AMD GPU profiles, end to end. GPL-3.0-or-later.
//
// The case that matters most here is the ABSENT one. The decision was that when ADLX is unavailable
// the panel renders nothing rather than a disabled row, so the test that protects that is the one
// running against the mock's default. A greyed-out control that says "nearly working" is exactly
// what this project spent a release deleting.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('AMD GPU profiles', () => {
  test('the panel is absent entirely when GPU control is unavailable', async ({ page }) => {
    await new DashboardPage(page).goto()
    await page.getByTestId('nav-profiles').click()

    // Not "hidden", not "disabled" — absent.
    await expect(page.getByTestId('gpu-current')).toHaveCount(0)
    await expect(page.getByTestId('gpu-adapter')).toHaveCount(0)
  })

  test('when available it shows the live settings and what each mode applies', async ({ page }) => {
    await page.route('**/gpu', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          available: true, status: 'Ready', adlxVersion: '1.5.0.124',
          adapter: 'AMD Radeon(TM) 890M Graphics', detail: 'Verified.',
          settings: {
            antiLag: { supported: true, enabled: false, value: null },
            chill: { supported: true, enabled: true, value: 60 },
            boost: { supported: true, enabled: false, value: 84 },
            imageSharpening: null,
            frameRateCap: { supported: false, enabled: false, value: null },
          },
          modeProfiles: {
            gaming: { name: 'Gaming', antiLag: true, chill: false, boost: false },
            battery: { name: 'Battery', antiLag: false, chill: true, boost: false },
          },
        }),
      })
    })

    await new DashboardPage(page).goto()
    await page.getByTestId('nav-profiles').click()

    const current = page.getByTestId('gpu-current')
    await expect(current).toContainText('Chill: on · 60')

    // The three "not on" states must read differently. Collapsing them is how a panel starts lying:
    // "we could not ask" is not "this GPU cannot", and neither is "it is off".
    await expect(current).toContainText('Image sharpening: not readable')
    await expect(current).toContainText('Frame rate cap: not supported by this GPU')
    await expect(current).toContainText('Anti-Lag: off')

    await expect(page.getByTestId('gpu-mode-profiles')).toContainText('gaming: Anti-Lag')
    await expect(page.getByTestId('gpu-mode-profiles')).toContainText('battery: Chill')
  })
})
