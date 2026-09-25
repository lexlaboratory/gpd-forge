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
    // One browser context per worker, reset between tests, instead of a fresh one per test. This is
    // the fix for the suite's old "one run in three fails a random handful" flake, and it is about
    // TCP, not about speed: a fresh context opens fresh sockets to the mock and the preview server —
    // ~1,000 new loopback connections a run — and on the dev handheld Windows stops completing new
    // connects to 127.0.0.1 for 10–35 s once that many have been opened in a couple of minutes
    // (measured 2026-09-25 with a bare net.connect loop; see docs/ROADMAP.md). A reused context keeps
    // its keep-alive sockets, and the run opens ~20. Playwright resets cookies, cache, localStorage,
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
