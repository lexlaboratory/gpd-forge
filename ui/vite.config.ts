import { defineConfig } from 'vite'
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

export default defineConfig({
  define: { __APP_VERSION__: JSON.stringify(pkg.version) },
  plugins: [react()],
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
