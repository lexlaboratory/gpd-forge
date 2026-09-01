// GPD Forge — the charge guard card. GPL-3.0-or-later.
//
// The thing most worth testing here is not a number, it is a REFUSAL. Anything called a charge guard
// invites the assumption "it stops charging at 80 %", and this board has no path to that. If the
// card lets someone believe otherwise, they stop worrying about a pack that is still ageing — which
// is a worse outcome than not shipping the feature at all.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('Charge guard', () => {
  test.beforeEach(async ({ page }) => {
    await new DashboardPage(page).goto()
    await page.getByTestId('nav-system').click()
    await expect(page.getByTestId('page-system')).toBeVisible()
  })

  test('states plainly that it cannot stop the charge', async ({ page }) => {
    const advisory = page.getByTestId('charge-guard-advisory')
    await expect(advisory).toBeVisible()
    await expect(advisory).toContainText('cannot stop charging')

    // And it cites EVIDENCE, so the limitation reads as a measured fact about the board rather than
    // as something GPD Forge chose not to bother with. ADR-0004 records the four read-only findings:
    // no vendor tool implements it, ACPI declares no _BMC/_BMD, _BTP only notifies, and the EC fields
    // that look like a threshold are never referenced by any firmware method.
    await expect(advisory).toContainText('measured rather than assumed')
    await expect(advisory).toContainText('_BMC/_BMD')
  })

  test('counts the hours spent plugged in at high charge', async ({ page }) => {
    const hours = page.getByTestId('charge-guard-hours')
    await expect(hours).toHaveText(/\d+\.\d\s*h at high charge/)
    await expect(hours).not.toHaveText(/--/)

    // The mock seeds an episode in progress, which is the interesting case: a fresh install shows
    // nulls and would exercise only the empty state.
    await expect(page.getByTestId('charge-guard-episode')).toHaveText(/plugged in \d+\.\d h now/)
  })

  test('cooling while charging is off until it is asked for, and round-trips through the daemon', async ({ page }) => {
    // Off by default: silently capping someone's performance because their machine is plugged in is
    // not a decision to make on their behalf.
    const cool = page.getByTestId('charge-guard-cool')
    await expect(cool).toHaveAttribute('aria-pressed', 'false')

    await cool.click()
    await expect(cool).toHaveAttribute('aria-pressed', 'true')

    // Reloaded, not just re-rendered: the toggle has to have reached the daemon, otherwise this
    // tests React state and nothing else.
    await page.reload()
    await page.getByTestId('nav-system').click()
    await expect(page.getByTestId('charge-guard-cool')).toHaveAttribute('aria-pressed', 'true')

    // Leave it as it was found — the mock daemon holds state across specs in this serial suite.
    await page.getByTestId('charge-guard-cool').click()
    await expect(page.getByTestId('charge-guard-cool')).toHaveAttribute('aria-pressed', 'false')
  })

  test('the counters survive a settings change', async ({ page }) => {
    // The POST returns only the settings, so a card that assigned the response wholesale would blank
    // the hours until the next mount — the sort of flicker that gets read as data loss.
    const before = await page.getByTestId('charge-guard-hours').textContent()

    await page.getByTestId('charge-guard-enabled').click()
    await expect(page.getByTestId('charge-guard-hours')).toHaveText(before!)

    await page.getByTestId('charge-guard-enabled').click()
  })
})
