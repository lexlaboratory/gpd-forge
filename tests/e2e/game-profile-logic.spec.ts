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
  gameUnderOverlay, isNonGamePresenter, capInForce,
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

  test('what the user changed mid-game is said to be theirs, not the profile\'s', () => {
    // F1 audit round 1: the overlay's live line kept saying 22 W beside a stepper showing 18.
    const active: ActiveGameProfile = {
      active: true, game: 'eldenring.exe', ruleId: 'r1', match: 'eldenring', mode: 'gaming',
      applied: { stapmW: null, frameCapFps: 60, fanMode: null, gpu: { antiLag: null, chill: null } },
      skipped: [], superseded: ['stapmW', 'fanMode'], freeze: [], sinceUtc: '2026-09-25T10:00:00Z',
    }
    expect(noticeParts(active).detail).toBe('60 FPS · TDP, fan changed by you')
    const allTheirs = { ...active, applied: { ...active.applied!, frameCapFps: null }, superseded: ['stapmW', 'fanMode', 'frameCapFps'] }
    expect(noticeParts(allTheirs).detail).toBe('TDP, fan, frame cap changed by you')
    // A daemon without the field reads as nothing superseded.
    expect(noticeParts({ ...active, superseded: undefined }).detail).toBe('60 FPS')
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
    // F1 audit round 2: a cap that could not be read (agent silent or stale, GET /gpu failed) is kept as
    // stored. It used to be sent as null, which cleared the game's own 60 FPS.
    expect(captureOverrides(PROFILE, { stapmW: 17, frameCapFps: null, fanMode: 'Quiet' }))
      .toEqual({ ...PROFILE, stapmW: 17, frameCapFps: 60, fanMode: 'Quiet' })
    expect(captureOverrides(PROFILE, { stapmW: 17, frameCapFps: 0, fanMode: 'Quiet' }))
      .toEqual({ ...PROFILE, stapmW: 17, frameCapFps: 0, fanMode: 'Quiet' })
    expect(captureOverrides(null, { stapmW: 17, frameCapFps: 0, fanMode: null }))
      .toEqual({ stapmW: 17, frameCapFps: 0, fanMode: null, gpu: null, freeze: null })
    expect(toRuleFanMode('Balanced')).toBe('Balanced')
    expect(toRuleFanMode('Manual')).toBeNull()
    expect(toRuleFanMode('turbo')).toBeNull()
  })

  // F1 audit round 2 (2026-09-25): the session recorder's `current` is whatever FrameTarget chose, and
  // with no game presenting that is a non-game one (the live device had chrome.exe and 37 dwm.exe
  // sessions). Opening the overlay on the desktop offered to save a `dwm -> gaming` rule.
  test('the game under the overlay is never a known non-game presenter', () => {
    expect(gameUnderOverlay(null, 'dwm.exe')).toBeNull()
    expect(gameUnderOverlay({ process: 'msedge', ruleId: null }, 'chrome.exe')).toBeNull()
    expect(gameUnderOverlay({ process: 'msedge', ruleId: null }, 'msedge')).toBeNull()
    expect(gameUnderOverlay(null, '  MSEdge.EXE ')).toBeNull()
    // A presenting non-game falls back to the app a rule decided on.
    expect(gameUnderOverlay({ process: 'eldenring', ruleId: 'r1' }, 'Chrome.exe')).toBe('eldenring')
    expect(gameUnderOverlay({ process: 'eldenring', ruleId: 'r1' }, 'dwm')).toBe('eldenring')
    // A game presenting is the answer, whatever the focus loop holds.
    expect(gameUnderOverlay({ process: 'msedge', ruleId: null }, 'eldenring.exe')).toBe('eldenring.exe')
    // A rule the user made is theirs to extend, even for a name on the list.
    expect(gameUnderOverlay({ process: 'steam', ruleId: 'r2' }, null)).toBe('steam')
    expect(isNonGamePresenter('GPD Forge.exe')).toBe(true)
    expect(isNonGamePresenter('eldenring')).toBe(false)
  })

  // F1 audit round 2: the save took the cap from panel state read once at open, so a profile that set
  // 60 FPS after the overlay opened was saved as "no FPS cap". The cap is read at the press: what the
  // daemon asked the driver for first (the agent reconciles a tick later), else what the driver holds.
  test("the cap in force: the daemon's request first, then the driver; unknown when it cannot be read", () => {
    const gpu = (frtc: { supported: boolean; enabled: boolean; value: number | null } | null, available = true) =>
      ({ available, settings: frtc ? { antiLag: null, chill: null, boost: null, imageSharpening: null, frameRateCap: frtc } : null })
    const on45 = gpu({ supported: true, enabled: true, value: 45 })
    const off = gpu({ supported: true, enabled: false, value: 60 })
    const requested = (fps: number | null) => ({ requested: true, frameCapFps: fps })
    const none = { requested: false, frameCapFps: null }

    expect(capInForce(null, none)).toEqual({ supported: false, fps: null })
    expect(capInForce(gpu({ supported: true, enabled: true, value: 45 }, false), none)).toEqual({ supported: false, fps: null })
    expect(capInForce(gpu({ supported: false, enabled: false, value: null }), none)).toEqual({ supported: false, fps: null })
    expect(capInForce(gpu(null), none)).toEqual({ supported: false, fps: null })

    expect(capInForce(off, requested(60))).toEqual({ supported: true, fps: 60 })
    expect(capInForce(on45, requested(null))).toEqual({ supported: true, fps: 0 })
    expect(capInForce(on45, none)).toEqual({ supported: true, fps: 45 })
    expect(capInForce(off, none)).toEqual({ supported: true, fps: 0 })
    // GET /gpu/desired failed: the driver's own answer still stands.
    expect(capInForce(on45, null)).toEqual({ supported: true, fps: 45 })
  })
})
