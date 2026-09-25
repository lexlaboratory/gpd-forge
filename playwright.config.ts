import { defineConfig, devices } from '@playwright/test'
import { MOCK_LOG } from './tests/e2e/mock-log'

// GPD Forge — Playwright config. GPL-3.0-or-later.
// The zero-defect visual/functional gate. Boots the Vite dev server for ui/ and runs
// tests/e2e against it. In CI it retries and captures artifacts on failure.
export default defineConfig({
  testDir: './tests/e2e',
  // Serial: tests share one mock daemon (mutable state), so a single worker keeps them deterministic.
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  // 60 s, not the default 30: a test that meets a loopback stall waits in fixtures.ts's retry for up
  // to 40 s (RIDE_OUT_MS, longer than the 35 s worst stall measured) and must still have time to run.
  timeout: 60_000,
  reporter: process.env.CI ? [['html', { open: 'never' }], ['list']] : 'list',
  expect: {
    toHaveScreenshot: {
      // Freeze CSS animations/transitions at their end state and hide the text caret: both are
      // things a comparison would otherwise catch mid-flight, which is how visual suites earn the
      // reputation for flaking that gets them switched off.
      animations: 'disabled',
      caret: 'hide',
      // Compare in CSS pixels, so a baseline does not silently depend on the device scale factor of
      // the machine that produced it.
      scale: 'css',
      // No maxDiffPixels on purpose: with the daemon stubbed and the clock/locale pinned (see
      // tests/e2e/visual.spec.ts) the render is byte-stable, and a pixel budget large enough to
      // absorb noise is also large enough to absorb a tile that has gone back to reading "--".
    },
  },
  use: {
    baseURL: 'http://127.0.0.1:4173',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    // One browser context per worker, reset between tests, instead of a fresh one per test. On the dev
    // handheld NEW connects to 127.0.0.1 fail for seconds at a time (Node: `connect ETIMEDOUT` after
    // ~305 ms; Chromium: a page that never loads) while open sockets keep working — so every new
    // connection a test needs is a chance to fail at random. A reused context keeps its keep-alive
    // sockets, and so does the request fixture's agent now that the mock and the preview server keep
    // idle sockets for the run. The stalls themselves are the host's: first blamed on the suite's own
    // ~1,000 connects a run, but a connect probe (F1 audit round 3, 2026-09-25) logged them at ~30
    // suite connects a run and after the suite had exited (tests/e2e/fixtures.ts, docs/ROADMAP.md).
    // fixtures.ts retries the connects that remain. Playwright resets cookies, cache, localStorage,
    // routes and init scripts between tests; what it does NOT reset (granted permissions, offline
    // mode, extra headers, window.name) no spec here touches. connection-churn.spec.ts guards it.
    reuseContext: true,
    // Must stay off: any video mode silently disables reuseContext, and with it the fix above.
    // Failures still get a screenshot and the error-context page snapshot.
    video: 'off',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],
  // Boot the mock daemon AND the UI. The UI is E2E-tested against its PRODUCTION BUILD
  // served by `vite preview` (static, deterministic) — not the dev server, whose on-the-fly
  // dep re-optimization flakes under rapid reloads. VITE_FORGE_API is inlined at build time.
  // Dedicated ports so we never bind to another dev server (e.g. jano on 5173).
  // Test port 8799 (the real installed service may own 8787).
  webServer: [
    {
      command: 'node tools/mock-daemon/server.mjs',
      // MOCK_LOG: every request, socket and event-loop stall, written to tests/e2e/artifacts/ — the
      // record that turned "cause unknown" into a diagnosis. stdout/stderr are piped so a handler
      // that throws shows up in the run's output instead of vanishing with the process.
      env: { PORT: '8799', MOCK_LOG },
      stdout: 'pipe',
      stderr: 'pipe',
      url: 'http://127.0.0.1:8799/health',
      reuseExistingServer: false,
      timeout: 30_000,
    },
    {
      command: 'npm --prefix ui run build && npm --prefix ui run preview -- --host 127.0.0.1',
      url: 'http://127.0.0.1:4173',
      env: { VITE_FORGE_API: 'http://127.0.0.1:8799' },
      reuseExistingServer: false,
      timeout: 180_000,
    },
  ],
})
