// GPD Forge — AMD GPU profiles, end to end. GPL-3.0-or-later.
//
// The case that matters most here is the ABSENT one. The decision was that when ADLX is unavailable
// the panel renders nothing rather than a disabled row, so the test that protects that is the one
// running against the mock's default. A greyed-out control that says "nearly working" is exactly
// what this project spent a release deleting.
import { test, expect } from './fixtures'
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

  test('the Display page offers no Radeon image controls when GPU control is unavailable', async ({ page }) => {
    await new DashboardPage(page).goto()
    await page.getByTestId('nav-display').click()
    await expect(page.getByTestId('refresh-modes').or(page.getByText('Enumerating'))).toBeVisible()
    await expect(page.getByTestId('display-rsr')).toHaveCount(0)
    await expect(page.getByTestId('display-ris')).toHaveCount(0)
  })

  test('RSR on the Display page is requested from the agent, never claimed applied (F4)', async ({ page }) => {
    await page.route('**/gpu', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        available: true, status: 'Ready', adlxVersion: '1.5.0.124', adapter: 'AMD Radeon(TM) 890M Graphics', detail: 'Verified.',
        settings: {
          antiLag: null, chill: null, boost: null, frameRateCap: null,
          imageSharpening: { supported: false, enabled: false, value: null },
          superResolution: { supported: true, enabled: false, value: 75, min: 0, max: 100 },
        },
      }),
    }))
    const posted: unknown[] = []
    await page.route('**/gpu/image', async (route) => {
      posted.push(route.request().postDataJSON())
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ applied: false, pending: true, reason: 'Handed to the GPU agent.' }) })
    })

    await new DashboardPage(page).goto()
    await page.getByTestId('nav-display').click()
    // An unsupported feature is absent, not greyed out.
    await expect(page.getByTestId('display-ris')).toHaveCount(0)
    await expect(page.getByTestId('display-rsr-sharpness')).toBeDisabled()   // off: its sharpness does nothing
    await page.getByTestId('display-rsr-toggle').click()
    await expect(page.getByTestId('toast-success')).toContainText('handed to the GPU agent')
    expect(posted).toEqual([{ rsr: true }])
  })

  test('a refused sharpness shows the daemon reason (mock daemon, F4)', async ({ request }) => {
    // The mock mirrors the daemon: 409 while no agent reports, and it never answers applied:true.
    const r = await request.post('http://127.0.0.1:8799/gpu/image', { data: { rsr: true } })
    expect(r.status()).toBe(409)
    expect(await r.json()).toMatchObject({ applied: false, pending: false })
  })
})
