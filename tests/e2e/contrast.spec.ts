// GPD Forge — text/background token pairs meet WCAG AA in both themes. GPL-3.0-or-later.
//
// The redesign moved the whole palette at once. A pixel baseline cannot say whether dim text on a
// raised card is still readable at arm's length on a 6" panel; a contrast ratio can. Every pair
// below is one the stylesheets actually draw, and each must reach 4.5:1 (AA for normal text) —
// the eyebrow labels are 12px, so the large-text allowance does not apply.
import { test, expect } from './fixtures'

// A background that is not a token on its own (F1 audit round 1, 2026-09-25): `mix` is CSS
// color-mix(in srgb, a p%, b) — the open game card's accent tint — and `over` is a translucent token
// composited over an opaque one — the overlay's profile line, --accent-soft on the panel's --bg-elev.
// The first F1 pass drew --text-faint on that tint at 4.26:1 (dark) / 4.38:1 (light) and nothing here
// measured it, because only plain tokens were listed.
type Bg = string | { mix: [a: string, percent: number, b: string] } | { over: [top: string, under: string] }

const PAIRS: [fg: string, bg: Bg][] = [
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
  // The open game card (.game-card.open): its small labels are --text-dim there, the rest --text.
  ['--text-dim', { mix: ['--accent', 10, '--bg-elev-2'] }],
  ['--text', { mix: ['--accent', 10, '--bg-elev-2'] }],
  // The overlay's "Profile X applied" line (.qam-profile): lead in --text-dim, values in --text.
  ['--text-dim', { over: ['--accent-soft', '--bg-elev'] }],
  ['--text', { over: ['--accent-soft', '--bg-elev'] }],
]

type Rgba = [r: number, g: number, b: number, a: number]

/** A 6-digit hex or an rgb()/rgba() value, as getComputedStyle returns a custom property; else null. */
function parse(value: string): Rgba | null {
  const v = value.trim()
  const hex = /^#([0-9a-f]{6})$/i.exec(v)
  if (hex) return [0, 2, 4].map((i) => parseInt(hex[1].slice(i, i + 2), 16)).concat(1) as Rgba
  const fn = /^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*(?:,\s*([\d.]+)\s*)?\)$/i.exec(v)
  return fn ? [Number(fn[1]), Number(fn[2]), Number(fn[3]), fn[4] === undefined ? 1 : Number(fn[4])] : null
}

const toHex = (c: number[]) => '#' + c.slice(0, 3).map((x) => Math.round(x).toString(16).padStart(2, '0')).join('')

/** The opaque colour a background resolves to. srgb color-mix and alpha compositing both interpolate
 *  the gamma-encoded channels, so both are the same lerp. */
function resolve(bg: Bg, tokens: Record<string, string>): string {
  if (typeof bg === 'string') return tokens[bg]
  const [top, t, under] = 'mix' in bg
    ? [parse(tokens[bg.mix[0]])!, bg.mix[1] / 100, parse(tokens[bg.mix[2]])!]
    : (() => { const c = parse(tokens[bg.over[0]])!; return [c, c[3], parse(tokens[bg.over[1]])!] as const })()
  return toHex([0, 1, 2].map((i) => top[i] * t + under[i] * (1 - t)))
}

const tokenNames = (bg: Bg): string[] => typeof bg === 'string' ? [bg] : 'mix' in bg ? [bg.mix[0], bg.mix[2]] : [...bg.over]

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
    }, [...new Set(PAIRS.flatMap(([fg, bg]) => [fg, ...tokenNames(bg)]))])

    // A token that is not a plain hex would compute NaN, and `NaN < 4.5` is false: the pair would
    // pass without ever being measured. Refuse that outright. Only a token that is composited OVER
    // something (`over`) may be translucent.
    const translucent = new Set(PAIRS.flatMap(([, bg]) => typeof bg === 'object' && 'over' in bg ? [bg.over[0]] : []))
    for (const [name, value] of Object.entries(tokens)) {
      if (translucent.has(name)) expect(parse(value), `${name} must be a colour to be measured`).not.toBeNull()
      else expect(value, `${name} must be a 6-digit hex to be measured`).toMatch(/^#[0-9a-f]{6}$/i)
    }

    const failures = PAIRS
      .map(([fg, bg]) => ({ pair: `${fg} on ${JSON.stringify(bg)}`, value: ratio(tokens[fg], resolve(bg, tokens)) }))
      .filter((r) => r.value < 4.5)
      .map((r) => `${r.pair}: ${r.value.toFixed(2)}:1`)
    expect(failures, `pairs under 4.5:1 in ${theme}`).toEqual([])
  })
}
