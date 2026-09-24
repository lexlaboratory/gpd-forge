// GPD Forge — text/background token pairs meet WCAG AA in both themes. GPL-3.0-or-later.
//
// The redesign moved the whole palette at once. A pixel baseline cannot say whether dim text on a
// raised card is still readable at arm's length on a 6" panel; a contrast ratio can. Every pair
// below is one the stylesheets actually draw, and each must reach 4.5:1 (AA for normal text) —
// the eyebrow labels are 12px, so the large-text allowance does not apply.
import { test, expect } from '@playwright/test'

const PAIRS: [fg: string, bg: string][] = [
  ['--text', '--bg'],
  ['--text', '--bg-elev'],
  ['--text', '--bg-elev-2'],
  ['--text-dim', '--bg-elev'],
  ['--text-dim', '--bg-elev-2'],
  ['--text-faint', '--bg'],
  ['--text-faint', '--bg-elev'],
  ['--text-faint', '--bg-elev-2'],
  ['--accent', '--bg-elev'],
  ['--accent-ink', '--accent'],
  ['--good', '--bg-elev'],
  ['--good-ink', '--good'],
  ['--warn', '--bg-elev'],
  ['--danger', '--bg-elev'],
  ['--danger', '--bg-elev-2'],
]

function luminance(hex: string): number {
  const h = hex.trim().replace('#', '')
  const full = h.length === 3 ? h.split('').map((c) => c + c).join('') : h
  const [r, g, b] = [0, 2, 4].map((i) => parseInt(full.slice(i, i + 2), 16) / 255)
    .map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4))
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

const ratio = (a: string, b: string) => {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x)
  return (hi + 0.05) / (lo + 0.05)
}

for (const theme of ['dark', 'light'] as const) {
  test(`every text/background pair reaches AA — ${theme}`, async ({ page }) => {
    await page.addInitScript(([t]) => {
      localStorage.setItem('forge-setup-done', '1')
      localStorage.setItem('forge-theme', t)
    }, [theme])
    await page.goto('/')
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme)

    const tokens = await page.evaluate((names) => {
      const cs = getComputedStyle(document.documentElement)
      return Object.fromEntries(names.map((n) => [n, cs.getPropertyValue(n).trim()]))
    }, [...new Set(PAIRS.flat())])

    // A token that is not a plain hex would compute NaN, and `NaN < 4.5` is false: the pair would
    // pass without ever being measured. Refuse that outright.
    for (const [name, value] of Object.entries(tokens)) {
      expect(value, `${name} must be a 6-digit hex to be measured`).toMatch(/^#[0-9a-f]{6}$/i)
    }

    const failures = PAIRS
      .map(([fg, bg]) => ({ pair: `${fg} on ${bg}`, value: ratio(tokens[fg], tokens[bg]) }))
      .filter((r) => r.value < 4.5)
      .map((r) => `${r.pair}: ${r.value.toFixed(2)}:1`)
    expect(failures, `pairs under 4.5:1 in ${theme}`).toEqual([])
  })
}
