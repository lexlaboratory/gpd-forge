// GPD Forge — visual regression. GPL-3.0-or-later.
//
// The suite could tell you every control existed and still not notice that the app had rendered a
// blank page or a grid of "--" tiles: both shipped in one day. This spec is the eye the rest of the
// suite does not have. It pins every section, in both themes, at the three sizes this app is
// actually used at:
//   1024x720  the size the Tauri window opens at
//   1280x800  the GPD Win 4 panel
//    720x600  below the 60rem breakpoint where the sidebar collapses into a top strip
//
// DETERMINISM — the whole spec is worthless if the baselines drift, so everything that moves is
// frozen here rather than tolerated with a fuzzy pixel budget:
//
//  * The daemon is stubbed, not merely quieted. The mock's /telemetry jitters every field on every
//    1 Hz poll, and its state is MUTABLE AND SHARED: the suite runs serially against one process, so
//    overlay.spec leaves the fan on Quiet, tuner.spec leaves a sweep result, alerts.spec leaves
//    seeded alerts. Passing that state through would make every baseline a function of which specs
//    ran before this one — green in isolation, red in a full run. Every GET the client makes (see
//    ui/src/api.ts) is answered from the fixed table below instead.
//  * Fixed telemetry also fixes the sparklines: useHistory (ui/src/Chart.tsx) records a sample only
//    when the value *changes*, so an unchanging feed yields exactly one point per series forever,
//    instead of a trace that grows for as long as the page has been open. The price is explicit:
//    these baselines pin the chart's frame, axis-free layout and single-sample state, not the shape
//    of a live trace — a moving feed would make the point count a function of wall-clock time.
//  * Locale and timezone are pinned because the alert cards render toLocaleString().
//  * reducedMotion + animations:'disabled' stop a transition being caught mid-flight.
//
// Nothing here changes the app: the stubbing lives in the test, the app is byte-for-byte the one
// that ships.
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { test, expect, type Page } from '@playwright/test'

// Must match the VITE_FORGE_API baked into the preview build by playwright.config.ts — that is the
// origin the UI fetches from, and therefore the one to intercept.
const API = 'http://127.0.0.1:8799'

const VIEWPORTS = [
  { name: '1024x720', width: 1024, height: 720 },
  { name: '1280x800', width: 1280, height: 800 },
  { name: '720x600', width: 720, height: 600 },
] as const

// The eleven sidebar sections (App.tsx NAV). The twelfth surface, the overlay, is a separate HTML
// entry point and gets its own test.
const SECTIONS = [
  'dashboard', 'power', 'fan', 'hardware', 'display',
  'profiles', 'sessions', 'monitor', 'system', 'settings', 'alerts',
] as const

const THEMES = ['dark', 'light'] as const

// --- frozen daemon --------------------------------------------------------------------------
// Captured from tools/mock-daemon/server.mjs at its start-up state, with the jittered numbers
// pinned. Keyed by pathname; anything not listed falls through to the real mock so a newly added
// endpoint shows up as a visible diff rather than a silent 404.
// Read from package.json, not hard-coded. A frozen '0.1.0' here meant that bumping the release made
// the About card show a shell-vs-daemon MISMATCH warning in every baseline — a fixture disagreeing
// with the app it is supposed to depict, failing seven screenshots for a reason that had nothing to
// do with the UI. The whole point of the version model is that there is one source of truth.
// __dirname, not import.meta: Playwright loads specs as CommonJS here, where import.meta is a
// syntax error rather than a missing value — the whole file fails to load.
const UI_VERSION: string = JSON.parse(
  readFileSync(join(__dirname, '..', '..', 'ui', 'package.json'), 'utf8')).version

const ALERT_SEEN = '2026-08-28T09:12:00.000Z'
const ALERT_LAST = '2026-08-28T09:41:00.000Z'

