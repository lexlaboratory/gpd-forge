// GPD Forge — the version model, end to end. GPL-3.0-or-later.
//
// These cover the two states that matter and are easy to get wrong: agreement (the normal case, where
// nothing alarming may be shown) and disagreement (the case the card exists for). The mismatch is
// produced by intercepting /version rather than by teaching the mock daemon to lie, so the mock keeps
// exactly one honest behaviour and the test still exercises the real rendering path.
import { test, expect } from '@playwright/test'
import { DashboardPage } from './pages/DashboardPage'

test.describe('Version model', () => {
  test('the About card shows the shell and daemon builds, and stays quiet when they agree', async ({ page }) => {
    await new DashboardPage(page).goto()
    await page.getByTestId('nav-settings').click()

    const card = page.getByTestId('version-card')
    await expect(card).toBeVisible()

    // Both readouts must carry a real version, not a placeholder. `--` would mean the daemon never
    // answered, and this repo has shipped tiles that showed `--` while claiming to be live.
    await expect(page.getByTestId('version-shell')).not.toContainText('--')
    await expect(page.getByTestId('version-daemon')).not.toContainText('--')
    await expect(page.getByTestId('version-daemon')).not.toContainText('unreachable')

    // Agreement must be silent: a warning shown when nothing is wrong trains people to ignore it.
    await expect(page.getByTestId('version-mismatch')).toHaveCount(0)

    // The mock records no commit and no build time, and the UI must SAY that rather than blank it or
    // substitute something plausible.
    await expect(page.getByTestId('version-build')).toContainText('not recorded')
  })

  test('a shell and daemon from different builds are called out', async ({ page }) => {
    await page.route('**/version', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          version: '99.0.0',            // deliberately not the shell's version
          commit: 'abcdef1234567890',
          builtUtc: '2026-08-29T12:00:00+00:00',
          runtime: 'test',
          model: 'GPD Win 4 (G1618-04)',
        }),
      })
    })

    await new DashboardPage(page).goto()
    await page.getByTestId('nav-settings').click()

    // The whole point of the card: the disagreement is stated, in a way that says what to do about it.
    const mismatch = page.getByTestId('version-mismatch')
    await expect(mismatch).toBeVisible()
    await expect(mismatch).toContainText('99.0.0')
    await expect(mismatch).toContainText('different builds')

    // And when the daemon DOES record a commit and a build time, they are shown rather than ignored.
    await expect(page.getByTestId('version-build')).toContainText('abcdef123456')
  })

  test('GET /version answers with the documented shape', async ({ request }) => {
    const res = await request.get('http://127.0.0.1:8799/version')
    expect(res.ok()).toBeTruthy()
    const body = await res.json()

    expect(typeof body.version).toBe('string')
    expect(body.version.length).toBeGreaterThan(0)
    expect(typeof body.runtime).toBe('string')
    // commit/builtUtc are nullable by contract — the assertion is that the KEYS exist, so a client can
    // distinguish "not recorded" from "field missing entirely".
    expect('commit' in body).toBeTruthy()
    expect('builtUtc' in body).toBeTruthy()
  })
})
