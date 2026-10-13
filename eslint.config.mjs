import tsParser from './ui/node_modules/@typescript-eslint/parser/dist/index.js'
import tsPlugin from './ui/node_modules/@typescript-eslint/eslint-plugin/dist/index.js'
import reactHooks from './ui/node_modules/eslint-plugin-react-hooks/index.js'
import globals from './ui/node_modules/globals/index.js'

export default [
  {
    ignores: ['ui/dist/**', 'ui/node_modules/**'],
  },
  {
    files: ['ui/src/**/*.{ts,tsx}', 'tests/e2e/{features,thermal-controls}.spec.ts'],
    languageOptions: {
      parser: tsParser,
      parserOptions: { ecmaVersion: 'latest', sourceType: 'module', ecmaFeatures: { jsx: true } },
      globals: { ...globals.browser, ...globals.node },
    },
    plugins: {
      '@typescript-eslint': tsPlugin,
      'react-hooks': reactHooks,
    },
    rules: {
      ...tsPlugin.configs.recommended.rules,
      'no-unused-vars': 'off',
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'warn',
    },
  },
]
