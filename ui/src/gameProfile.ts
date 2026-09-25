// GPD Forge — per-game profile rules, as pure functions. GPL-3.0-or-later.
//
// F1 (2026-09-25). A game profile is an app rule with `overrides` (core/Profiles/RuleOverrides.cs).
// The Games page, the overlay's "save as profile" button and the "profile applied" notice all have
// to agree on three questions, so they are answered once, here:
//   - which rule a game actually lands on — the daemon's (AppRulePolicy.Matches): the first ENABLED
//     rule whose needle is a substring of the normalised process name, in list order;
//   - what saving must do so the saved profile really is the one that wins;
//   - what the notice says was applied.
// Nothing here touches the network or the DOM (imports are types only), so tests/e2e/
// game-profile-logic.spec.ts runs it in Node. The requests live in gameProfileSave.ts.
import type {
  ActiveGameProfile, AppRule, AppRuleMatch, GpuDesired, GpuInfo, ModeId, RuleFanMode, RuleOverrides,
} from './types'

/** Mirrors AppRulePolicy.Normalize: trimmed, lowercase, no ".exe" tail. */
export const normalizeMatch = (name: string): string => {
  let p = name.trim().toLowerCase()
  if (p.endsWith('.exe')) p = p.slice(0, -4)
  return p.trim()
}

/** How a game is named on screen: the process name without ".exe", case kept ("League of Legends"). */
export const displayName = (app: string): string => app.trim().replace(/\.exe$/i, '')

/** The rule the daemon would apply to this process, or null when none claims it. */
export function governingRule(rules: readonly AppRule[], app: string): AppRule | null {
  const process = normalizeMatch(app)
  if (process.length === 0) return null
  return rules.find((r) => r.enabled && r.match.length > 0 && process.includes(r.match)) ?? null
}

/** The rule written for exactly this game (enabled or not) — the only one a profile edit may touch. */
export const exactRule = (rules: readonly AppRule[], app: string): AppRule | null =>
  rules.find((r) => r.match === normalizeMatch(app)) ?? null

/** True when at least one field is set. An object of nulls means "the mode decides everything". */
export const hasOverrides = (o: RuleOverrides | null | undefined): o is RuleOverrides =>
  o != null && (o.stapmW != null || o.frameCapFps != null || o.fanMode != null
    || o.gpu?.antiLag != null || o.gpu?.chill != null || (o.freeze?.length ?? 0) > 0)

export type ProfileKind = 'profile' | 'rule' | 'none'

/** `profile`: settings of its own. `rule`: a rule only picks its mode. `none`: the AC/battery default. */
export function profileState(rules: readonly AppRule[], app: string): { kind: ProfileKind; rule: AppRule | null } {
  const rule = governingRule(rules, app)
  return { kind: rule == null ? 'none' : hasOverrides(rule.overrides) ? 'profile' : 'rule', rule }
}

type Summarised = Pick<RuleOverrides, 'stapmW' | 'frameCapFps' | 'fanMode'> & {
  gpu: { antiLag: boolean | null; chill: boolean | null } | null
}

/** "22 W · 60 FPS · Aggressive · Anti-Lag". Only fields that are set; "mode settings" when none is. */
export function describeOverrides(o: Summarised | null | undefined): string {
  if (!o) return 'mode settings'
  const parts: string[] = []
  if (o.stapmW != null) parts.push(`${o.stapmW} W`)
  if (o.frameCapFps != null) parts.push(o.frameCapFps === 0 ? 'no FPS cap' : `${o.frameCapFps} FPS`)
  if (o.fanMode != null) parts.push(o.fanMode)
  // Chill first when both are stated: a game's Chill is what turns Anti-Lag off (the driver refuses
  // the pair), so "Chill · Anti-Lag off" reads as cause and effect.
  if (o.gpu?.chill === true) parts.push('Chill')
  if (o.gpu?.antiLag === true) parts.push('Anti-Lag')
  if (o.gpu?.antiLag === false) parts.push('Anti-Lag off')
  if (o.gpu?.chill === false) parts.push('Chill off')
  return parts.length > 0 ? parts.join(' · ') : 'mode settings'
}

