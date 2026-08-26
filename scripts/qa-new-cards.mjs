// GPD Forge — QA capture for the new Guardian + AI cards. Not shipped. GPL-3.0-or-later.
import { chromium } from '@playwright/test'
import { mkdirSync } from 'node:fs'

const BASE = process.env.QA_BASE || 'http://127.0.0.1:4173'
const OUT = process.env.QA_OUT || 'C:/Users/Alex/.claude/jobs/e678971e/tmp/qa-new'
mkdirSync(OUT, { recursive: true })

const browser = await chromium.launch()
const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 1.75 })
const page = await ctx.newPage()
const errs = []
page.on('console', (m) => { if (m.type() === 'error') errs.push(m.text()) })
page.on('pageerror', (e) => errs.push(e.message))
await page.addInitScript(() => localStorage.setItem('forge-theme', 'dark'))
await page.goto(BASE, { waitUntil: 'networkidle' })

await page.getByTestId('mode-ai').click()      // reveals JobsPanel + AiCard
await page.waitForTimeout(800)
await page.screenshot({ path: `${OUT}/ai-dashboard.png`, fullPage: true })

await page.getByTestId('nav-settings').click() // Guardian card lives in Settings
await page.waitForTimeout(500)
await page.screenshot({ path: `${OUT}/settings-guardian.png`, fullPage: true })

console.log('QA_ERRORS:' + JSON.stringify(errs))
await ctx.close(); await browser.close()
console.log('QA_DONE')
