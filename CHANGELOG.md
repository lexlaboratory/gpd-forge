# Changelog

All notable changes to GPD Forge are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [Unreleased]

### Changed
- **A modern redesign.** The retro HUD (monospace caps everywhere, bracket corners, scanlines,
  phosphor glow, 2–4 px radii) became soft layered surfaces with depth from luminance, 10–16 px
  radii, Segoe UI Variable, large tabular numbers and one accent colour, in both themes. Hover
  styles apply only to a real hovering pointer, so a tap on the touchscreen never leaves a control
  stuck; pressable controls scale slightly on press; nothing that fires tens of times a minute
  (page switches, the Ctrl+K palette) animates. Emoji icons are gone: modes, overlay actions and
  toasts share one inline-SVG set. Destructive buttons are tonal rather than solid red.
- **Below 1100 px the sidebar is a fixed icon rail** instead of a strip that scrolled away with the
  page; the unread-alerts count becomes a dot on the bell.
- **The Quick Access Menu fits its 380 px window.** It was a fixed 23 rem, which with large text
  measured 402 px and pushed the right-hand controls out of the window.
- **New app icon: a GPD Win 4**, drawn as SVG (`ui/public/logo.svg`) and used for the sidebar, the
  overlay, the favicon, the tray and the installer shortcuts. `scripts/make-icons.mjs` rebuilds
  every raster size from it; before, the icon was a lone PNG nothing in the repo could regenerate.
- **The dashboard's TDP has −/+ 1 W buttons** beside the slider: a d-pad can press a button but
  cannot drag a range.
- **The UI is sized for the handheld it runs on.** The two smallest text sizes were 9.6 px and
  11 px, used for HUD labels across the app; they are now 12 px and 13 px. The desktop window opens
  at the panel's native 1280×800 instead of 1024×720. Density detection uses `any-pointer: coarse`,
  because on the Win 4 the touchpad is the primary pointer and the touchscreen alone did not count,
  so the handheld booted into 32 px mouse targets.
- **Sidebar icons are inline SVG** (Lucide shapes) instead of emoji, so they follow the theme's
  colour and sit on one grid on every system.
- **Telemetry is read once a second, by one loop.** A full hardware read (four WMI queries, every
  LibreHardwareMonitor device, the EC) costs ~100–140 ms, and six callers each did their own: the
  worker, `GET /telemetry` for the panel and again for the overlay, auto-profiles every 1.5 s for
  one yes/no, `POST /jobs`, `/health/check` and the standby sampler. A single sampler now reads at
  1 Hz and all of them serve its last reading. Battery, discharge and the ACPI thermal zone are
  queried every 5 s instead of every call, and the WMI queries are built once instead of per read.
  `GET /telemetry` gains `sampledAtMs` and `sampleAgeMs`, so a stalled sampler shows as an ageing
  reading instead of passing for a live one.
- **The fan has its own 1 s loop.** It was the last step of the TDP worker's tick, behind the thermal
  guardian's and charge guard's `ryzenadj` applies, which under a sustained throttle stretched a tick
  to ~1.7 s — so the fan answered heat late by however long a TDP write took. It now runs on a fixed
  timer over the cached temperature; the curve, smoothing, ramp and 3 s sensor grace are unchanged.
  A telemetry sampler that stops publishing now counts as a missing reading: after the same 3 s grace
  the fan goes back to firmware control instead of running its curve off the last cached value.
- **Auto-FPS only writes when its answer changes.** Inside the ±2 FPS deadband it used to re-apply
  the same limit every second — a `ryzenadj` apply and readback per tick for nothing. It also steers
  from the limit actually in force (after a mode switch, the mode's preset) instead of a counter that
  started at 25 W in every mode and was never synced.
- **A manual TDP keeps the mode's thermal limit.** `POST /tdp` used Tctl 90 °C whatever the mode, so
  asking for fewer watts in `windows` (92) or `gaming` (95) also lowered the thermal limit.

### Added
- **The overlay on a back paddle, from the installer (plan F7).** The Win 4 has no Home button;
  map L4, R4 or Menu to F24 in GPD's WinControls and install with
  `-EnableHotkeys -OverlayHotkey F24`. The startup listener then catches that key instead of
  Ctrl+Alt+Home, with no shortcut to build by hand. The key is checked when you install, so a typo
  fails in front of you rather than silently at logon. See `docs/overlay-home-button.md`.
- **Agents / AI mode keeps the fan steady (plan F7).** A long inference run dips a few degrees
  between batches, and the fan curve followed every dip down and back up. In `ai` mode your chosen
  curve (Quiet, Balanced or Aggressive) now waits for a 10 °C fall instead of 5 °C before slowing
  the fan. It never slows a rise, and it leaves Auto and Manual alone.
- **Battery time per game, and gaming vs gaming-battery side by side (plan F6).** On battery the
  overlay's Battery line now reads "~1 h 28 m in this game": the charge left at what this game has
  drained per hour in its recorded battery play (in the current mode when it has 5+ minutes there),
  which holds steadier than the live rate that swings with every menu. Without enough battery play of
  the game it keeps the live-rate figure. Each game card on the Games page shows its energy per hour
  (Wh/h, "chip" when only the package was measured) and, for a game played in both, a Gaming vs
  Gaming (batt) table of FPS, 1 % low, watts and FPS per watt, on one energy basis so a plugged-in
  run is never set against a battery one. The Advisor suggests switching to Gaming (battery) when you
  are on battery in Gaming and the game held 30+ FPS without stutter over 5+ minutes in it; this is
  advice only, since writing the mode into the game's rule would also apply it on AC.
  `GET /battery/budget` gains `game`; `GET /sessions/games` gains `whPerHour`, `energySource`, `modes`.
- **Freeze background apps while playing (plan F5).** A game's profile editor on the Games page has
  a "Freeze background apps while playing" checklist of the heavy apps running now
  (`GET /freezer/candidates`): Ollama, LM Studio, OneDrive and Google Drive are suggested, and
  system processes are never offered. While the game's profile is in force the ticked apps that are
  running are suspended, and the profile notice says "froze ollama, onedrive". They are resumed when
  you leave the game, it exits or the mode changes, on every exit path including errors and a clean
  stop. If the daemon crashes or is killed, the next start resumes them from `game-freeze.json`. An
  app you froze by hand on the Monitor page is never taken over or thawed by a game. While a game
  profile is in force the daemon also waits to run its own heavy background work (the sleep study
  and the battery report) until the game ends.
- **Processor power policy per mode (plan F4, roadmap H4).** Each mode now sets the CPU's energy
  preference (EPP), boost mode and maximum state on the active Windows power scheme: gaming uses
  efficient boost with EPP 33, so the 22-25 W budget is not spent on clock spikes the thermal limit
  takes straight back; windows EPP 50; battery EPP 80 with boost off on battery; AI sustained. Values
  are read back with `powercfg /q` and logged in `GET /audit`; `GET /power-policy` shows the plan and
  what Windows holds. The values in place before the first change are saved and put back by
  `install-gpd-forge.ps1 -Restore` / `-Uninstall`. Other power schemes are never touched. Writes only
  with `GPDFORGE_ENABLE_HARDWARE=1`.
- **Radeon Super Resolution and Image Sharpening (plan F4).** RSR (on/off and sharpness) is read
  and applied through ADLX, and Image Sharpening, until now read-only, can be applied too. Both can
  be set per game in the Games profile editor, and from a new "Radeon image" card on the Display
  page, which appears only when the GPU agent reports them supported. RSR turns Radeon Boost off
  first, and each feature is switched on before its sharpness is written. Like the frame cap, each
  request is written once, so a change made in Adrenalin afterwards stays. When a game leaves, the
  values the driver had before it are put back. If those values were never read, nothing is forced
  off. `GET /gpu` gains `settings.superResolution`, and there is a new `POST /gpu/image`.
- **Forge Advisor (plan F3).** A "Suggestions" card on the Dashboard and the Games page, and a
  one-line suggestion with **Apply** in the overlay, propose changes for the game: cap at the
  display's refresh when it runs well above it uncapped; cap at 30 FPS (or lower the resolution)
  when the thermal guardian throttles and the 1 % low falls under half the average; hold the
  sustained limit at the game's learned thermal ceiling (where the guardian settles, learned per
  game); and fewer watts for a light game with FPS to spare. Nothing changes until Apply, which
  writes that one suggestion into the game's profile and records it; Dismiss hides it.
  `GET /advisor/suggestions`, `POST /advisor/apply`, `POST /advisor/dismiss`.
