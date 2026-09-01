// GPD Forge — the MOCK daemon is validated against the shared API contract. GPL-3.0-or-later.
//
// The other half of core.tests/ApiContractTests.cs. That file points the same contract at the real
// daemon; this one points it at the mock. Neither is ever compared to the other — both are compared
// to tests/contract/api-contract.json — because a contract checked against its own replica is what
// produced the 2026-08-28 outage: the mock said "Aviso", the daemon said 1, every test was green and
// the shipped app rendered a black window.
//
// The asymmetry worth understanding: the daemon-side guard catches the daemon drifting from the
// contract, and THIS one catches the mock drifting from it. The second is what keeps the rest of the
// E2E suite meaningful — every other spec in this directory talks to the mock, so if the mock is
// wrong, all of them are confidently testing fiction.
import { test, expect } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

// Playwright loads specs as CommonJS, where `import.meta` is a syntax error that takes the whole
// file down — the same trap visual.spec.ts hit reading ui/package.json.
const CONTRACT_PATH = join(__dirname, '..', 'contract', 'api-contract.json')
const API = process.env.VITE_FORGE_API ?? 'http://127.0.0.1:8799'

type Rule = string | { type: string; oneOf?: string[]; items?: Shape; fields?: Shape }
type Shape = Record<string, Rule>
type Route = { method: string; path: string; shape?: Shape | null; mockOnly?: boolean }

const contract: { routes: Route[] } = JSON.parse(readFileSync(CONTRACT_PATH, 'utf8'))

/** Mirrors ApiContract.CheckType in the C# guard. Kept deliberately small: the two validators have
 *  to agree, and the way to keep two implementations agreeing is to give them very little to do. */
function typeOf(value: unknown): string {
  if (value === null) return 'null'
  if (Array.isArray(value)) return 'array'
  return typeof value === 'object' ? 'object' : typeof value
}

function accepts(union: string, value: unknown): boolean {
  return union
    .split('|')
    .map((t) => t.trim())
    .filter(Boolean)
    .includes(typeOf(value))
}

function validate(actual: unknown, shape: Shape, where: string, problems: string[]): void {
  if (typeOf(actual) !== 'object') {
    problems.push(`${where}: expected a JSON object, got ${typeOf(actual)}`)
    return
  }

  for (const [field, rule] of Object.entries(shape)) {
    const holder = actual as Record<string, unknown>
    // Missing counts as a violation: a field that quietly stops being emitted breaks a client just
    // as surely as one with the wrong type, and is harder to spot.
    if (!(field in holder)) {
      problems.push(`${where}.${field}: declared in the contract but absent from the response`)
      continue
    }
    validateValue(holder[field], rule, `${where}.${field}`, problems)
  }
  // Extra undeclared fields are allowed — the contract is a floor, not an exact match.
}

function validateValue(value: unknown, rule: Rule, where: string, problems: string[]): void {
  if (typeof rule === 'string') {
    if (!accepts(rule, value)) problems.push(`${where}: expected ${rule}, got ${typeOf(value)} (${JSON.stringify(value)?.slice(0, 60)})`)
    return
  }

  if (!accepts(rule.type, value)) {
    problems.push(`${where}: expected ${rule.type}, got ${typeOf(value)}`)
    return
  }

  if (rule.oneOf && !rule.oneOf.includes(value as string)) {
    problems.push(
      `${where}: got ${JSON.stringify(value)}, which is not one of [${rule.oneOf.join(', ')}]. ` +
        'If this is a number, an enum is being emitted as its ordinal and the UI will crash parsing it.',
    )
  }

  if (rule.items && Array.isArray(value)) {
    // Only when non-empty: an empty array is legitimate, and failing on it would make the guard
    // depend on the mock's seed data rather than on its shape.
    value.slice(0, 3).forEach((item, i) => validate(item, rule.items!, `${where}[${i}]`, problems))
  }

  if (rule.fields && value !== null && typeOf(value) === 'object') {
    validate(value, rule.fields, where, problems)
  }
}

const shaped = contract.routes.filter((r) => r.method === 'GET' && r.shape && !r.path.includes('{'))

test.describe('API contract — the mock daemon', () => {
  for (const route of shaped) {
    test(`${route.path} matches the declared shape`, async ({ request }) => {
      const res = await request.get(`${API}${route.path}`)

      expect(
        res.ok(),
        `GET ${route.path} returned ${res.status()}. The mock daemon does not implement a route the ` +
          `contract declares, so no E2E test can reach it and the real daemon's version of it is ` +
          `unverified from the UI side.`,
      ).toBeTruthy()

      const body = await res.text()
      let parsed: unknown
      try {
        parsed = JSON.parse(body)
      } catch {
        throw new Error(`GET ${route.path} did not return JSON. Body starts: ${body.slice(0, 120)}`)
      }

      const problems: string[] = []
      validate(parsed, route.shape!, route.path, problems)
      expect(
        problems,
        `GET ${route.path} does not match tests/contract/api-contract.json:\n` +
          problems.map((p) => `  - ${p}`).join('\n'),
      ).toEqual([])
    })
  }

  test('the mock serves no route the contract does not declare', async ({ request }) => {
    // The direction that stops phantoms. A route the mock invents will pass every spec written
    // against it and 404 in production; /telemetry/stream is allowed only because it is declared
    // `mockOnly: true` with a stated reason, not because unknown extras are tolerated in general.
    const declared = new Set(contract.routes.map((r) => `${r.method} ${r.path}`))

    const served = readFileSync(join(__dirname, '..', '..', 'tools', 'mock-daemon', 'server.mjs'), 'utf8')
    const matches = [...served.matchAll(/method === '(GET|POST|PUT|DELETE)' && path === '(\/[^']*)'/g)]

    // The guard's own guard: a regex that stops matching would otherwise make this test pass by
    // finding nothing, which is the most dangerous way for a coverage check to fail.
    expect(
      matches.length,
      'The mock-daemon route parser matched almost nothing, so this test proves nothing until it is fixed.',
    ).toBeGreaterThan(20)

    const undeclared = matches
      .map((m) => `${m[1]} ${m[2]}`)
      .filter((r) => !declared.has(r))
      // Test-only seams the real daemon deliberately lacks; they are prefixed so they cannot be
      // mistaken for product surface.
      .filter((r) => !r.includes('/_test-'))
      .sort()

    expect(
      [...new Set(undeclared)],
      'The mock daemon serves routes the contract does not declare. Specs written against these ' +
        'pass while the endpoint does not exist in the daemon:\n' +
        undeclared.map((r) => `  - ${r}`).join('\n'),
    ).toEqual([])
  })
})
