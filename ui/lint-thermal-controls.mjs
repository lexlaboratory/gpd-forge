import { ESLint } from 'eslint'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const uiRoot = resolve(fileURLToPath(new URL('.', import.meta.url)))
const repoRoot = resolve(uiRoot, '..')
const eslint = new ESLint({
  cwd: repoRoot,
  overrideConfigFile: resolve(repoRoot, 'eslint.config.mjs'),
})
const files = [
  'ui/src/api.ts',
  'ui/src/components/Chip.tsx',
  'ui/src/pages/FanPage.tsx',
  'ui/src/pages/HardwarePage.tsx',
  'ui/src/pages/PowerPage.tsx',
  'ui/src/pages/DashboardPage.tsx',
  'ui/src/pages/TriggerDiagnostics.tsx',
  'ui/src/types.ts',
  'tests/e2e/features.spec.ts',
  'tests/e2e/thermal-controls.spec.ts',
]
const results = await eslint.lintFiles(files)
const formatter = await eslint.loadFormatter('stylish')
const output = formatter.format(results)
if (output) process.stdout.write(output)
if (results.some((result) => result.errorCount > 0 || result.fatalErrorCount > 0)) process.exitCode = 1
