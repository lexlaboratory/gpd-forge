// GPD Forge - drive the REAL dashboard against the REAL daemon and report what the
// controls actually do. GPL-3.0-or-later.
//
// Not part of the automated suite: it needs the installed service running. It exists because the
// Playwright suite only ever talks to the mock daemon, which is precisely the blind spot that let
// a broken build ship. Run with: node tests/manual/live-ui-probe.mjs
import { chromium } from '@playwright/test'

const BASE = process.env.GPDFORGE_API ?? 'http://127.0.0.1:8787'
const api = async (path, init) => (await fetch(`${BASE}${path}`, init)).json()

const browser = await chromium.launch()
const page = await browser.newPage()

const consoleErrors = []
const failures = []
const writes = []
page.on('console', (m) => m.type() === 'error' && consoleErrors.push(m.text()))
page.on('requestfailed', (r) => failures.push(`${r.method()} ${r.url()} :: ${r.failure()?.errorText}`))
page.on('response', async (r) => {
  const m = r.request().method()
  if (m === 'POST' || m === 'PUT') writes.push(`${m} ${new URL(r.url()).pathname} -> ${r.status()}`)
})

await page.goto(BASE)
await page.waitForTimeout(2500)

const report = (label, value) => console.log(`${label.padEnd(28)} ${value}`)

console.log('=== BEFORE ===')
report('daemon /mode', JSON.stringify(await api('/mode')))
report('UI active mode', await page.getByTestId('active-mode').textContent().catch(() => '(no testid)'))

// Is anything covering the app? A full-bleed overlay swallows every click while the UI underneath
// still renders live data - which looks exactly like "the app does nothing".
const overlay = await page.evaluate(() => {
  const w = document.querySelector('[data-testid="wizard"]')
  if (!w) return null
  const cs = getComputedStyle(w)
  const r = w.getBoundingClientRect()
  return {
    opacity: cs.opacity, visibility: cs.visibility, display: cs.display,
    pointerEvents: cs.pointerEvents, zIndex: cs.zIndex,
    box: `${Math.round(r.width)}x${Math.round(r.height)} @ ${Math.round(r.x)},${Math.round(r.y)}`,
    setupDone: localStorage.getItem('forge-setup-done'),
  }
})
report('wizard overlay present?', overlay ? JSON.stringify(overlay) : 'no')
await page.screenshot({ path: 'tests/manual/live-ui-firstrun.png' })

if (overlay) {
  console.log('\n=== dismissing the wizard, then retrying the same controls ===')
  const skip = page.getByTestId('wizard-skip')
  if (await skip.count()) await skip.click()
  else await page.evaluate(() => localStorage.setItem('forge-setup-done', '1'))
  await page.reload({ waitUntil: 'domcontentloaded' })
  await page.waitForTimeout(2500)
}

console.log('\n=== CLICK a mode chip (battery) ===')
writes.length = 0
await page.getByTestId('mode-battery').click()
await page.waitForTimeout(2500)
report('POSTs seen', writes.length ? writes.join(' | ') : '*** NONE - the click sent nothing ***')
report('daemon /mode', JSON.stringify(await api('/mode')))
report('UI active mode', await page.getByTestId('active-mode').textContent().catch(() => '(no testid)'))

console.log('\n=== DRAG the TDP slider ===')
writes.length = 0
const slider = page.getByTestId('tdp-slider')
await slider.fill('18')
await slider.dispatchEvent('change')
await page.waitForTimeout(2500)
report('POSTs seen', writes.length ? writes.join(' | ') : '*** NONE - the slider sent nothing ***')
report('UI readout', await page.getByTestId('tdp-value').textContent().catch(() => '(no testid)'))

console.log('\n=== console errors ===')
console.log(consoleErrors.length ? consoleErrors.join('\n') : '(none)')
console.log('=== failed requests ===')
console.log(failures.length ? failures.join('\n') : '(none)')

await browser.close()
