import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { resolve } from 'node:path'

// GPD Forge UI — Vite config. GPL-3.0-or-later.
// Two entries: the main dashboard (index.html) and the gamepad overlay (overlay.html),
// so the daemon can serve the Quick Access Menu at /overlay.html in its own lean bundle.
export default defineConfig({
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
