// GPD Forge — Dashboard Page Object. GPL-3.0-or-later.
import { type Page, type Locator, expect } from '@playwright/test'

export class DashboardPage {
  readonly page: Page
  readonly device: Locator
  readonly stats: Locator
  readonly tdpSlider: Locator
  readonly tdpValue: Locator
  readonly activeMode: Locator

  constructor(page: Page) {
    this.page = page
    this.device = page.getByTestId('device')
    this.stats = page.locator('.tile')
    this.tdpSlider = page.getByTestId('tdp-slider')
    this.tdpValue = page.getByTestId('tdp-value')
    this.activeMode = page.getByTestId('active-mode')
  }

  async goto() {
    // Pre-set the first-run wizard's "done" flag so every spec using this POM sees the normal
    // dashboard, not the setup wizard (tests/e2e/wizard.spec.ts exercises the wizard itself, on a
    // deliberately clean localStorage, without going through this POM).
    await this.page.addInitScript(() => localStorage.setItem('forge-setup-done', '1'))
    await this.page.goto('/', { waitUntil: 'domcontentloaded' })
    // Vite may compile deps on the first request; allow generous time for first paint.
    await expect(this.device).toBeVisible({ timeout: 15_000 })
  }

  mode(id: string): Locator {
    return this.page.getByTestId(`mode-${id}`)
  }

  async pickMode(id: string) {
    await this.mode(id).click()
  }
}