- **Frame pacing (plan F2).** The overlay draws the game's last 10 s of frame times and says
  "Steady pacing" or "Stutters: N/min" (a stutter is a frame slower than twice the rolling median
  and 25 ms), with the 1 % and 0.1 % lows beside it. `GET /frames` serves the series and the
  metrics. Each play session now also records its 0.1 % low, stutters per minute, the energy it
  cost in Wh (the battery drain when it ran entirely on battery, otherwise the package power), and
  the mode and frame cap it ran under; the Games page shows them under each recent session.
- **Per-game profiles, in the daemon.** A rule in `/app-rules` can now carry `overrides` —
  `stapmW` (5–40 W), `frameCapFps` (`0` = cap off), `fanMode` (Auto / Quiet / Balanced /
  Aggressive), `gpu { antiLag, chill }` and `freeze` — and while that game is settled in front, in
  the rule's mode, the auto-profile worker layers them over the mode: the watts through the same
  intent the manual override uses (one write, owner `game-profile`, kept by the 30 s reassert and
  put back after a guardian throttle), the cap and Radeon features through the GPU agent's desired
  state, the fan through `FanState`. Leaving the game (after the same ~4.5 s hysteresis as a mode
  switch) puts the previous cap, fan and Radeon profile back; a cap or fan the user changed
  mid-game stays theirs. The game's fan mode is never saved to `fan.json`. Steam → Elden Ring
  applies the profile even though both are `gaming`. `GET /profiles/active` says which profile is
  in force, what it applied and what it refused and why (a cap below the auto-FPS target, say).
  `freeze` is stored only until F5 acts on it. Older `app-rules.json` files load unchanged; an
  out-of-band value in a hand-edited file is repaired instead of costing the rule. The Games page
  and the overlay's "Save as profile for this game" button, below, are how you set them.
- **A Games page** (joystick icon, between Profiles and Monitor; LB/RB walk it in rail order). Every
  game the play history has seen — last played, sessions, average FPS and 1 % low, average package
  watts (new on `GET /sessions/games` as `packageAvgW`, duration-weighted, null when unmeasured) —
  and whether it has a profile, only a rule that picks its mode, or nothing. Selecting one opens its
  profile editor, a side sheet from 1100 px and stacked above the list below that: mode, TDP (off by
  default; slider plus ±1 W), frame cap (Off / 30 / 40 / 45 / 60 / mode default), fan (Auto / Quiet /
  Balanced / Aggressive / mode default), Anti-Lag and Chill (turning one on turns the other off —
  AMD refuses the pair). Save writes the game's OWN rule: a broader rule that governs it today
  (`elden` over `eldenring`) is never edited, and a new rule is moved above it, since a rule added
  at the end of the list would otherwise be stored and never win. Remove clears the settings and
  keeps the rule, which may have picked the game's mode long before (the seeded `yuzu` does). B or
  Escape closes the editor and returns to the game.
- **"Profile Elden Ring applied: 22 W · 60 FPS · Aggressive."** When a game profile takes effect the
  main window says so, from what `GET /profiles/active` reports as applied rather than what the rule
  asked for, and names anything refused and why. A profile already in force when the window opens is
  not announced as new; one re-applied after an edit is. The overlay shows the profile in force as
  one line in its header, where a long game name gives way before the values do.
- **"Save as profile for this game" in the overlay.** One press stores the TDP, driver frame cap
  (when the GPU offers one) and fan mode in force into the rule for the game under the overlay — the
  process the focus loop judged, not the overlay's own window — creating the rule or updating it,
  and keeping the Radeon toggles the overlay cannot see. The button names the game, and says "No
  game in front" instead of guessing.
- **The active mode's TDP is applied when the daemon starts.** It used to wait for the first mode
  change, so after a reboot the machine ran on whatever the last writer or the firmware had left
  while the app named a mode that was not in force. It yields to MotionAssistant / GPD Tool exactly
  as a mode switch does.
- **TDP is kept in force by reading it back, not by re-applying it blind.** Every 30 s the daemon
  reads `ryzenadj --info` and re-applies its last write only if the limits moved — never on a
  failed read and never while another power controller runs. A re-apply shows as owner `reassert`
  in `GET /tdp` and the audit log. **Still open:** the `ryzenadj --info` parser is tested against a
  table rebuilt from ryzenadj's own printer, not one captured on the HX 370 (a non-elevated run
  refuses with `WinRing0 Err: Driver not loaded`). The new read-only `--probe-tdp` prints the real
  output verbatim from an elevated shell, to replace the fixture.
- **The daemon sees the app in front of you.** It runs as a service in session 0, where the
  foreground window is always "none", so auto-profiles never switched on focus and the FPS reading
  had no foreground to follow. The session agent (`--gpu-agent`), which runs in your session, now
  reports the foreground app every 3 s (`POST /session/foreground`); `GET /session/foreground` says
  what the daemon is using and whether it came from the agent. The installer now **always** starts
  this agent at logon — before, it existed only with `-EnableGpuProfiles`, so on a default install
  nothing reported and the foreground stayed "none". Radeon profiles still need that switch; without
  it the agent never touches ADLX. `GET /health/check` reports `foreground_unreported` when the
  service has no agent report.
- **A frozen reading says so.** When the daemon's sampler stops reading the hardware, the main
  window shows "Stalled · N s ago" beside the connection chip and dims the live numbers, and the
  overlay does the same; the MCP `get_telemetry` tool adds `stale: true`. `GET /health/check`
  reports `telemetry_stale`, and the service log warns once per outage and notes the recovery —
  before, a hung sensor read logged nothing while the thermal guardian quietly stopped.
- **`GET /tdp` reports the manual override in force** (`manualStapmW`).
- **LB / RB switch sections** from a gamepad, wrapping at either end. Alerts was ten D-pad presses
  from the Dashboard; it is now one.

