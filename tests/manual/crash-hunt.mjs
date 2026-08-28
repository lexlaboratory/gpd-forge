// GPD Forge - walk every section of the REAL dashboard against the REAL daemon and report the
// first thing that blanks the app. GPL-3.0-or-later.
//
// The automated suite cannot see this: it runs against the mock daemon, whose responses are always
// well-formed and fully populated. Real hardware returns nulls and empty arrays the mock never emits.
import { chromium } from '@playwright/test'

const BASE = process.env.GPDFORGE_API ?? 'http://127.0.0.1:8787'
const browser = await chromium.launch()
const page = await browser.newPage()

const errors = []
page.on('console', (m) => m.type() === 'error' && errors.push(m.text()))
page.on('pageerror', (e) => errors.push(`PAGEERROR: ${e.message}\n${(e.stack ?? '').split('\n').slice(0, 6).join('\n')}`))

await page.addInitScript(() => localStorage.setItem('forge-setup-done', '1'))
await page.goto(BASE, { waitUntil: 'domcontentloaded' })
await page.waitForTimeout(2000)

// Enumerate the sidebar exactly as the user sees it, rather than hardcoding a guess.
const navs = await page.locator('nav a, nav button, .nav a, .nav button, aside a, aside button')
  .evaluateAll((els) => els.map((e) => (e.textContent ?? '').trim()).filter(Boolean))
console.log('sections found:', JSON.stringify(navs))

const alive = async () => {
  const n = await page.locator('#root *').count()
  return n
}
console.log(`start: ${await alive()} nodes rendered`)

const items = page.locator('nav a, nav button, .nav a, .nav button, aside a, aside button')
for (let i = 0; i < navs.length; i++) {
  const name = navs[i]
  errors.length = 0
  try {
    await items.nth(i).click({ timeout: 5000 })
  } catch (e) {
    console.log(`  ${name.padEnd(14)} (could not click: ${String(e).split('\n')[0]})`)
    continue
  }
  await page.waitForTimeout(2500)
  const nodes = await alive()
  const flag = nodes < 10 ? '  *** BLANK ***' : ''
  console.log(`  ${name.padEnd(14)} ${String(nodes).padStart(4)} nodes${flag}`)
  if (errors.length) console.log('      ' + errors.join('\n      '))
  if (nodes < 10) break
}

console.log(`\nfinal: ${await alive()} nodes`)
await page.screenshot({ path: 'tests/manual/crash-hunt.png', fullPage: true })
await browser.close()
