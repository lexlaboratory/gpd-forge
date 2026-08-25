// One-off: screenshot the running UI to docs/preview.png. GPL-3.0-or-later.
// Usage: start the ui dev server on :5188, then `node tools/snap.mjs`.
import { chromium } from '@playwright/test'

const url = process.env.SNAP_URL ?? 'http://localhost:5188'
const out = process.env.SNAP_OUT ?? 'docs/preview.png'

const browser = await chromium.launch()
const page = await browser.newPage({ viewport: { width: 1024, height: 720 }, deviceScaleFactor: 2 })
await page.goto(url, { waitUntil: 'networkidle' })
await page.getByTestId('mode-ai').click() // show an active mode in the shot
await page.waitForTimeout(300)
await page.screenshot({ path: out })
await browser.close()
console.log('saved', out)
