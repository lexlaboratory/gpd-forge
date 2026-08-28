// GPD Forge â€” QA screenshot sweep. Captures every page in both themes at the GPD's
// native 1280x800, full-page, and records console/page errors. Not shipped. GPL-3.0-or-later.
import { chromium } from '@playwright/test'
import { mkdirSync } from 'node:fs'

const BASE = process.env.QA_BASE || 'http://127.0.0.1:4173'
const OUT = process.env.QA_OUT || 'C:/Users/Alex/.claude/jobs/e678971e/tmp/qa'
mkdirSync(OUT, { recursive: true })

const PAGES = ['dashboard', 'power', 'fan', 'hardware', 'display', 'profiles', 'monitor', 'system', 'settings']
const THEMES = ['dark', 'light']

const browser = await chromium.launch()
const errors = []
const missing = []
for (const theme of THEMES) {
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 1.75 })
  const page = await ctx.newPage()
  page.on('console', (m) => { if (m.type() === 'error') errors.push(`[${theme}] console.error: ${m.text()}`) })
  page.on('pageerror', (e) => errors.push(`[${theme}] pageerror: ${e.message}`))
  await page.addInitScript((t) => localStorage.setItem('forge-theme', t), theme)
  await page.goto(BASE, { waitUntil: 'networkidle' })
  for (const id of PAGES) {
    const nav = page.getByTestId(`nav-${id}`)
    if (await nav.count() === 0) { missing.push(`nav-${id}`); continue }
    await nav.click()
    // Monitor's sparklines accumulate one sample per 1s telemetry tick â€” linger so the trend fills.
    await page.waitForTimeout(id === 'monitor' ? 12000 : 600)
    // heading should reflect the page
    const h = await page.locator('.page-title').first().textContent().catch(() => null)
    if (!h || !h.trim()) missing.push(`${theme}:${id}:no-title`)
    await page.screenshot({ path: `${OUT}/${theme}-${id}.png`, fullPage: true })
  }
  await ctx.close()
}
await browser.close()
console.log('QA_ERRORS:' + JSON.stringify(errors))
console.log('QA_MISSING:' + JSON.stringify(missing))
console.log('QA_DONE')

