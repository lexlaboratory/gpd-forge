// GPD Forge — the pure rules behind the Games page and the overlay's "save as profile". GPL-3.0-or-later.
//
// F1 (2026-09-25). No browser here: ui/src/gameProfile.ts imports nothing but types, so Playwright's own
// TypeScript loader runs it in Node. These are the decisions that are easy to get subtly wrong and
// invisible in a screenshot — which rule a game actually lands on (first ENABLED substring match, as
// core/Profiles/AppRulePolicy.Matches does it), where a new rule has to sit to win, and what the notice
// says was applied.
import { test, expect } from '@playwright/test'
import {
  normalizeMatch, displayName, governingRule, exactRule, profileState, describeOverrides, noticeText, noticeParts,
  profileKey, planSave, precedenceDelta, captureOverrides, toRuleFanMode, hasOverrides,
} from '../../ui/src/gameProfile'
import type { ActiveGameProfile, AppRule, RuleOverrides } from '../../ui/src/types'

const rule = (id: string, match: string, extra: Partial<AppRule> = {}): AppRule =>
  ({ id, match, mode: 'gaming', enabled: true, overrides: null, ...extra })

const PROFILE: RuleOverrides = {
  stapmW: 22, frameCapFps: 60, fanMode: 'Aggressive', gpu: { antiLag: true, chill: null }, freeze: ['discord'],
}

test.describe('game profile logic', () => {
  test('names are matched the way the daemon normalises them', () => {
    expect(normalizeMatch('  League of Legends.EXE ')).toBe('league of legends')
    expect(normalizeMatch('eldenring')).toBe('eldenring')
    expect(displayName('League of Legends.exe')).toBe('League of Legends')
    expect(displayName('hades2')).toBe('hades2')
  })

  test('the governing rule is the first ENABLED substring match, in list order', () => {
    const rules = [rule('a', 'ring', { enabled: false }), rule('b', 'elden'), rule('c', 'eldenring')]
    expect(governingRule(rules, 'eldenring.exe')?.id).toBe('b')
    expect(exactRule(rules, 'EldenRing.exe')?.id).toBe('c')
    expect(governingRule(rules, 'hades2.exe')).toBeNull()
    // An empty needle would match everything; the daemon ignores it and so must we.
    expect(governingRule([rule('x', '')], 'hades2')).toBeNull()
  })

  test('profile state says whether a game has settings of its own, only a mode, or nothing', () => {
    expect(profileState([rule('p', 'hades2', { overrides: PROFILE })], 'hades2.exe').kind).toBe('profile')
    expect(profileState([rule('r', 'hades2')], 'hades2.exe').kind).toBe('rule')
    expect(profileState([rule('r', 'steam')], 'hades2.exe').kind).toBe('none')
    // An overrides object with every field null is no profile: the mode decides everything.
    const empty: RuleOverrides = { stapmW: null, frameCapFps: null, fanMode: null, gpu: null, freeze: null }
    expect(hasOverrides(empty)).toBe(false)
    expect(profileState([rule('e', 'hades2', { overrides: empty })], 'hades2').kind).toBe('rule')
  })

  test('the summary reads like the notice Alex asked for, and never invents a field', () => {
    expect(describeOverrides(PROFILE)).toBe('22 W · 60 FPS · Aggressive · Anti-Lag')
    expect(describeOverrides({ ...PROFILE, stapmW: null, frameCapFps: 0, fanMode: null, gpu: { antiLag: false, chill: true } }))
      .toBe('no FPS cap · Chill · Anti-Lag off')
    expect(describeOverrides(null)).toBe('mode settings')
  })

  test('the notice names the game, what was applied, and what was refused', () => {
    const active: ActiveGameProfile = {
      active: true, game: 'eldenring.exe', ruleId: 'r1', match: 'eldenring', mode: 'gaming',
      applied: { stapmW: 22, frameCapFps: 60, fanMode: 'Aggressive', gpu: { antiLag: null, chill: null } },
      skipped: [], freeze: [], sinceUtc: '2026-09-25T10:00:00Z',
    }
    expect(noticeText(active)).toBe('Profile eldenring applied: 22 W · 60 FPS · Aggressive')
    expect(noticeText({ ...active, skipped: [{ field: 'frameCapFps', reason: 'below the auto-FPS target' }] }))
      .toBe('Profile eldenring applied: 22 W · 60 FPS · Aggressive — frame cap not applied: below the auto-FPS target')
    // The overlay lays the two halves out separately so a long name, not the values, is what gives way.
    expect(noticeParts(active)).toEqual({ lead: 'Profile eldenring applied:', detail: '22 W · 60 FPS · Aggressive' })
    // The key changes when the profile is re-applied (an edit mid-game), not on every poll.
    expect(profileKey(active)).toBe('r1@2026-09-25T10:00:00Z')
    expect(profileKey({ ...active, active: false })).toBeNull()
  })

  test('saving updates the game\'s own rule, or adds one — never edits a broader rule', () => {
    const own = rule('own', 'eldenring', { enabled: false, mode: 'windows', overrides: PROFILE })
    const update = planSave([rule('b', 'elden'), own], 'eldenring.exe', 'gaming', { ...PROFILE, stapmW: 18 })
    // Saving a profile turns its rule back on: a saved profile that silently never applies is worse
    // than one the user has to switch off again.
    expect(update).toEqual({ kind: 'update', id: 'own', body: { match: 'eldenring', mode: 'gaming', enabled: true, overrides: { ...PROFILE, stapmW: 18 } } })

    const add = planSave([rule('b', 'elden')], 'EldenRing.exe', 'gaming', PROFILE)
    expect(add).toEqual({ kind: 'add', body: { match: 'eldenring', mode: 'gaming', enabled: true, overrides: PROFILE } })

    // All-null settings are stored as "no overrides", not as an empty object.
    const none = planSave([], 'hades2', 'gaming', { stapmW: null, frameCapFps: null, fanMode: null, gpu: null, freeze: null })
    expect(none.body.overrides).toBeNull()
  })

  test('a new rule is moved above any broader rule that would otherwise win', () => {
    const rules = [rule('x', 'steam'), rule('b', 'elden'), rule('y', 'yuzu'), rule('own', 'eldenring')]
    expect(precedenceDelta(rules, 'eldenring.exe', 'own')).toBe(-2)   // lands just above 'elden'
    expect(precedenceDelta([rule('own', 'eldenring'), rule('b', 'elden')], 'eldenring', 'own')).toBe(0)
    expect(precedenceDelta([rule('b', 'elden', { enabled: false }), rule('own', 'eldenring')], 'eldenring', 'own')).toBe(0)
  })

  test('the overlay capture keeps what it cannot see (GPU, freeze) and drops a fan mode a rule cannot hold', () => {
    expect(captureOverrides(PROFILE, { stapmW: 17, frameCapFps: null, fanMode: 'Quiet' }))
      .toEqual({ ...PROFILE, stapmW: 17, frameCapFps: null, fanMode: 'Quiet' })
    expect(captureOverrides(null, { stapmW: 17, frameCapFps: 0, fanMode: null }))
      .toEqual({ stapmW: 17, frameCapFps: 0, fanMode: null, gpu: null, freeze: null })
    expect(toRuleFanMode('Balanced')).toBe('Balanced')
    expect(toRuleFanMode('Manual')).toBeNull()
    expect(toRuleFanMode('turbo')).toBeNull()
  })
})
