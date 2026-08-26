// GPD Forge — overlay screenshot capture (both themes). Not shipped. GPL-3.0-or-later.
import { chromium } from '@playwright/test'
import { mkdirSync } from 'node:fs'

const BASE = process.env.QA_BASE || 'http://127.0.0.1:4173'
const OUT = process.env.QA_OUT || 'C:/Users/Alex/.claude/jobs/e678971e/tmp/qa-overlay'
mkdirSync(OUT, { recursive: true })

const browser = await chromium.launch()
const errors = []
for (const theme of ['dark', 'light']) {
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 1.75 })
  const page = await ctx.newPage()
  page.on('console', (m) => { if (m.type() === 'error') errors.push(`[${theme}] ${m.text()}`) })
  page.on('pageerror', (e) => errors.push(`[${theme}] ${e.message}`))
  await page.addInitScript((t) => localStorage.setItem('forge-theme', t), theme)
  await page.goto(`${BASE}/overlay.html`, { waitUntil: 'networkidle' })
  await page.waitForTimeout(1200)
  await page.screenshot({ path: `${OUT}/overlay-${theme}.png` })
  await ctx.close()
}
await browser.close()
console.log('QA_ERRORS:' + JSON.stringify(errors))
console.log('QA_DONE')