const FIXTURES: Record<string, unknown> = {
  '/health': { ok: true, version: UI_VERSION, model: 'GPD Win 4 (G1618-04) · Ryzen AI 9 HX 370' },
  // Frozen on purpose. Left to the live mock, `runtime` would carry the Node version, so a routine
  // Node upgrade would fail every "sections" baseline for a reason unrelated to the UI. The version
  // itself is deliberately NOT frozen away from the real one: the About card compares shell against
  // daemon, and a fixture that disagreed would bake the mismatch warning into every baseline.
  '/version': {
    version: UI_VERSION, commit: null, builtUtc: null,
    runtime: 'frozen runtime (visual fixture)', model: 'GPD Win 4 (G1618-04) · Ryzen AI 9 HX 370',
  },
  '/telemetry': {
    cpuTempC: 61.4, gpuTempC: 58.2, packageW: 19.6, cpuClockMhz: 3300, fanRpm: 3560,
    fanDutyPct: 45, fps: 60, fps1PctLow: 48, batteryPct: 78, dischargeW: 18.2,
    acConnected: false, tdpVerified: true,
  },
  '/mode': { active: 'windows' },
  // Only the sample COUNT is rendered ("N samples in the last 5 minutes"), and it grows with every
  // telemetry poll against the live mock — hence a fixed-length list.
  '/history': {
    samples: Array.from({ length: 12 }, (_, i) => ({
      unixMs: 1787964000000 + i * 1000,
      snap: {
        cpuTempC: 61.4, gpuTempC: 58.2, packageW: 19.6, cpuClockMhz: 3300, fanRpm: 3560,
        fanDutyPct: 45, fps: 60, fps1PctLow: 48, batteryPct: 78, dischargeW: 18.2,
        acConnected: false, tdpVerified: true,
      },
    })),
  },
  // Two alerts on purpose: the empty state is already asserted by alerts.spec, and the populated
  // list — severity pills, the ×N coalescing badge, the timestamp span — is the part with layout to
  // regress. The unread count also pins the sidebar badge that every other section's shot includes.
  '/alerts': {
    alerts: [
      {
        id: 'vis-1', timestampUtc: ALERT_SEEN, lastSeenUtc: ALERT_LAST, severity: 'Critica',
        category: 'Thermal', title: 'Thermal guardian', message: 'CPU 91°C — easing to 24 W',
        technicalData: null, acknowledged: false, dedupeKey: 'thermal', count: 7,
      },
      {
        id: 'vis-2', timestampUtc: ALERT_SEEN, lastSeenUtc: ALERT_SEEN, severity: 'Info',
        category: 'Power', title: 'Mode switched', message: 'Unplugged — switched to Battery.',
        technicalData: null, acknowledged: true, dedupeKey: null, count: 1,
      },
    ],
  },
  '/alerts/summary': {
    unread: 1, unreadInfo: 0, unreadAviso: 0, unreadCritica: 1, unreadOccurrences: 7, latest: null,
  },
  '/jobs': [],
  '/profiles': {
    battery: { stapmW: 8, fastW: 12, slowW: 10, tctlC: 90 },
    windows: { stapmW: 15, fastW: 20, slowW: 17, tctlC: 92 },
    gaming: { stapmW: 25, fastW: 33, slowW: 28, tctlC: 95 },
    ai: { stapmW: 25, fastW: 25, slowW: 25, tctlC: 90 },
    standby: { stapmW: 15, fastW: 20, slowW: 17, tctlC: 92 },
  },
  '/fan': { mode: 'Auto', manualDuty: 128, controllable: true },
  '/battery/budget': {
    minutesRemaining: 78, remainingWh: 40.2, dischargeW: 18.4,
    projections: [
      { watts: 8, minutes: 301 }, { watts: 12, minutes: 201 }, { watts: 15, minutes: 160 },
      { watts: 20, minutes: 120 }, { watts: 25, minutes: 96 },
    ],
  },
  // Play sessions carry absolute timestamps that the mock derives from its own start-up time, so an
  // unfrozen /sessions makes the page render a different clock on every run. Fixed instants here;
  // the spec pins locale and timezone above so the rendered strings are stable too.
  '/sessions': {
    fpsAvailable: true,
    current: null,
    sessions: [
      {
        id: 'vis-s1', app: 'cyberpunk2077',
        startedUtc: '2026-08-27T20:00:00.000Z', endedUtc: '2026-08-27T21:00:00.000Z',
        durationSeconds: 3600, samples: 3600, samplesWithoutFps: 0,
        fpsAvg: 61.8, fps1PctLow: 44.2, fpsMax: 78.9,
        cpuTempAvgC: 81, cpuTempMaxC: 94.2, packageAvgW: 31.4,
        onBattery: false, batteryStartPct: null, batteryEndPct: null, batteryUsedPct: null,
        fpsTrend: Array.from({ length: 96 }, (_, i) => Math.round((61.8 + Math.sin(i / 3.1) * 4) * 10) / 10),
      },
      {
        // Ran entirely on battery — the one shape where a drain figure means anything.
        id: 'vis-s2', app: 'cyberpunk2077',
        startedUtc: '2026-08-26T18:00:00.000Z', endedUtc: '2026-08-26T19:30:00.000Z',
        durationSeconds: 5400, samples: 5400, samplesWithoutFps: 120,
        fpsAvg: 52.4, fps1PctLow: 38.1, fpsMax: 71.2,
        cpuTempAvgC: 78.3, cpuTempMaxC: 91.5, packageAvgW: 24.6,
        onBattery: true, batteryStartPct: 96, batteryEndPct: 31, batteryUsedPct: 65,
        fpsTrend: Array.from({ length: 120 }, (_, i) => Math.round((52.4 + Math.sin(i / 3.1) * 4) * 10) / 10),
      },
      {
        // The frame probe never produced a reading: every FPS field null, empty trend. Pins the
        // "no reading" branch, which is the one most likely to regress into showing a bare 0.
        id: 'vis-s3', app: 'hades2',
        startedUtc: '2026-08-26T09:00:00.000Z', endedUtc: '2026-08-26T09:30:00.000Z',
        durationSeconds: 1800, samples: 1800, samplesWithoutFps: 1800,
        fpsAvg: null, fps1PctLow: null, fpsMax: null,
        cpuTempAvgC: 64.2, cpuTempMaxC: 72.8, packageAvgW: 15.1,
        onBattery: true, batteryStartPct: 88, batteryEndPct: 61, batteryUsedPct: 27,
        fpsTrend: [],
      },
    ],
  },
  '/sessions/games': {
    fpsAvailable: true,
    games: [
      { app: 'cyberpunk2077', sessions: 2, totalSeconds: 9000, fpsAvg: 56.1, fps1PctLow: 40.5, lastPlayedUtc: '2026-08-27T20:00:00.000Z' },
      { app: 'hades2', sessions: 1, totalSeconds: 1800, fpsAvg: null, fps1PctLow: null, lastPlayedUtc: '2026-08-26T09:00:00.000Z' },
    ],
  },
  '/freezer': { frozen: [] },
  '/auto-fps': { enabled: false, targetFps: 60 },
  '/health/check': {
    status: 'warn',
    issues: [{ level: 'warn', code: 'fan_not_spinning', message: 'Fan not spinning while warm — 0 rpm at 74°C CPU.' }],
  },
  '/guardian': {
    enabled: true, autoThrottle: true, tempThrottleC: 90, tempCriticalC: 96, throttleFloorW: 12,
    batteryLowPct: 15, batteryCriticalPct: 8, throttling: false, throttledToW: null,
    lastAlert: null, lastSeverity: 'ok',
  },
  '/system/incumbents': { motionAssistant: false, gpdTool: false },
  '/power-source': { enabled: false, onBatteryMode: 'battery', onAcMode: 'windows' },
  '/settings/export': {
    modePresets: {
      battery: { stapmW: 8, fastW: 12, slowW: 10, tctlC: 90 },
      windows: { stapmW: 15, fastW: 20, slowW: 17, tctlC: 92 },
      gaming: { stapmW: 25, fastW: 33, slowW: 28, tctlC: 95 },
      ai: { stapmW: 25, fastW: 25, slowW: 25, tctlC: 90 },
      standby: { stapmW: 15, fastW: 20, slowW: 17, tctlC: 92 },
    },
    guardian: {
      enabled: true, autoThrottle: true, tempThrottleC: 90, tempCriticalC: 96,
      throttleFloorW: 12, batteryLowPct: 15, batteryCriticalPct: 8,
    },
    fanMode: 'Auto', brightness: 70,
    powerSource: { enabled: false, onBatteryMode: 'battery', onAcMode: 'windows' },
    autoFps: { enabled: false, targetFps: 60 },
  },
  '/ai': {
    antiStandby: { active: false, holders: 0, manual: false },
    sustainedProfile: { stapmW: 25, fastW: 25, slowW: 25, tctlC: 90 },
    vram: {
      reportedMb: 512, adapterName: 'AMD Radeon 890M', available: true,
      advisory: 'UMA/VRAM size is set by the BIOS at boot (GOP/_DSM) and only changes after a reboot. GPD Forge reads the current allocation but will not write it blindly — change it in BIOS setup, or wait for a verified, reversible write path for this board.',
    },
  },
  '/display': { brightness: 70 },
  '/display/refresh': { current: 60, supported: [48, 60], error: null },
  '/display/night': { on: false, warmth: 0 },
  '/display/tablet': {
    convertible: null, raw: null, applied: false,
    advisory: "ConvertibilityEnabled is not set — Windows falls back to chassis-type/DeviceForm detection (the source of the Win 4's known 'everything opens maximized' behavior).",
  },
  '/display/keyboard-backlight': {
    controllable: false, applied: false,
    advisory: "Keyboard backlight is controlled by the embedded controller (the same EC path already blocked on this board's firmware) or the Fn hotkey directly. GPD Forge has no verified write path for it yet, so this stays read-only/advisory.",
  },
  '/led': {
    mode: 'Off', color: '#00c8ff', controllable: true, applied: true,
    advisory: "Mock daemon: LED is presented as controllable so the UI/E2E can exercise the round-trip. The real daemon is gated behind GPDFORGE_ENABLE_HARDWARE=1 and, even then, this HX370's firmware has no working HID write path yet.",
  },
  '/battery/charge-limit': {
    percent: 100, available: true, applied: true,
    advisory: 'Mock daemon: charge limit is presented as available/controllable for UI/E2E. The real daemon is gated, and "stop charging at N%" is an EC/BIOS feature with no known driverless write path yet.',
  },
  '/undervolt': {
    coCount: 0, offsetMv: 0, applied: true,
    advisory: 'Mock daemon: undervolt is presented as applied for UI/E2E. The real daemon is gated, and RyzenAdj (its TDP backend) does not expose Curve Optimizer / PBO at all.',
  },
  '/standby': {
    lastDrainPctPerHour: 6.2, lastDrainSleptHours: 7.5, lastDrainAt: '2026-08-28T01:45:00.000Z',
    topWakeReason: 'Fingerprint device (Win 4)', blockers: ['GPDKeyboard.exe'],
    diagnosticsAvailable: true, diagnosticsError: null, lastRestore: null,
    // Fixed timestamps: these end up rendered in the baseline, so anything derived from "now" would
    // make every snapshot differ from the last one.
    sleepStudy: {
      measuredAt: '2026-08-29T09:00:00.000Z',
      sessions: 120,
      findings: [
        {
          kind: 'failed-resume',
          at: '2026-08-29T03:45:14.000Z',
          detail:
            'Hibernate lasting 5.0 h — the next thing the machine did was an abnormal shutdown, ' +
            'so it did not come back on its own.',
        },
        { kind: 'bugcheck', at: '2026-08-28T07:38:16.000Z', detail: 'Bugcheck, stop code 0x133.' },
      ],
    },
    sleepStudyError: null,
  },
  '/update/check': { current: '0.1.0', latest: null, updateAvailable: false, url: null },
  '/tuner': {
    running: false, goal: 'MaxFps', targetFps: null, minW: 8, maxW: 30, tempCapC: 95,
    currentStapmW: 8, points: [], best: null, note: null,
  },
}