### Fixed
- **A game's frame cap goes on even when it is the value GPD Forge last set.** The GPU agent wrote
  a cap only when the requested value changed from the last one it had written, so with a 60 FPS
  request carried out yesterday and 45 set in Adrenalin since (the device's state on 2026-09-25), a
  game whose profile says 60 asked for 60 and nothing was written: the game ran at 45 and the notice
  blamed the driver. Each request now has an identity (`capVersion` and `requestedAtUtc` on
  `GET /gpu/desired`), and a new request is carried out whatever its value; an old one is still never
  re-asserted over a change you make in Adrenalin.
- **A restart mid-game no longer leaves the game's cap on the driver for good.** The cap to put back
  lived only in memory, and FRTC is a driver setting that survives the daemon, so a reboot, service
  restart or update with a capped game in front kept that cap for every app, and the next game took
  it for yours. The pending restore is now kept on disk from the game's start to its end and put back
  at startup; a clean stop also ends the profile.
- **With auto-profiles off (`GPDFORGE_AUTO_PROFILES=0`) the Games page and the overlay say a profile
  will not apply.** Nothing applies profiles then, yet the editor said "Applies automatically" and
  the overlay's save toast read as if it would. Profiles still save; the page, the editor and the
  toast now say so plainly.
- **Leaving a game puts back the cap you set in Adrenalin, not an old one of ours.** The cap to
  restore was GPD Forge's last request whenever there had ever been one; but the GPU agent applies a
  request once and never re-asserts it, so a cap changed in Adrenalin afterwards stayed changed. On
  the device a day-old 60 FPS request sat under a driver at 45, and leaving a 30 FPS game wrote 60.
  A driver reading taken after the request had time to apply (two agent ticks) now wins.
- **The overlay's cap row and "Save as profile" read the driver's cap, not a stale request.** The
  same day-old request made the overlay show 60 FPS while the driver held 45, and saving stored 60
  as the game's cap. A request counts only until the agent reports back after it.
- **The E2E suite rides out loopback connect stalls.** On the dev handheld new connections to
  127.0.0.1 fail for seconds at a time whatever the suite does — a probe logged them at ~30 suite
  connections a run, disproving the earlier "the suite opens too many" diagnosis. The mock and the
  preview server now keep idle sockets for the run, and a connect that fails before the request
  left (API setup calls, `page.goto`) is retried for up to 40 s instead of failing a random test.
- **"Save as profile for this game" no longer saves a profile for the overlay itself.** With a game
  that had no rule yet in front, opening the overlay (an Edge window) made the button name `msedge`,
  and pressing it wrote `msedge -> gaming` with the game's watts — applied to every browser from then
  on. The overlay now takes the app presenting frames, else the app a rule decided on, and says "No
  game in front" when there is neither.
- **The overlay's save captures what you chose, not a passing write.** It stored the stepper's value,
  seeded once when the panel opened from the last write by any owner — opened mid-throttle, the
  guardian's ceiling became the game's watts. TDP and fan are now read from the daemon at the press,
  TDP as your own intent (`GET /tdp` `intentStapmW`: manual, else the game's, else the preset); the
  button waits until both are known, and a fan pick the daemon refuses is said and undone instead of
  staying on screen to be saved.
- **"Save as profile for this game" no longer offers the desktop or a browser as the game.** The
  frame recorder's current app is whatever presents frames, which with no game running is `dwm` or
  `chrome` — and the button offered to save `dwm -> gaming`. Known non-game presenters (the shell,
  browsers, launchers, overlays, GPD Forge) are ignored; the button says "No game in front" instead.
- **The overlay's save keeps the game's frame cap.** The cap was taken from the panel as it was when
  it opened, so a profile that set 60 FPS a few seconds later was saved as "no FPS cap"; with the GPU
  agent silent the stored cap was erased. The cap is now read when you press Save (what GPD Forge has
  asked the driver for, else what the driver holds), and kept as stored when it cannot be read. The
  mode for a new rule is read at the press too, not the one in force when the panel opened.
- **The overlay's cap row and TDP stepper follow a game profile.** When a profile went on or off
  after the overlay opened, the cap row still said Off under "60 FPS" and the stepper still showed the
  preset under the game's watts — so + stepped down, to a manual value below what the device ran at.
- **A Radeon setting the driver does not take is no longer reported as applied.** Anti-Lag or Chill
  the driver does not support is skipped at apply, with the reason; and the frame cap, Anti-Lag and
  Chill leave the notice's "applied" when the GPU agent's report shows the driver holding something
  else, or when the agent stops reporting — they said "applied" for as long as the game ran.
- **The overlay shows why a profile setting was not applied, whole.** The reason was cut off with an
  ellipsis in the 380 px window and only readable in a tooltip a pad cannot open; with something not
  applied the line now wraps and is marked as a warning.
- **A game profile no longer wipes a TDP you set by hand.** Leaving a game for another app in the same
  mode, or editing its profile mid-game, ended the manual override and wrote the preset or the game's
  watts. The override now lasts until the mode changes, as it always should have; while it is set,
  a game coming or going writes nothing.
- **The profile notice only says what is true.** A rule that only picks a mode (the seeded `steam`)
  is no longer announced as "Profile steam applied: mode settings". The fan is reported as not
  applied while fan control is off, and the frame cap and Anti-Lag / Chill while Radeon control is
  off (a default install, without `-EnableGpuProfiles`) or the GPU agent reports it unavailable. The
  watts are reported as held while another power controller keeps TDP (named) or the thermal
  guardian throttles below them. And what you change mid-game — TDP, fan, cap — is shown as "changed
  by you" instead of the overlay's line still claiming the profile's values beside controls showing
  yours.
- **Leaving a game puts the frame cap back safely.** It could restore a cap below an auto-FPS target
  switched on mid-game (the pairing every other path refuses; the cap is turned off instead), and it
  turned off your own Adrenalin cap when the GPU agent had not reported yet at the game's start (the
  value is now read from the agent's first report, or the request is withdrawn).
- **One failed read no longer flips the Radeon settings.** A non-2xx from `/gpu/desired` made the GPU
  agent write the mode's Anti-Lag / Chill over the game's and the game's back on the next tick; an
  unreadable desired state now skips that tick.
- **The Games page lists games only.** The compositor, browsers and launchers present frames too, and
  on the device `dwm.exe` headed the list as a game to profile. `GET /sessions/games` leaves the known
  non-game presenters out (their sessions stay in `GET /sessions`), and the profile editor now shows
  the game's last five sessions — when, how long, FPS and 1 % low.
- **The open game card's small labels meet AA contrast** in both themes (they measured 4.26:1 and
  4.38:1 on its accent tint); the contrast check now measures that tint and the overlay's profile line.
- **The first touchpad click after using the gamepad did nothing.** A mouse press switched the UI from
  pad to mouse density on `pointerdown`, which shrank every control (the rail's entries from 45 to
  37 px) before the release — so the release landed on a different element and the browser dropped
  the click. Density now switches after the click. Found adding the Games page, whose shoulder-button
  test clicked the rail with a pad connected.
- **Picking a mode no longer lifts a hot device out of a guardian throttle.** `POST /mode` (and an
  auto-profile switch) wrote the mode's preset even while the thermal guardian held a lower ceiling,
  and the guardian re-asserted it only up to 30 s later. The apply now reports `HeldByGuardian` and
  writes nothing; the ceiling, computed under the new mode, stays, and the mode's TDP comes back when
  the throttle clears.
- **`/app-rules` refusals carry a `code`** beside the sentence the UI shows (`bad_rule`, or the
  override field's code). A wrong JSON type is a coded 400 naming the field, not a framework error.
- **`GET /fan`'s contract lists Quiet, Balanced and Aggressive.** `POST /fan` always accepted them and
  `/panic` sets Aggressive, but the contract only allowed Auto / Manual / Curve.
- **The E2E suite no longer fails a random handful of tests one run in three.** The cause was not
  the mock, the UI or the tests: on the dev handheld, Windows stops completing *new* connections to
  127.0.0.1 — every listener at once, for 10–35 s — after a burst of roughly a thousand of them, and
  a fresh browser context per test opened ~600 to the mock and ~400 to the preview server per run.
  Node reported the dropped SYN as `connect ETIMEDOUT` after ~310 ms; Chromium as a shell that never
  rendered. The suite now reuses one browser context per worker (Playwright's `reuseContext`, reset
  between tests) and opens ~18 connections a run instead of ~1,000; 184/184 three runs in a row, in
  1.3 min instead of 2–3. `connection-churn.spec.ts` fails if the churn comes back. Video recording
  is off, because any video mode silently disables context reuse; failures still get a screenshot.
- **Opening the overlay over a game no longer switches the mode.** Now that the session agent reports
  the real foreground, the overlay — an Edge window — became the app auto-profiles judged, so about
  4.5 s after opening it over a game a rule had put in `gaming` the mode flipped to `windows`, wrote
  its preset and threw away the TDP just set with the overlay's own stepper; closing it flipped back.
  A known non-game window (the overlay, GPD Forge, the shell, Steam's overlay) now leaves the game
  underneath deciding for as long as that game is running.
- **Steam in front switches to `gaming` again.** The overlay fix above held whatever app had been in
  front before — notepad, a terminal — and replaced every listed non-game window with it, so opening
  Steam or Big Picture with notepad still open stayed in `windows`, and a rule of your own on a
  browser or Discord was ignored the same way. A window a rule names now decides for itself, and only
  an app a rule names is held under the overlay.
- **A TDP the firmware refuses is no longer rewritten every 30 s.** After `verified: false` (asking
  35 W of a firmware that caps at 30, say) the reassert read the same refusal on every check and ran
  the whole closed loop again — four applies and ~9 s of retries each time, stalling the thermal
  guardian's loop and holding up `POST /tdp`, `/mode` and `/panic` behind it. A refused limit is now
  left alone until the readback changes; then it is tried once more.
- **Auto-profiles follow the power source again after a restart.** Every mode switch was saved as if
  you had picked it, so a `battery` auto-profiles chose unplugged came back after a boot on AC — 8 W
  on the charger, even under an unruled game — until a ruled app or pulling the charger. Only a mode
  you pick is restored now; after an automatic switch the daemon starts in `windows` and auto-profiles
  decide from what is true at the new start.
- **The overlay says Offline when the daemon stops answering.** A failed poll kept the last good
  reading, whose age was fresh when the daemon served it and never aged afterwards, so a crashed
  daemon — and with it the guardian, the fan loop and TDP control — left a green "live" dot over
  frozen numbers for as long as the overlay stayed open. After 3 s without an answer the overlay now
  shows "Offline · N s ago", turns the dot off and dims the numbers.
- **Reinstalling without `-EnableGpuProfiles` really opts out.** The installer now always starts the
  session agent, and that agent inherits the installer's environment. An elevated shell opened after
  an earlier opt-in install still carried the gate, so the agent went on applying Radeon profiles
  until logoff; the installer now clears the gate from its own process too, not only machine-wide.
- **A PM-table row that ignores writes no longer fails every TDP write.** The slow limit and Tctl
  are judged since the reassert learned to read them, but neither row has ever been seen on the
  HX 370. A firmware that printed a fixed Tctl would have made every write — manual ones included —
  unverified after four retries, and the 30 s reassert would have rewritten the limits every check.
  Each row is now judged until the first write it follows, and dropped from the judgement, with a
  warning in the service log, after a write it never once followed while STAPM and fast held.
- **Starting beside MotionAssistant or GPD Tool no longer loses the mode.** The startup apply yields
  to them, and nothing applied the mode once they exited — until you picked a mode again. The 30 s
  reassert now applies it, as it already did for a mode switch that yielded mid-session.
- **`/history` records every sample.** It was filled by the TDP worker's tick, which takes only the
  newest sample and waits on `ryzenadj` writes, so under auto-FPS or a tuner sweep samples went
  missing and the rate the history showed fell below the sampler's real 1 Hz. The sampler now feeds
  the history and the session tracker directly.
- **A reading the daemon never took says so.** When the first hardware read hangs, `GET /telemetry`
  answers with an all-null placeholder that claimed a known power source, so the panel, the overlay
  and the MCP tool showed a live "Battery --%". It is now `acKnown: false`; the panel and the overlay
  show "No reading yet", the power source reads unknown, and `get_telemetry` reports
  `unsampled: true`.
- **The TDP controls show what is in force.** The Dashboard slider re-reads the TDP after you switch
  mode (it kept showing the manual value the switch had ended), shows a 36–40 W override as it is
  instead of clamping it to 35 W (the slider now spans the daemon's 5–40 W manual band, like the
  overlay), and when it could not read the TDP at open it says so and retries instead of staying
  disabled at "--". The overlay stepper no longer opens on a made-up 20 W. Both "verified" marks
  follow telemetry after a write, so a limit the firmware reverted and the reassert could not hold
  stops saying "verified". A mode switch that fails in the overlay is reported and undone instead of
  showing the new mode's TDP as applied.
- **A sensor's own timeout no longer stops the daemon.** A cancellation raised inside a telemetry
  read was re-thrown as if the service were shutting down, which ends the host — guardian, fan and
  TDP with it. It is now a failed read: logged once, last sample kept.
- **PresentMon cannot be started twice.** The frame probe is read from more than one thread, and two
  readers that both saw PresentMon exit could each start one; the extra one was never stopped.
- **A restart keeps your mode.** The daemon applies the active mode's TDP when it starts, but the
  active mode was held only in memory and began as `windows` — so rebooting while in `gaming`
  actively wrote windows' 15/20/17 W. The mode you pick is now saved and is what the next start
  applies. Auto-profiles keep it too: they used to count the first foreground with no rule as a
  switch away from the restored mode, so about 4.5 s after the start they wrote windows' TDP anyway
  and saved `windows` over your pick. Now only a real change — a ruled app coming to the front, the
  power source flipping — switches.
- **The TDP reassert also catches a slow limit or Tctl put back behind its back.** It compared only
  STAPM and the fast limit; `ryzenadj --info` prints the slow limit and Tctl too, and they are now
  judged whenever the table has them — by the same rule that decides whether any write verified.
- **The reassert's readback no longer runs beside another TDP write.** Its `ryzenadj --info` ran
  outside the write gate, so a Dashboard or mode-switch write arriving mid-read started a second
  ryzenadj on the SMU mailbox at the same moment. Read, compare and re-apply are now one step under
  the gate.
- **A manual TDP no longer outlives a mode switch that raced it.** A `POST /mode` landing while a
  `POST /tdp` waited its turn wrote the new mode, and then the manual write — built for the old
  mode — landed on top and was kept every 30 s while the app said there was no override. The manual
  write now stands down and answers `409 tdp_superseded`.
- **A power source the daemon could not read is shown as unknown.** A failed battery query reached
  the top bar as "Battery --%" on a plugged-in machine. `GET /telemetry` now carries `acKnown`; the
  pill reads "Power --", the Profiles page says rule switching is paused, and `GET /health/check`
  reports `ac_unknown`.
- **The Dashboard's TDP never shows a made-up 20 W.** With nothing written yet (the startup apply
  yielded to MotionAssistant or GPD Tool) or `GET /tdp` failing, the slider sat on a hardcoded 20 W
  as if it were in force, while the overlay showed the mode preset. It now opens on the same value
  the overlay does, and on "--" (disabled) when nothing is known.
- **FPS reads the game even when the foreground is unknown.** Without a foreground (the service in
  session 0 with no agent reporting), a Steam game that no rule names lost to `steamwebhelper`,
  which the shipped `steam` rule matches — the reading was Steam's UI or nothing, and auto-FPS went
  idle. Known non-game presenters (the compositor, browsers, launchers, overlays, GPD Forge itself)
  can no longer be picked through a rule, and with the foreground unknown the busiest app that could
  be a game is read.
- **The 30 s TDP reassert no longer puts back a mode you left.** A mode switch made while
  MotionAssistant or GPD Tool ran wrote nothing (it yields), so the last write was still the
  previous mode's; once the rival exited the reassert re-applied it while the app showed the new
  mode — and brought back a manual override the switch had ended. It now applies the mode you chose.
- **TDP writes happen one at a time.** The Dashboard, the overlay, mode switches, panic and the
  resume restore each wrote from their own thread, and two closed loops overlapping fought retry by
  retry. The reassert could also overwrite a `POST /tdp` that was still being verified, because it
  only noticed writes that had finished; it now stands down for any write started or finished
  during its check.
- **A failed battery query no longer switches the mode.** Its "on battery" fallback was cached for
  5 s and read as an unplug, so one WMI glitch on a plugged-in machine switched to the battery
  profile and back, ending any manual TDP. The AC/battery switch now skips a reading whose AC state
  is unknown, and the failure is a warning in the service log instead of a debug line.
- **The TDP controls open on the value in force.** The Dashboard started at a hardcoded 20 W and the
  overlay at a preset while a remembered manual value applied; both now read `GET /tdp`. A refused
  or failed write shows an error and puts the control back, instead of being swallowed. The
  Dashboard's badge reads "unknown" when nothing has been verified yet (it said "verified"), and an
  unreadable readback no longer blanks the overlay's stepper.
- **A hand-set TDP survives a hot spell.** When the thermal guardian stopped throttling it restored
  the mode's preset, so a manual 20 W came back as 15/20/17 W with nothing saying why; and the
  throttle ceiling was built on the preset, so a manual 10 W could be raised to the 12 W throttle
  floor. The manual value is now an override until the mode changes: the guardian and charge-guard
  restores, the resume restore and the 30 s reassert put it back, and a throttle never exceeds it.
- **`POST /tdp` refuses values outside 5–40 W** with `400 bad_tdp`, as `docs/api.md` always said it
  did. The daemon passed any number straight to `ryzenadj`; only the mock refused (and at 35 W,
  below the overlay's 40 W stepper — both now use 5–40).
- **No more console flashes over a fullscreen game.** Every logon shortcut the installer creates
  (tray, hotkeys, GPU agent) and the overlay hotkey's launch are now hosted by `conhost.exe
  --headless` instead of launching `powershell.exe`/`dotnet.exe` directly. A console program draws
  its window before `-WindowStyle Hidden` can hide it, and that one frame was enough to pull a game
  out of fullscreen. The GPU agent no longer keeps a minimised console open for the whole session.
- **The Tauri shell spawns the daemon with `CREATE_NO_WINDOW`**, so opening the app while the
  service is down no longer leaves a `dotnet` console behind.
- **The hotkey logon shortcuts pointed at a path that does not exist**: `WindowsPowerShell\v1.0`
  had become `WindowsPowerShell<0x0B>1.0` (a `\v` read as a vertical tab). A test now rejects
  control bytes in any script.
- **The fan curve no longer hunts.** The temperature it reacts to is now an exponential average
  weighted by real elapsed time (τ 3 s heating, 10 s cooling) instead of a 4-sample average that
  one hot Tctl tick could still move, and whose window stretched whenever a TDP apply stalled the
  loop. After the curve, a new `FanDutyRamp` limits how fast the duty moves (up ~10 %/s, down
  ~2.4 %/s) and holds 8 s after any increase before it may fall, so a target wobbling around a
  level gives a steady fan. One missed sensor read (up to 3 s) no longer hands the fan to firmware
  and back, which used to end in a full-speed burst.
- **EC access is atomic.** Addressing an EC cell takes five port writes, and the fan controller,
  the RPM reader and LibreHardwareMonitor all drive the same Super I/O ports. Each access now holds
  an in-process lock and the machine-wide `Access_ISABUS.HTP.Method` mutex those tools honour.
- **A thermal throttle could raise power.** Its ramp starts at an absolute 25 W chosen for
  `gaming`, and it was applied flat with Tctl 96 whatever the mode: in `windows` (15/20/17 W,
  Tctl 92) it applied 25/25/25 W at Tctl 96 to a device that was already hot (seen in `/audit` on
  2026-09-24). Every limit is now the lower of the throttle and the active mode's.
- **The guardian no longer throttles on a single Tctl spike.** The throttle band reacts to a 2 s
  time-weighted average instead of the raw reading, so one tick past 90 °C neither flattens the
  boost limits nor holds them until the raw reading happens to dip. A sustained excursion still
  throttles within seconds, and the critical limit (96 °C) still reacts to the raw reading.
- **A steady throttle no longer re-runs ryzenadj every tick.** Under a 5-minute full load the
  ceiling flipped 23 ↔ 24 W each tick and every flip was an apply, stretching the worker loop to
  ~1.7 s. While throttling, the ceiling now moves only in steps of ≥ 3 W (about 1.4 °C, above
  Tctl's own wobble), is never raised within 10 s of its last change, and an unchanged ceiling is
  re-asserted every 30 s rather than every tick. A second load run with only a 2 W step still
  bounced 23 ↔ 25 W every few seconds; the step and dwell are sized from it.
- **The fan preference survives a restart.** It lived only in memory, so every reboot, service
  restart or reinstall silently handed the fan back to firmware. `POST /fan` and
  `/settings/import` now save it to `%ProgramData%\GPD Forge\fan.json`, read back and validated at
  startup. `/panic`'s Aggressive is deliberately not saved: an emergency is not a preference.
- **The CPU clock is real.** `cpuClockMhz` was Windows' `CurrentClockSpeed`, which on the HX 370
  is the fixed 2000 MHz base clock: it said 2000 at idle and 2000 at full boost. It is now
  LibreHardwareMonitor's average effective core clock. Without the hardware gate the Windows value
  is still consulted, but shown only once it has been seen to change — a number that never moves is
  now "n/a" rather than a reading.
- **FPS is the game's, not the busiest window's.** PresentMon traces every process, and the reading
  went to whichever presented the most frames in the last 2 s — the compositor, a browser, a 144 Hz
  overlay — whatever was in the foreground. It now follows the foreground app when that app is
  presenting, else an app your rules name (the game rather than `steamwebhelper`, which the shipped
  `steam` rule also matches), else the game it was already reading, which keeps the FPS on the game
  while the overlay or the Steam menu has focus. With the foreground known and nothing matching it
  is "n/a", never the busiest app. A browser, launcher or overlay in front never takes the reading
  from a game still rendering: the Ctrl+Alt+Home overlay is an Edge window that repaints once a
  second, and opening it over a game read ~1 fps — which auto-FPS answered by raising the TDP.
- **Frames are timed by PresentMon's own clock.** They were stamped when their line reached the
  daemon, and PresentMon's output arrives in bursts, so a flush looked like hundreds of frames at
  once. Each row's own time (`TimeInMs` in the bundled 2.5.1, `TimeInSeconds` in 1.x) now places it.
  A row whose `FrameTime` is `NA` falls back to `MsBetweenPresents` instead of being dropped, and
  `NA` in columns the daemon does not read no longer matters. The daemon also keeps the target's
  last 10 s of frame times, which the frame-pacing work will build on.

## [0.3.0] — 2026-09-01

If 0.2.0 was about the app no longer claiming things it had not verified, this one is about the app
knowing the difference between a measurement and a zero — and about the tests being able to tell.

### The headline
- **Battery health**: how much of the pack's factory capacity survives, sampled once a day so
  degradation is a trend rather than a reading. On the reference device, 40,009 of 43,890 mWh — 91.2 %.
- **A charge guard** that counts the hours spent plugged in and full, and can hold a cooler ceiling
  while that happens. It cannot stop charging, says so, and now has the evidence to back it.
- **`gaming-battery`**: a fifth mode, frame-capped at 45 and cooler, for the longest session away
  from a charger.
- **A sensor with no reading is `null`, not `0`.** The last place in the app that still invented a
  number when it could not measure one.
- **One API contract, checked from both sides** — the real daemon and the mock are each validated
  against the same file, never against each other.

### What is honest about it
Two of this release's features are refusals with evidence attached. The charge threshold does not
exist on this board and [ADR-0004](docs/adr/0004-no-charge-threshold-on-this-board.md) says why, in
four independent read-only findings. Cycle count and cell temperature report `null` with a stated
reason rather than the `0` the EC hands over. And the frames-per-watt claim for `gaming-battery` is
labelled in the roadmap as arithmetic rather than evidence, because nobody has run the game yet.

### Added
- **`GET /battery/health`** — designed vs full-charge capacity, health percentage, and a degradation
  trend from daily samples. Cycle count and cell temperature are `null` with reasons: this EC returns
  0 cycles for a pack that has demonstrably lost 8.8 %, and printing that would put "0 cycles" beside
  "91 % health" for the user to reconcile. Design capacity is not exposed over WMI at all here, so it
  comes from `powercfg /batteryreport` once and is cached — it is a factory constant.
- **`GET`/`POST /battery/charge-guard`** — hours at high state of charge, an alert once per episode,
  and an opt-in cooler ceiling. `canStopCharging` is a permanent `false` **in the contract** rather
  than an omission, so no client can be built on the assumption. Lithium ages from time at high
  charge multiplied by temperature; this attacks the half that is reachable.
- **`gaming-battery` mode** — 15 W sustained, Tctl 90 (a lower ceiling means the fan spins less, and
  the fan is part of the ~9 W the system draws before the SoC does anything), Chill, and a 45 fps
  FRTC cap. The cap is the larger lever: an uncapped game turns every watt it is allowed into frames
  nobody sees, and this panel reports 60 Hz with no other supported mode. Deliberately not auto-FPS
  eligible — a cap below an active target is the one pathological pairing.
- **`core/Profiles/Modes.cs`** — one catalogue for what a mode is. Modes had been enumerated in seven
  places that did not know about each other; `ModeCatalogueTests` now fails the build when they
  diverge, including across the TypeScript and mock-daemon boundaries.
- **`tests/contract/api-contract.json`** plus guards on both sides. A route the daemon gains but
  nobody declares fails the C# check; declaring it then fails the mock check until the mock
  implements it.
- **`tests/desktop/`** — the packaged shell driven through Windows UI Automation: the window opens,
  the title states whether the daemon is reachable, a webview is mounted, and closing hides to tray
  instead of exiting.
- **`GPDFORGE_DATA_DIR`** — run the daemon against isolated state.

### Fixed
- **Telemetry reports `null` for a sensor it cannot read.** With the hardware gate closed the daemon
  used to report `cpuTempC: 0, packageW: 0, fanRpm: 0`. A **measured** zero is still reported as
  zero, so the distinction is per-field: `dischargeW: 0` on AC is true, and `cpuClockMhz` is a real
  WMI reading.
- **The thermal guardian says when it cannot see.** This is the dangerous half of that change: in C#
  `null >= 90` is false, so every threshold would have gone on compiling and quietly deciding "not
  hot" — and the release path is a comparison too, so a throttle already applied would never have
  been cleared. It now holds an existing throttle and reports that it is holding one it cannot verify.
- **`GET /health/check` warns instead of answering `ok`** for a machine whose CPU it cannot read.
- **A failed battery read no longer announces an emergency.** It returned 0, and the guardian raises
  CRITICAL below 8 %.
- **The CSV export writes an empty cell, not a zero**, for an unmeasured sensor. That file outlives
  the session, and a column of zeros plots a CPU at 0 °C.
- **The test suite no longer writes to the installed service's state.** `ApiStartupTests` ran against
  `%ProgramData%\GPD Forge`, so every run read and wrote the machine's real alerts and sessions — and
  its shape checks silently verified nothing on a clean runner, because the arrays were empty.
- **`/audit`, `/firmware` and `/standby/hibernate`** shipped in 0.2.0 with no mock behind them, so no
  E2E test could reach them. Found by the new contract guard on its first run.
- **The release workflow takes one version's section** of the changelog rather than the whole file.

### Changed
- The overlay's mode grid wraps to three-and-three. Six modes in one row on a handheld puts every
  target below the thumb minimum, and that surface is gamepad-first.
- `docs/ROADMAP.md` reconciled against the tree. Phase 6 had called itself "the single biggest
  blocker in the project" while every item in it had shipped.
- Decisions that constrain future work now live in [`docs/adr/`](docs/adr/README.md).

## [0.2.0] — 2026-08-30

First release with the daemon doing real work on real hardware. The theme, if there is one, is that
the app stopped claiming things it had not verified.

### The headline
- **AMD Radeon profiles** — Anti-Lag, Chill, Boost, Image Sharpening and the driver's own frame-rate
  cap, applied automatically per mode (and therefore per app, through the existing rules). Reached
  through ADLX's C interface with no native shim, verified against the driver on every read.
- **A real frame cap** (FRTC), distinct from the auto-TDP target it used to be mislabelled as.
- **The window closes to the tray** instead of exiting — it is a controller's UI, and the daemon keeps
  working either way.
- **Hibernate policy**, because this board has no S1/S2/S3 and Modern Standby is what drains it.
- **A version model with one source of truth**, and an About card that says when the shell and daemon
  are from different builds.
- **An audit log of every hardware write**, with three-valued verification: written, refused, or
  could-not-be-confirmed.

### What is honest about it
Several things this release does are refusals. `/firmware` reports and will not flash. The controller
config write path is blocked on a measured fact — none of the pad's HID interfaces expose feature
reports — rather than shipped on a guess. GPU profiles report `available:false` with a reason rather
than a switch that does nothing. Where a value cannot be measured it is `null`, never a plausible
substitute.


### Added
- **A version model with one source of truth, and `GET /version`.** The version used to be a
  hand-typed literal in four independent places, with nothing keeping them equal and nothing failing
  when they drifted. One of those places was `UpdateService`'s `currentVersion` **default parameter** —
  and DI took the default, so the daemon compared every GitHub release against a constant nobody would
  ever bump. It would have kept offering an update that was already installed. Every unit test passed
  a version explicitly; production was the sole caller taking the default, which is the worst possible
  place for that mistake to hide.

  `<GpdForgeVersion>` in `Directory.Build.props` is now the only declaration. It feeds the assembly;
  `/health` and the new `/version` read the assembly; `UpdateService` *requires* the version, so
  forgetting to supply it is a compile error rather than a wrong answer. `ui/package.json` and
  `ui/src-tauri/tauri.conf.json` keep copies because npm and Tauri each demand their own field, and
  `VersionModelTests` asserts all three equal — drift is a failing build, not a slow surprise.

  `/version` also reports the commit and the build timestamp, both **nullable and null when the build
  did not record them**. Deterministic builds put a content hash in the PE timestamp field, which read
  as unix seconds yields a confident, plausible, wrong date; implausible values are rejected rather
  than shipped as a date, because the entire value of that field is that it can be trusted.

- **Settings ▸ About now shows the shell build, the daemon build, and says when they disagree.** This
  is the point of the whole change. On 2026-08-28 the app showed no telemetry while the daemon was
  healthy the entire time — the shell in Program Files predated the commit that fixed it, and
  establishing that took diffing the installed binary against a fresh build hunting for marker
  strings. Nothing on screen could say which build was on screen. Now it can, and agreement stays
  silent so the warning keeps its meaning.

### Changed
- **The AI mode's sustained power shaping is now enforced instead of merely calculated.**
  `ProfileShaper` collapses fast/slow boost onto one flat ceiling for a good reason — boost above
  sustained STAPM buys no throughput once a workload is continuously CPU-bound, it only adds heat,
  fan noise and thermal cycling. It had existed, been unit-tested, and been called from exactly one
  place: `GET /ai`, where its result was rendered and thrown away. The profile that actually reached
  the silicon came straight from the preset map.

  Nothing looked wrong, because the default AI preset is written flat by hand. But nothing was
  *keeping* it flat: `ModeProfiles.Set` clamped ranges without flattening, so a single
  `POST /profiles/ai` put the boost headroom back and the shaper was not in the path to stop it.

  Shaping now lives in `ModeProfiles.For`/`Set` — the point where all six callers converge — so the
  guarantee covers the mode switch, the auto-profile worker, the standby restore and the resume
  worker at once, rather than one call site. It flattens on the way in as well as out, so
  `GET /profiles` cannot report a boost that will never be applied. The user still sets the sustained
  ceiling; only the headroom above it is removed, and `POST` answers with `sustained: true` so a
  client can say why the numbers came back changed.

  The Power page no longer renders fast/slow sliders for this mode. Two controls a user can drag
  that change nothing are worse than their absence, so they are replaced by a sentence explaining
  the trade — and the sliders re-seed from the daemon's reply rather than from what was posted.

### Fixed
- **A Smart App Control block on the service DLL uninstalled the daemon instead of failing the
  publish.** SAC judges each unsigned binary individually and inconsistently — on 2026-08-29 the same
  source produced a build it allowed and, minutes later, one it refused. A refused *service* binary
  does not fail `dotnet publish`; it fails `Start-Service` six steps later with an error naming no
  cause, and by then step 1 has already unregistered the service and overwritten the binary that
  worked. `update-shell.ps1` has verified the shell this way since it existed; the service had no
  such guard, which is precisely the failure that hit.

  `install-gpd-forge.ps1` now loads the published assembly before continuing, and on a block rebuilds
  with `-p:Deterministic=false` — a plain retry is useless, because a deterministic build reproduces
  the identical hash and therefore the identical verdict. Three details the first two attempts at this
  guard got wrong, each of which made it silently useless:
  - `Start-Process -FilePath 'dotnet'` does not resolve a bare command name through `PATH` the way the
    call operator does. The launch failed, the failure was caught, and "could not run the test" was
    indistinguishable from "the test passed" — so a blocked binary sailed through.
  - Only `0x800711C7` counts as blocked. Any other non-zero exit means the assembly *loaded* and
    failed for an unrelated reason, and rebuilding over that would hide a real fault behind a retry.
  - The check is retried: SAC's verdict on a freshly written binary is a cloud lookup, and the same
    file can be refused on one load and accepted seconds later.

### Added
- **The resume restore's last empty step now does something.** `hid` has reported
  `restored: false — no backend yet` since the Standby Doctor shipped; it now re-enumerates the
  controller (`core/Hid/HidReenumerator.cs`).

  The interesting constraint is what it must *not* do. Restarting the pad on every wake would yank a
  working controller out from under a running game — worse than the fault being repaired — so it acts
  only on a node Windows itself reports faulted (`ConfigManagerErrorCode != 0`) and otherwise reports
  success *because* it did nothing. When it does act it restarts the USB composite parent: confirmed
  on hardware, the pad presents as **seven** PnP nodes (VID_2F24 & PID_0135 — which also settles the
  `// verify on HX370 Win 4` TODO left in `GpdButtonMap`), and one parent restart re-enumerates all of
  them. Afterwards it re-reads the device, because `pnputil` exits cleanly for a restart that left the
  node exactly as faulted as it was.

  Device identity comes from `PNPDeviceID` and `ConfigManagerErrorCode` — an ID and a number. Nothing
  keys on device names or status text: on this machine the pad is called "Dispositivo definido por el
  proveedor compatible con HID", and a name-matching implementation would report a missing controller
  on any non-English Windows, which is the same trap that produced six phantom sleep blockers.

  `pnputil` was chosen over SetupAPI/CfgMgr32 P/Invoke: an in-box command keeps the layer testable
  behind `IProcessRunner`, and a resume path is the last place to hand-roll native device calls.
- **The sleep study findings now reach the panel.** Parsing them was only half the job: until now the
  only way to see that the machine had hibernated and never come back was `--probe-sleepstudy` from a
  console, which is not where anyone looks after power-cycling a handheld by hand. `GET /standby`
  gains `sleepStudy` + `sleepStudyError`, and the Standby Doctor panel renders failed resumes,
  bugcheck stop codes and the worst measurable drain.

  The report is **never generated on the request path** — it costs tens of seconds and ~9 MB.
  `SleepStudyWorker` samples it two minutes after start (not at start: the daemon comes up with the
  machine, and generating a sleep study while Windows is still starting services would compete with
  the boot it exists to observe) and then every 12 h, into a cache the endpoint reads.

  The wire format carries three states that clients must not collapse: `sleepStudy` and
  `sleepStudyError` both null means the sampler has not run yet; an error means powercfg refused (it
  needs elevation); a summary with no findings means it ran and found nothing. Treating a refusal as
  a clean report would tell the user their machine is healthy on no evidence at all.

### Fixed
- **An absent `sleepStudy` field rendered as a failure with no reason.** The panel tested
  `sleepStudyError !== null`, and a daemon predating these fields omits them entirely — `undefined
  !== null` is true, so an older build produced an empty "unavailable" badge instead of "not sampled
  yet". Caught by the visual baseline, not by the DOM assertions: the E2E asserting on findings
  passed the whole time, because it ran against the mock daemon while the visual spec's own stub
  still had the old shape.
- **`powercfg /sleepstudy` is parsed, so the Standby Doctor can finally explain a machine that went
  to sleep and never came back** (`core/Standby/SleepStudy.cs`). On the reference Win 4 the System
  event log had recorded *no* standby transition at all for the night in question, while the sleep
  study held the whole session, a `0x133` DPC_WATCHDOG_VIOLATION bugcheck two days earlier, and every
  abnormal shutdown of the week.

  The report defeats the obvious implementation three times over. Its `<table>` elements are
  client-side templates full of `${$Scope.Foo}` placeholders, so scraping the HTML returns the
  scaffolding and none of the data — everything lives in one `var LocalSprData = {…}` blob (whose
  keys stay English on a localised Windows, because the markup is the part that gets translated).
  That blob is a *JavaScript object literal*, not JSON: a handful of values are single-quoted
  (`{"Value":'0x0'}`), which `System.Text.Json` rejects — and the first one sits ~180 KB in, so a
  JSON-only parser passes a short fixture and dies on a real report. The payload is therefore
  extracted by a string-aware scanner that normalises single-quoted literals as it goes, which also
  keeps a brace inside a process name or path from ending the scan early.

  Third and least obvious: **battery drain is not meaningful for every session type.** The report's
  own script restricts discharge to Active/Screen-Off/Modern-Sleep sessions with both full-charge
  readings present, and those rules are mirrored here rather than reinvented. Subtracting the
  capacities of a Hibernate session yields a confident four-figure milliwatt number that means
  nothing: the machine is off, an exit capacity of 0 alongside a full-charge capacity of 0 is the
  *absence* of a reading rather than an empty battery, and the session is timestamped to when the
  user pressed power, not to when the machine stopped drawing.

  What it reports instead is what a user actually asks: bugcheck stop codes, abnormal shutdowns, and
  **failed resumes** — a suspend immediately followed by an abnormal shutdown. That last one is an
  inference from adjacency rather than a field the report provides, and is documented as such; it is
  also what distinguishes "it slept and never woke up" from "it crashed while I was using it".
  Exposed as `--probe-sleepstudy [report.html]`, which re-reads an existing report so the parser can
  be exercised against a real multi-megabyte one without elevation.

### Fixed
- **Six imaginary sleep blockers on every non-English Windows.** `powercfg /requests` prints a
  "nothing here" sentinel under each category, and the parser skipped only the English literal
  `"None."` — so a Spanish install reported `Ninguna.` six times as six reasons the machine could not
  sleep. Matching the translated word instead would have been a lottery per language: powercfg
  localises that sentinel while localising the category headers only *inconsistently* (the same
  machine prints `DISPLAY:`, `SYSTEM:` and `AWAYMODE:` in English but `EJECUCIÓN:` in Spanish). The
  sentinel is now recognised structurally — it is a lone word, whereas every real request is a tag
  plus a driver name, service name or path and so contains whitespace. That test direction also fails
  safe: an unfamiliar line is reported rather than swallowed, so the worst case is one blocker too
  many, never a hidden one.

### Added
- **Waking the machine now restores the fan and the power limits by itself.** `RestoreAsync` has
  existed since the Standby Doctor landed and the only thing that ever called it was a human pressing
  a button on the Standby panel — the wrong shape for the failure it prevents, since the EC comes back
  from a suspend uninitialised whether or not anyone is looking, and re-applying power limits against
  an uninitialised EC is how the Win 4 ends up hot and silent. A resume is now detected and repaired
  without a human (`core/Standby/ResumeRestoreWorker.cs`).

  The resume is detected from clock divergence, not from `WM_POWERBROADCAST`: a Windows Service has
  no message pump, and receiving a power broadcast would mean hosting a hidden window purely to be
  told something two clock reads already prove. `QueryUnbiasedInterruptTime` does not advance while
  the system is suspended — including S0ix, where `TickCount64` keeps counting — so wall-clock delta
  minus unbiased delta is time genuinely spent asleep.

  `ResumeDetector` deliberately does **not** reuse `StandbyDrainTracker`, which already computes the
  same difference. Its gates are correct for a drain figure and wrong for a restore: it ignores
  anything under 15 minutes, anything on the charger, and anything where the battery did not drop,
  and the hardware needs restoring in all three of those cases. Sharing the type would have produced
  a restore that silently skipped short sleeps and every resume on mains — so a test asserts the
  detector takes no AC or battery input at all.

  The floor is 60 s of observed sleep (Modern Standby dips in and out for seconds at a time and
  re-initialising the EC on each would be a write storm), and the poll is 5 s rather than the drain
  sampler's minute — the resolution that matters here is how long the machine runs uninitialised
  after waking. HID re-enumeration still has no backend and continues to report `restored: false`
  with the reason. If `QueryUnbiasedInterruptTime` is unavailable the worker says so once and stops
  instead of polling forever to learn nothing; `POST /standby/restore` still works.

### Changed
- **The UI no longer presents controls that cannot reach the hardware as if they could.** LED/RGB,
  the battery charge limit, undervolt/Curve Optimizer and the keyboard backlight all store a setting
  and return `applied: false`, because this HX370's firmware accepts no write on the EC/HID paths
  they need. They used to sit inline on the Power and Display pages among controls that really do
  change the machine, with nothing to tell them apart — which is what made the app feel like a
  mock-up. They now live on a new **Hardware** page beside a capability report that states, per
  feature, what blocks it and what would unblock it, read live from the daemon.
- The **Controller** section is gone. It was a top-level page consisting entirely of disabled
  sliders advertising a feature with no daemon endpoint at all; it is now one honest line on the
  Hardware page. The on-screen-display and GPU placeholders moved there too.
- `GET /health` finally has a consumer. It has been served since the first release and no client
  ever called it, so the app could not tell you which daemon build it was talking to or which board
  had been detected. Both now appear on the Hardware page.
- `ui/src/pages.tsx` (856 lines) is now `ui/src/pages/`, one file per page.

### Fixed
- **One bad field killed the whole app.** `AlertSeverity`/`AlertCategory` are C# enums, and without
  `JsonStringEnumConverter` they went out as ordinals (`"severity":1`) while `docs/api.md`, the mock
  daemon and `ui/src/types.ts` all specify names (`"Aviso"`). The Alerts page called
  `severity.toLowerCase()` on a number, React threw during render, and with no error boundary the
  entire tree unmounted: the window went blank and *nothing* was clickable — which reads as "the app
  does nothing", not "one page is broken". The daemon now serializes enum names, an `ErrorBoundary`
  scopes any future panel failure to that panel, and `AlertsPage` coerces the field defensively.
  Covered by `core.tests/AlertWireFormatTests.cs`, including a test that fails if the converter is
  ever removed as redundant.
- **Reinstalling silently closed the fan gate.** Fan writes need `GPDFORGE_ENABLE_FAN_CONTROL=1` on
  top of the hardware gate, and the installer never wrote it — so it wiped a gate an operator had
  opened by hand and left every fan control inert (`controllable: false`). Now set by default, with
  `-NoFanControl` to opt out.
- `scripts/update-shell.ps1` — replaces just the desktop shell, and **verifies the new binary
  actually runs before overwriting the installed one**. Smart App Control judges each unsigned build
  individually and inconsistently; the old flow could swap a working shell for a blocked one.
- **Telemetry was invisible in the native window.** The installed shell binary predated the fix that
  makes the client target the daemon absolutely; inside Tauri the origin is `http://tauri.localhost`,
  so every relative `fetch` 404'd and each tile rendered `--` with no error shown. The root cause was
  packaging, not code: `install-gpd-forge.ps1` copied whatever binary happened to sit in
  `target/release` instead of building one. It now builds the shell, stops a running instance before
  replacing it (a locked image was failing the copy silently), wipes `wwwroot` instead of layering
  stale bundles on top of each other, logs its elevated half, and refuses to finish if
  `scripts/verify-install.ps1` does not pass.
- Removed the `build:desktop` script, whose `set VAR=value &&` form appends a trailing space to the
  value in `cmd.exe` and would have produced an unusable API base. Origin detection at runtime
  (`ui/src/api.ts`) covers both the shell and the browser from one bundle.

### Added
- **Real FPS telemetry** via Intel PresentMon, behind its own `GPDFORGE_ENABLE_FPS=1` gate
  (`core/Telemetry/PresentMonFrameRateProbe.cs`). CSV columns are resolved by name so a PresentMon
  version bump cannot silently misread them; `fps1PctLow` is now populated and exported. With nothing
  presenting, FPS stays 0 meaning "not available" — never a guess. This also revives Auto-TDP-to-FPS
  and the auto-tuner, both of which were dead code behind an `fps > 0` guard that never held.
- `scripts/verify-install.ps1` — checks the service, live telemetry, that the installed shell carries
  the current markers, and that `wwwroot` has no dangling asset references.
- `scripts/fetch-presentmon.ps1` — downloads PresentMon and refuses it unless Windows reports a valid
  Authenticode signature from Intel Corporation.

### Changed
- `docs/api.md` no longer claims a production WebSocket at `/telemetry/stream`; the daemon polls only,
  and that endpoint exists in the mock alone.

## [0.1.0] — 2026-08-26

First tagged release. GPD Forge already **substitutes** MotionAssistant + GPD Tool on a GPD Win 4
(HX370 / G1618-04): it owns TDP through a verified closed loop and serves real telemetry.

### Added
- **Daemon-first architecture** — a .NET 9 Windows Service (Kestrel on `127.0.0.1:8787`) exposing a
  local HTTP API and serving the web UI. Runs under **Smart App Control** with no unsigned binary:
  hosted by the signed `dotnet.exe`, UI opened in the browser.
- **Closed-loop TDP** via RyzenAdj — apply → re-read the PM table → retry/backoff → honest `verified`
  flag (replaces MotionAssistant's blind re-apply). Verified on real HX370 hardware.
- **Conflict guard** — GPD Forge yields TDP while another controller (MA/GPD Tool) runs; takes over
  only as sole owner. `install-gpd-forge.ps1 -Substitute` stops + disables the incumbents (reversibly).
- **Real telemetry** — driverless WMI by default (battery/AC/discharge/clock/thermal); optional richer
  package-watts/temps via LibreHardwareMonitor behind `GPDFORGE_ENABLE_HARDWARE=1`.
- **Modes** — Gaming, Agents/AI, Windows, Battery, Standby Doctor, with per-mode TDP presets.
- **Web UI** — 9 pages (Dashboard/Power/Fan/Controller/Display/Profiles/Monitor/System/Settings),
  light + dark themes, live SVG sparklines, toast notifications. Browser-QA'd at the Win 4's native
  1280×800 in both themes.
- **Features** — Freezer (suspend/resume background processes, protected-list guarded), Battery budget
  (runtime + what-if projections), Auto-TDP-to-FPS (PID controller), editable presets, fan preference,
  live WMI brightness, Standby Doctor (drain diagnostics + resume restore).
- **Quick Access Menu overlay** (`/overlay.html`) — gamepad-first, right-docked; live header +
  mode/TDP/fan/FPS-cap/brightness/battery/standby. Launch via `scripts/overlay-launch.ps1` (signed
  browser app-window) + `scripts/overlay-hotkey.ps1` (resident global-hotkey listener).
- **MCP server** (`mcp/server.mjs`) — zero-dependency stdio Model Context Protocol server exposing 15
  tools (telemetry, mode, TDP, fan, auto-FPS, freezer, constraint-gated jobs, standby) so agents can
  drive the handheld. Verified end-to-end against the live service.
- **Standby Doctor** and **HID safe-writer** (backup → patch → verify → restore, anti-brick).
- Node **mock daemon** implementing the API contract, **105** core unit tests (xUnit) and **23**
  Playwright E2E, wired in CI.

### Known limitations
- **Fan RPM / curve control is parked** — the runtime EC read needs a PawnIO-capable
  LibreHardwareMonitor; the stable path is Ring0/WinRing0-only. Fan shows 0 rpm and stays on the BIOS
  auto curve until the driver decision lands.
- **Native desktop `.exe` needs code-signing** to run under Smart App Control — the service + browser
  model is the supported way today. A signing pipeline is prepared (see `docs/signing.md`).
- The scripted controller remap (`gpd-winctl.ps1`) does not work on the HX370 Win 4 (its config
  firmware differs from what pyWinControls supports) — use GPD's official WinControls there.

[0.1.0]: https://github.com/lexlaboratory/gpd-forge/releases/tag/v0.1.0
