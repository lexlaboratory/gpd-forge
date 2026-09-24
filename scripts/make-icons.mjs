// GPD Forge — regenerate every raster icon from the SVG master. GPL-3.0-or-later.
//
//   node scripts/make-icons.mjs            render ui/public/logo.svg -> ui/src-tauri/icon-source.png (1024)
//                                          then run `tauri icon` to rebuild ui/src-tauri/icons/*
//   node scripts/make-icons.mjs --preview  also write small previews to scripts/logs/icon-preview-*.png
//
// The master used to be a lone PNG with nothing in the repo able to regenerate it. Playwright is
// already a dev dependency, and its Chromium renders SVG exactly as the app's own webview does.
import { chromium } from '@playwright/test'
import { execSync } from 'node:child_process'
import { mkdirSync, readFileSync, rmSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = join(dirname(fileURLToPath(import.meta.url)), '..')
const svg = readFileSync(join(root, 'ui', 'public', 'logo.svg'), 'utf8')
const preview = process.argv.includes('--preview')

async function render(page, size, out) {
  await page.setViewportSize({ width: size, height: size })
  await page.setContent(
    `<html><body style="margin:0;background:transparent">` +
    svg.replace('<svg ', `<svg width="${size}" height="${size}" `) +
    `</body></html>`)
  await page.locator('svg').screenshot({ path: out, omitBackground: true })
}

const browser = await chromium.launch()
const page = await browser.newPage({ deviceScaleFactor: 1 })
await render(page, 1024, join(root, 'ui', 'src-tauri', 'icon-source.png'))
if (preview) {
  mkdirSync(join(root, 'scripts', 'logs'), { recursive: true })
  for (const s of [256, 64, 32, 16]) await render(page, s, join(root, 'scripts', 'logs', `icon-preview-${s}.png`))
}
await browser.close()

if (preview) {
  console.log('previews written to scripts/logs/icon-preview-*.png (icons/ left untouched)')
} else {
  // One command string (no argument array) so the same line runs through cmd.exe and sh alike.
  execSync('npx tauri icon src-tauri/icon-source.png', { cwd: join(root, 'ui'), stdio: 'inherit' })
  // tauri icon also emits Android and iOS sets; this is a Windows desktop app.
  for (const mobile of ['android', 'ios']) rmSync(join(root, 'ui', 'src-tauri', 'icons', mobile), { recursive: true, force: true })
  console.log('icons regenerated from ui/public/logo.svg')
}
