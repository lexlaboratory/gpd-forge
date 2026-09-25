import { defineConfig, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import { resolve } from 'node:path'
import { readFileSync } from 'node:fs'

// GPD Forge UI — Vite config. GPL-3.0-or-later.
// Two entries: the main dashboard (index.html) and the gamepad overlay (overlay.html),
// so the daemon can serve the Quick Access Menu at /overlay.html in its own lean bundle.

// The bundle's own version, burned in at build time from package.json (which VersionModelTests keeps
// equal to Directory.Build.props). This is what lets the About card compare the SHELL against the
// DAEMON: on 2026-08-28 a bundle older than the daemon it talked to cost an afternoon to identify,
// because nothing on screen could say which build was on screen. Read from disk rather than from
// process.env.npm_package_version, which is only set when the build runs through an npm script.
const pkg = JSON.parse(readFileSync(resolve(__dirname, 'package.json'), 'utf8')) as { version: string }

// `vite preview` serves only the E2E suite. Its idle keep-alive sockets stay open for the run instead
// of Node's 5 s: on the dev handheld NEW loopback connects fail for seconds at a time whatever the suite
// does (tests/e2e/fixtures.ts, measured 2026-09-25), and a browser that reuses an open socket for the
// next page.goto never meets that window. The dev server and the production build are untouched.
const longKeepAlive: Plugin = {
  name: 'gpd-forge-preview-keep-alive',
  configurePreviewServer(server) {
    const http = server.httpServer as { keepAliveTimeout?: number }
    if (typeof http.keepAliveTimeout === 'number') http.keepAliveTimeout = 10 * 60_000
  },
}

export default defineConfig({
  define: { __APP_VERSION__: JSON.stringify(pkg.version) },
  plugins: [react(), longKeepAlive],
  server: { port: 5188, strictPort: true },
  preview: { port: 4173, strictPort: true },
  build: {
    outDir: 'dist',
    sourcemap: true,
    rollupOptions: {
      input: {
        main: resolve(__dirname, 'index.html'),
        overlay: resolve(__dirname, 'overlay.html'),
      },
    },
  },
})
