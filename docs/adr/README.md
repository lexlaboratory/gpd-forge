# Architecture Decision Records

Durable decisions about *why* GPD Forge is shaped the way it is. This is the **history** role: if a
fact answers "why did we choose X over Y", it belongs here and everything else links to it.

The other roles live elsewhere and should not duplicate these:

| Role | Owner | Answers |
|---|---|---|
| **Map** | `docs/ROADMAP.md` | What exists, in which phase, where the code is |
| **Status** | `docs/ROADMAP.md` § *Open, blocked, and dropped* | What is open, blocked, or deliberately dropped |
| **History** | `docs/adr/` (this directory) | Why a shape was chosen, and what was rejected |
| **Changes** | `CHANGELOG.md` | What shipped, in which release |

An ADR is written when a decision **constrains future work** — not for every choice. The test: would
a future session, seeing only the code, be tempted to undo it? ADR-0002 exists precisely because
"just read one value from the daemon" looks harmless and crashes the service.

## Index

| ADR | Title | Status |
|---|---|---|
| [0001](0001-pawnio-over-winring0-for-ec-access.md) | PawnIO, not WinRing0, for EC and low-level hardware access | accepted |
| [0002](0002-adlx-runs-in-a-user-session-agent.md) | ADLX runs in a user-session agent, and the daemon never holds a handle | accepted |
| [0003](0003-firmware-assistant-reports-and-refuses.md) | The firmware assistant reports and refuses; `canAttempt` is permanently false | accepted |
| [0004](0004-no-charge-threshold-on-this-board.md) | There is no charge threshold on the G1618-04, and here is the evidence | accepted |
| [0005](0005-no-api-token-origin-allowlist-instead.md) | The local API has no token; an Origin allowlist is the boundary | accepted |

## Format

Lightweight Nygard ADRs: **Context → Decision → Alternatives considered → Consequences**. Alternatives
carry an explicit *why not*, because a rejected option with no recorded reason gets re-proposed.

Status is one of `proposed`, `accepted`, `deprecated`, or `superseded by ADR-NNNN`. Superseded ADRs
are **kept**, never edited into agreement with the new decision — the record of what was believed at
the time is the point.
