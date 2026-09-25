// GPD Forge — saving and removing a game profile against the daemon. GPL-3.0-or-later.
//
// F1 (2026-09-25). The decisions are in gameProfile.ts (pure, unit-tested); this file only sends them.
// Shared by the Games page editor and the overlay's "save as profile" button so the two cannot end up
// writing profiles differently. Errors propagate with the daemon's own sentence (api.ts `rules`), which
// is what the caller shows.
import type { AppRulesInfo, ModeId, RuleOverrides } from './types'
import { addAppRule, getAppRules, moveAppRule, updateAppRule } from './api'
import { exactRule, planSave, precedenceDelta } from './gameProfile'

/** Create or update the game's own rule, then make sure it is the rule that wins for that game. */
export async function saveGameProfile(app: string, mode: ModeId, overrides: RuleOverrides | null): Promise<AppRulesInfo> {
  // Read fresh rather than trusting the caller's copy: the Profiles page, the overlay and this editor
  // all write the same list, and planning against a stale one could add a duplicate the daemon refuses.
  const plan = planSave((await getAppRules()).rules, app, mode, overrides)
  let info = plan.kind === 'update'
    ? await updateAppRule(plan.id, plan.body)
    : await addAppRule(plan.body.match, plan.body.mode, plan.body.overrides)
  const own = exactRule(info.rules, app)
  const delta = own ? precedenceDelta(info.rules, app, own.id) : 0
  if (own && delta !== 0) info = await moveAppRule(own.id, delta)
  return info
}

/**
 * Clear the game's settings. The rule itself stays: it may have picked this game's mode long before it
 * had a profile (the seeded `yuzu` -> gaming is one), and removing settings must not also change which
 * mode the game runs in. Deleting the rule is the Profiles page's job.
 */
export async function removeGameProfile(app: string): Promise<AppRulesInfo> {
  const own = exactRule((await getAppRules()).rules, app)
  if (!own) return getAppRules()
  return updateAppRule(own.id, { match: own.match, mode: own.mode, enabled: own.enabled, overrides: null })
}
