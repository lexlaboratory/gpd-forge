// GPD Forge — per-game overrides on /app-rules, against the mock daemon. GPL-3.0-or-later.
//
// F1 (2026-09-25). core.tests/Profiles/AppRuleOverridesEndpointTests.cs pins the same behaviour on the
// real daemon; this pins the mock, so a Games page built against the mock cannot learn a contract the
// daemon does not keep: absent `overrides` keeps them, null clears them, and a refusal answers
// 400 { error: <sentence>, code } with the field's code.
import { test, expect, type APIRequestContext } from './fixtures'

const API = 'http://127.0.0.1:8799'

type Rule = { id: string; match: string; overrides: Record<string, unknown> | null }
const find = (body: { rules: Rule[] }, match: string) => body.rules.find((r) => r.match === match)

async function remove(request: APIRequestContext, match: string) {
  const body = await (await request.get(`${API}/app-rules`)).json()
  const rule = find(body, match)
  if (rule) await request.delete(`${API}/app-rules/${rule.id}`)
}

test.describe('Per-game rule overrides (mock daemon)', () => {
  test('round-trip, and a PUT without overrides keeps them', async ({ request }) => {
    const match = 'e2e-eldenring'
    try {
      const added = await request.post(`${API}/app-rules`, {
        data: {
          match, mode: 'gaming',
          overrides: { stapmW: 22, frameCapFps: 60, fanMode: 'Aggressive', gpu: { antiLag: true }, freeze: ['Discord.exe'] },
        },
      })
      expect(added.status()).toBe(200)
      const rule = find(await added.json(), match)!
      expect(rule.overrides).toEqual({
        stapmW: 22, frameCapFps: 60, fanMode: 'Aggressive', gpu: { antiLag: true, chill: null, rsr: null, rsrSharpness: null, ris: null, risSharpness: null }, freeze: ['discord'],
      })

      // The Profiles page's enable toggle sends match/mode/enabled only.
      const toggled = await request.put(`${API}/app-rules/${rule.id}`, { data: { match, mode: 'gaming', enabled: false } })
      expect(find(await toggled.json(), match)!.overrides).toMatchObject({ stapmW: 22 })

      const cleared = await request.put(`${API}/app-rules/${rule.id}`, { data: { match, mode: 'gaming', enabled: true, overrides: null } })
      expect(find(await cleared.json(), match)!.overrides).toBeNull()
    } finally {
      await remove(request, match)
    }
  })

  test('bad overrides are refused with the field code and nothing is stored', async ({ request }) => {
    const cases: [unknown, string][] = [
      [{ stapmW: 60 }, 'bad_stapm'],
      [{ stapmW: '22' }, 'bad_stapm'],
      [{ frameCapFps: -1 }, 'bad_frame_cap'],
      [{ fanMode: 'Manual' }, 'bad_fan_mode'],
      [{ gpu: { antiLag: true, chill: true } }, 'bad_gpu'],
      [{ freeze: ['C:\\evil\\x.exe'] }, 'bad_freeze'],
    ]
    for (const [overrides, code] of cases) {
      const res = await request.post(`${API}/app-rules`, { data: { match: 'e2e-bad', mode: 'gaming', overrides } })
      expect(res.status(), JSON.stringify(overrides)).toBe(400)
      const body = await res.json()
      expect(body.code).toBe(code)
      expect(typeof body.error).toBe('string')   // the sentence the UI shows verbatim
    }
    const plain = await request.post(`${API}/app-rules`, { data: { match: 'e2e-bad', mode: 'turbo' } })
    expect((await plain.json()).code).toBe('bad_rule')
    expect(find(await (await request.get(`${API}/app-rules`)).json(), 'e2e-bad')).toBeUndefined()
  })
})
