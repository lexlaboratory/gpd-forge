import { defineConfig, devices } from '@playwright/test'

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
    video: 'retain-on-failure',
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
      env: { PORT: '8799' },
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