const FIELD_LABEL: Record<string, string> = {
  stapmW: 'TDP', frameCapFps: 'frame cap', fanMode: 'fan', gpu: 'Radeon settings', antiLag: 'Anti-Lag', chill: 'Chill',
}

/**
 * "Profile eldenring applied: 22 W · 60 FPS · Aggressive". Built from what GET /profiles/active says
 * was APPLIED, never from what the rule asked for — "automatic with notice" is only worth having if
 * the notice is true — and it says what was refused, and why, rather than leaving it out.
 */
export function noticeText(p: ActiveGameProfile): string {
  const { lead, detail } = noticeParts(p)
  return `${lead} ${detail}`
}

/** The notice in two halves — who, then what — for a one-line layout where a long game name must be
 *  the part that gives way, not the values the notice exists to show. */
export function noticeParts(p: ActiveGameProfile): { lead: string; detail: string } {
  const lead = `Profile ${displayName(p.game ?? p.match ?? 'game')} applied:`
  // What the user changed since is theirs now (F1 audit round 1): said once, after what still applies,
  // so the overlay's live line cannot contradict the controls right below it.
  const changed = (p.superseded ?? []).map((f) => FIELD_LABEL[f] ?? f)
  const described = describeOverrides(p.applied)
  const applied = changed.length > 0 && described === 'mode settings'
    ? `${changed.join(', ')} changed by you`
    : changed.length > 0 ? `${described} · ${changed.join(', ')} changed by you` : described
  if (p.skipped.length === 0) return { lead, detail: applied }
  const refused = p.skipped.map((s) => `${FIELD_LABEL[s.field] ?? s.field} not applied: ${s.reason}`).join('; ')
  return { lead, detail: `${applied} — ${refused}` }
}

/** Identity of one application of a profile. It changes when the rule is re-applied (an edit mid-game
 *  re-stamps `sinceUtc`), not on every poll — so the notice fires once per change. */
export const profileKey = (p: ActiveGameProfile | null | undefined): string | null =>
  p?.active ? `${p.ruleId}@${p.sinceUtc}` : null

export interface RuleBody { match: string; mode: ModeId; enabled: boolean; overrides: RuleOverrides | null }
export type SavePlan = { kind: 'update'; id: string; body: RuleBody } | { kind: 'add'; body: RuleBody }

/**
 * What saving a profile for `app` sends. Only the game's OWN rule is ever edited: a broader rule that
 * happens to govern it today (`elden` over `eldenring`, a user's `steam`) belongs to every other app it
 * matches, and writing one game's watts into it would change all of them. The rule is re-enabled — a
 * saved profile that silently never applies is worse than one the user switches off again.
 */
export function planSave(rules: readonly AppRule[], app: string, mode: ModeId, overrides: RuleOverrides | null): SavePlan {
  const body: RuleBody = { match: normalizeMatch(app), mode, enabled: true, overrides: hasOverrides(overrides) ? overrides : null }
  const own = exactRule(rules, app)
  return own ? { kind: 'update', id: own.id, body } : { kind: 'add', body }
}

/**
 * How far the game's own rule must move (negative = up) to be the one that wins. A new rule is added
 * at the END of the list, and precedence is list order — so without this, a broader enabled rule above
 * it would keep claiming the game and the profile would be stored but never applied.
 */
export function precedenceDelta(rules: readonly AppRule[], app: string, ownId: string): number {
  const governing = governingRule(rules, app)
  if (!governing || governing.id === ownId) return 0
  const from = rules.findIndex((r) => r.id === ownId)
  const to = rules.findIndex((r) => r.id === governing.id)
  return from > to ? to - from : 0
}

/**
 * Mirrors core/Telemetry/FrameTarget.NonGamePresenters (normalised names): processes that present
 * frames and are never the game — the shell, browsers and web views, launchers and overlays, GPD Forge.
 * Kept literal, like tools/mock-daemon's copy; the daemon's list is the source.
 */
const NON_GAME_PRESENTERS: ReadonlySet<string> = new Set([
  'dwm', 'explorer', 'applicationframehost', 'shellexperiencehost', 'startmenuexperiencehost',
  'searchhost', 'textinputhost', 'lockapp', 'systemsettings',
  'chrome', 'msedge', 'msedgewebview2', 'firefox', 'brave', 'opera', 'vivaldi',
  'steam', 'steamwebhelper', 'epicgameslauncher', 'eadesktop', 'galaxyclient', 'ubisoftconnect',
  'gamebar', 'gamebarftserver', 'xboxpcappft', 'radeonsoftware', 'discord', 'motionassistant',
  'gpd forge', 'gpd-forge',
])