interface Prep { theme: 'dark' | 'light'; density?: 'pad' | 'mouse' }

async function prepare(page: Page, { theme, density = 'mouse' }: Prep) {
  await page.route(`${API}/**`, async (route) => {
    const req = route.request()
    // Reads are frozen; anything else (there should be none in this spec) reaches the real mock, so
    // a stray write is not silently swallowed by a fake 200.
    if (req.method() !== 'GET') return route.continue()
    const body = FIXTURES[new URL(req.url()).pathname]
    if (body === undefined) return route.continue()
    return route.fulfill({ json: body })
  })
  await page.addInitScript(([t, d]) => {
    localStorage.setItem('forge-setup-done', '1') // the first-run wizard has its own spec
    localStorage.setItem('forge-theme', t)
    localStorage.setItem('forge-textscale', 'normal')
    // Pinned, not detected: useDensity would otherwise pick a density from the pointer/gamepad of
    // whatever machine runs the suite.
    localStorage.setItem('forge-density', d)
  }, [theme, density])
}

/** Everything that must have settled before a pixel comparison is meaningful. */
async function settle(page: Page) {
  // The logo is a network image; screenshotting before it decodes yields a hole where the brand is.
  await page.waitForFunction(() => {
    const img = document.querySelector<HTMLImageElement>('.brand-logo')
    return !img || (img.complete && img.naturalWidth > 0)
  })
  await page.evaluate(() => document.fonts.ready)
}

