/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_FORGE_API?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}

// The UI bundle's own version, injected by vite.config.ts from ui/package.json at build time.
// Distinct from the daemon's version on purpose — the About card compares the two, because a shell
// and a daemon from different builds is a real failure mode this project has already lived through.
declare const __APP_VERSION__: string