/** True for a process that presents frames but is never the game (FrameTarget.IsNonGame). */
export const isNonGamePresenter = (app: string): boolean => NON_GAME_PRESENTERS.has(normalizeMatch(app))

/**
 * The game under the overlay, for "save as profile" — or null, and the button says so.
 *
 * F1 audit round 1 (2026-09-25): this used to be `lastMatch.process` first. But the focus loop only
 * holds a RULED app under a known non-game window (FocusProfileLoop.Effective); with a game that has
 * no rule yet in front — exactly when a first profile gets saved — opening the overlay (an Edge --app
 * window) made it `msedge`, and saving wrote `msedge -> gaming` with the game's watts, applied to every
 * browser from then on. So: the app presenting frames first, else the focus loop's app only when a
 * rule decided on it.
 *
 * F1 audit round 2: "presenting" (GET /sessions `current`) is NOT filtered by the daemon. The recorder
 * opens a session for whatever FrameTarget chose, and with no game presenting Choose falls back to a
 * non-game one (`return held ?? front`) — only /sessions/games leaves them out. The live device had a
 * chrome.exe session and 37 dwm.exe ones, so on the desktop the button offered `dwm -> gaming`. A known
 * non-game presenter is ignored here; a rule the user wrote (`lastMatch` with a ruleId) is theirs.
 */
export function gameUnderOverlay(
  lastMatch: Pick<AppRuleMatch, 'process' | 'ruleId'> | null | undefined,
  presenting: string | null | undefined,
): string | null {
  if (presenting && presenting.trim().length > 0 && !isNonGamePresenter(presenting)) return presenting
  return lastMatch?.ruleId != null && lastMatch.process ? lastMatch.process : null
}

/**
 * The driver frame cap in force, as a rule stores it (0 = cap off), and whether this GPU has one.
 * `fps` null = it cannot be known right now: the agent has not reported, went stale, or the read
 * failed. Callers keep what they had rather than inventing "off".
 *
 * The daemon's request comes first (F1 audit round 2): a game profile or a pick in any window asks
 * GpuDesiredState, and the agent carries it out a tick (3 s) later — reading only the driver would
 * capture the cap from before. With nothing requested the driver's own (Adrenalin) cap is what holds.
 */
export function capInForce(
  gpu: Pick<GpuInfo, 'available' | 'settings'> | null | undefined,
  desired: Pick<GpuDesired, 'requested' | 'frameCapFps'> | null | undefined,
): { supported: boolean; fps: number | null } {
  const frtc = gpu?.available ? gpu.settings?.frameRateCap ?? null : null
  if (!frtc?.supported) return { supported: false, fps: null }
  if (desired?.requested) return { supported: true, fps: desired.frameCapFps ?? 0 }
  return { supported: true, fps: frtc.enabled ? frtc.value : 0 }
}

/** A fan mode a rule can hold. The live fan can be `Manual`; a profile cannot (RuleOverridesPolicy). */
export const toRuleFanMode = (mode: string | null | undefined): RuleFanMode | null =>
  mode === 'Auto' || mode === 'Quiet' || mode === 'Balanced' || mode === 'Aggressive' ? mode : null

/** The overlay's capture: what is in force now for TDP, cap and fan; the Radeon toggles and the
 *  freeze list the overlay cannot see are kept from the profile that exists, not wiped. So is a cap
 *  it could not read (`frameCapFps` null, see capInForce): sending null erased the game's own cap
 *  whenever the GPU agent was silent at the press (F1 audit round 2). */
export function captureOverrides(
  existing: RuleOverrides | null | undefined,
  now: { stapmW: number | null; frameCapFps: number | null; fanMode: RuleFanMode | null },
): RuleOverrides {
  return {
    gpu: existing?.gpu ?? null, freeze: existing?.freeze ?? null, ...now,
    frameCapFps: now.frameCapFps ?? existing?.frameCapFps ?? null,
  }
}
