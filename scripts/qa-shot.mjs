// GPD Forge — capture the real UI at every size and theme that matters. GPL-3.0-or-later.
//
// Replaces the ad-hoc qa-* scripts' single 1280x800 pass. Three viewports on purpose:
//   1024x720  the size the Tauri window actually opens at — never captured before
//   1280x800  the GPD Win 4 panel
//    720x600  below the sidebar-collapse breakpoint, which had never been looked at
//
// Usage: node scripts/qa-shot.mjs [--url http://localhost:4173] [--pages dashboard,alerts]
import { chromium } from '@playwright/test'
import { mkdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

const HERE = dirname(fileURLToPath(import.meta.url))
const OUT = join(HERE, '..', 'tests', 'manual', 'shots')

const arg = (name, fallback) => {
  const i = process.argv.indexOf(`--${name}`)
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : fallback
}
const URL_BASE = arg('url', 'http://127.0.0.1:8787')

// `alerts` is included deliberately: it was missing from the old capture list, and it is the exact
// page whose render crash blanked the whole app.
const PAGES = arg('pages', 'dashboard,power,fan,controller,display,profiles,monitor,system,settings,alerts').split(',')
const VIEWPORTS = [
  { name: '1024x720', width: 1024, height: 720, scale: 1 },
  { name: '1280x800', width: 1280, height: 800, scale: 1.75 },
  { name: '720x600', width: 720, height: 600, scale: 1 },
]
const THEMES = ['dark', 'light']

mkdirSync(OUT, { recursive: true })
const browser = await chromium.launch()
const problems = []

for (const vp of VIEWPORTS) {
  for (const theme of THEMES) {
    const ctx = await browser.newContext({
      viewport: { width: vp.width, height: vp.height },
      deviceScaleFactor: vp.scale,
    })
    const page = await ctx.newPage()
    page.on('pageerror', (e) => problems.push(`${vp.name}/${theme}: PAGEERROR ${e.message}`))
    page.on('console', (m) => m.type() === 'error' && problems.push(`${vp.name}/${theme}: ${m.text()}`))

    await page.addInitScript(([t]) => {
      localStorage.setItem('forge-setup-done', '1')
      localStorage.setItem('forge-theme', t)
    }, [theme])
    await page.goto(URL_BASE, { waitUntil: 'domcontentloaded' })
    await page.evaluate((t) => { document.documentElement.dataset.theme = t }, theme)
    await page.waitForTimeout(1200)

    for (const id of PAGES) {
      const nav = page.getByTestId(`nav-${id}`)
      if (await nav.count()) {
        await nav.click()
        await page.waitForTimeout(700)
      }
      const nodes = await page.locator('#root *').count()
      if (nodes < 10) problems.push(`${vp.name}/${theme}/${id}: BLANK (${nodes} nodes)`)
      await page.screenshot({ path: join(OUT, `${id}--${theme}--${vp.name}.png`), fullPage: true })
    }
    await ctx.close()
  }
}

await browser.close()
console.log(`shots -> ${OUT}`)
console.log(problems.length ? `PROBLEMS:\n  ${problems.join('\n  ')}` : 'no console errors, no blank pages')
process.exit(problems.length ? 1 : 0)
