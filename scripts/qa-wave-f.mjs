// GPD Forge — QA capture for wave F (wizard + system health). Not shipped. GPL-3.0-or-later.
import { chromium } from '@playwright/test'
import { mkdirSync } from 'node:fs'
const BASE = process.env.QA_BASE || 'http://127.0.0.1:4173'
const OUT = process.env.QA_OUT || 'C:/Users/Alex/.claude/jobs/e678971e/tmp/qa-f'
mkdirSync(OUT, { recursive: true })
const browser = await chromium.launch()
const errs = []

// 1) First-run wizard (clean localStorage → it should appear)
{
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 1.75 })
  const page = await ctx.newPage()
  page.on('pageerror', (e) => errs.push('wizard:' + e.message))
  await page.addInitScript(() => localStorage.setItem('forge-theme', 'dark'))
  await page.goto(BASE, { waitUntil: 'networkidle' })
  await page.waitForTimeout(700)
  await page.screenshot({ path: `${OUT}/wizard.png` })
  await ctx.close()
}

// 2) System page — health card + panic cool (skip the wizard via the flag)
{
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 1.75 })
  const page = await ctx.newPage()
  page.on('pageerror', (e) => errs.push('system:' + e.message))
  await page.addInitScript(() => { localStorage.setItem('forge-theme', 'dark'); localStorage.setItem('forge-setup-done', '1') })
  await page.goto(BASE, { waitUntil: 'networkidle' })
  await page.getByTestId('nav-system').click()
  await page.waitForTimeout(600)
  await page.screenshot({ path: `${OUT}/system-health.png`, fullPage: true })
  await ctx.close()
}
console.log('QA_ERRORS:' + JSON.stringify(errs))
await browser.close()
console.log('QA_DONE')
