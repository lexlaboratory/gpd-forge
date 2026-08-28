// GPD Forge — capture the dashboard under each candidate phosphor hue. GPL-3.0-or-later.
// The whole HUD palette hangs off two tokens, so the choice is a two-line change now and an
// expensive one later. This makes it a decision made from pictures instead of adjectives.
import { chromium } from '@playwright/test'

const URL_BASE = process.env.FORGE_UI ?? 'http://localhost:4173'
const VARIANTS = {
  cian:  ['#39e0ff', '#04141b', 'rgba(57,224,255,'],
  ambar: ['#ffb31f', '#241500', 'rgba(255,179,31,'],
  verde: ['#4df58a', '#032013', 'rgba(77,245,138,'],
}

const browser = await chromium.launch()
for (const [name, [accent, ink, rgb]] of Object.entries(VARIANTS)) {
  const ctx = await browser.newContext({ viewport: { width: 1100, height: 760 }, deviceScaleFactor: 1.5 })
  const page = await ctx.newPage()
  await page.addInitScript(() => localStorage.setItem('forge-setup-done', '1'))
  await page.goto(URL_BASE, { waitUntil: 'domcontentloaded' })
  // Must match the specificity of the `:root[data-theme="dark"]` block in tokens.css, otherwise a
  // plain `:root` rule loses to it and the override silently does nothing.
  await page.addStyleTag({
    content: `:root,:root[data-theme="dark"]{--accent:${accent};--accent-ink:${ink};--bg-grid:${rgb}0.05);--glow-accent:0 0 .5rem ${rgb}0.5);}`,
  })
  await page.waitForTimeout(1500)
  await page.screenshot({ path: `tests/manual/shots/fosforo--${name}.png` })
  await ctx.close()
  console.log('captured', name)
}
await browser.close()