test.use({
  // Alert cards print toLocaleString(); without pinning these, the baseline encodes the clock and
  // the language of the machine that generated it.
  locale: 'en-US',
  timezoneId: 'UTC',
  reducedMotion: 'reduce',
})

// A baseline is pixel-exact for the platform that produced it — Linux and Windows rasterise text
// differently — and Playwright encodes that in the file name (…-chromium-win32.png). Only win32
// baselines are committed, so on the ubuntu-latest CI runner every one of these would fail as a
// MISSING snapshot: a red build that says nothing about the UI. Skipping is the honest state of
// affairs, not a fix. To make CI actually cover this: generate the -linux baselines inside the
// pinned mcr.microsoft.com/playwright container (same image tag as the @playwright/test version),
// commit them next to these, and delete this guard.
test.skip(() => process.platform !== 'win32', 'visual baselines are committed for win32 only')

for (const vp of VIEWPORTS) {
  test.describe(vp.name, () => {
    test.use({ viewport: { width: vp.width, height: vp.height } })

    for (const theme of THEMES) {
      test.describe(theme, () => {
        // Matches the app's own theme so UA-drawn chrome (scrollbars, form controls) does not sit
        // in a dark widget on a light page.
        test.use({ colorScheme: theme })

        test(`sections — ${theme} — ${vp.name}`, async ({ page }) => {
          // Eleven full-page captures in one test; the default 30s is not enough on a cold start.
          test.setTimeout(180_000)
          await prepare(page, { theme })
          await page.goto('/', { waitUntil: 'domcontentloaded' })
          // "Live" means the first telemetry response landed: before it, every tile reads "--" and
          // the offline banner is still on screen.
          await expect(page.getByTestId('conn')).toHaveText('Live', { timeout: 15_000 })
          await settle(page)

          for (const id of SECTIONS) {
            await page.getByTestId(`nav-${id}`).click()
            await expect(page.getByTestId(`page-${id}`)).toBeVisible()
            // Soft: one section regressing should still report the other ten, and on the first run
            // it writes all eleven missing baselines in a single pass instead of one per run.
            await expect.soft(page).toHaveScreenshot(`${id}-${theme}-${vp.name}.png`, { fullPage: true })
          }
        })

        test(`overlay — ${theme} — ${vp.name}`, async ({ page }) => {
          await prepare(page, { theme })
          // The overlay is a separate Vite entry with no theme bootstrap of its own — the main app
          // sets data-theme from React, this page never mounts that shell.
          await page.addInitScript(([t]) => {
            document.documentElement.dataset.theme = t
          }, [theme])
          await page.goto('/overlay.html', { waitUntil: 'domcontentloaded' })
          await expect(page.getByTestId('qam')).toBeVisible({ timeout: 15_000 })
          await expect(page.getByTestId('qam-budget')).not.toHaveText('—')
          await settle(page)
          await expect(page).toHaveScreenshot(`overlay-${theme}-${vp.name}.png`, { fullPage: true })
        })
      })
    }
  })
}

