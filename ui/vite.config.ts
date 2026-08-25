import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// GPD Forge UI — Vite config. GPL-3.0-or-later.
export default defineConfig({
  plugins: [react()],
  server: { port: 5188, strictPort: true },
  preview: { port: 4173, strictPort: true },
  build: { outDir: 'dist', sourcemap: true },
})