// Density is the central lever of the redesign — the same markup, retokenised for a thumb — and
// until now nothing compared the two layouts as images. Dark only, and only where it bites: the
// densest screen (dashboard), the tallest form (settings), and the collapsed-sidebar width.
test.describe('density: pad', () => {
  test.use({ viewport: { width: 1024, height: 720 }, colorScheme: 'dark' })

  test('pad density — dashboard and settings at 1024x720', async ({ page }) => {
    test.setTimeout(60_000)
    await prepare(page, { theme: 'dark', density: 'pad' })
    await page.goto('/', { waitUntil: 'domcontentloaded' })
    await expect(page.getByTestId('conn')).toHaveText('Live', { timeout: 15_000 })
    await expect(page.locator('html')).toHaveAttribute('data-density', 'pad')
    await settle(page)
    await expect.soft(page).toHaveScreenshot('dashboard-pad-dark-1024x720.png', { fullPage: true })

    await page.getByTestId('nav-settings').click()
    await expect(page.getByTestId('page-settings')).toBeVisible()
    await expect.soft(page).toHaveScreenshot('settings-pad-dark-1024x720.png', { fullPage: true })
  })
})

test.describe('density: pad, collapsed sidebar', () => {
  test.use({ viewport: { width: 720, height: 600 }, colorScheme: 'dark' })

  test('pad density — dashboard at 720x600', async ({ page }) => {
    test.setTimeout(60_000)
    await prepare(page, { theme: 'dark', density: 'pad' })
    await page.goto('/', { waitUntil: 'domcontentloaded' })
    await expect(page.getByTestId('conn')).toHaveText('Live', { timeout: 15_000 })
    await settle(page)
    await expect(page).toHaveScreenshot('dashboard-pad-dark-720x600.png', { fullPage: true })
  })
})
