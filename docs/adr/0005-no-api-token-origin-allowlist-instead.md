# ADR-0005: The local API has no token; an Origin allowlist is the boundary

**Date**: 2026-09-02
**Status**: accepted
**Deciders**: Alex (decision), KRÓNOS (analysis)

## Context

The daemon runs as LocalSystem and binds `127.0.0.1:8787`. It can set power limits, drive the fan,
suspend processes and read a log of every hardware write it has performed. Until 2026-09-02 its CORS
policy was `AllowAnyOrigin()`, and the hole was demonstrated against the running service: a preflight
carrying `Origin: https://evil.example.com` for `POST /tdp` returned 204 with
`Access-Control-Allow-Origin: *`, and `GET /audit` answered 200 with the same header. **Any page the
user happened to be visiting could set power limits, fire a panic cool, and read this machine's
hardware audit log.**

Closing that raised the obvious next question: should the API also require a token?

`docs/api.md` had claimed one since it was written — *"Auth: bearer token for HTTP; ACL for the named
pipe"* — and no such thing has ever existed in `core/`. So the choice was not "add auth" but "decide
what the boundary is, and then make the documentation true either way".

## Decision

**No token. The boundary is the Origin allowlist plus loopback binding, and this is written down so
it reads as a decision rather than an omission.**

Allowed origins are the Tauri shell (`tauri.localhost`, both schemes) and the Vite dev/preview ports.
The panel and overlay are served by the daemon itself, so they are same-origin and CORS never applies
to them.

## Alternatives considered

### A bearer token in a file under `%ProgramData%`
- **Pros**: the shape people expect; survives a future where the daemon is not loopback-only.
- **Cons**: **it does not authenticate what it appears to authenticate.** Every first-party client
  that mutates state is a non-elevated user-session process — the tray, the hotkey listeners, the
  Tauri shell, the overlay running in Edge. For all four to read the token, the token must be
  readable by the user's session. At that point every process that can already reach loopback can
  also read the token, which is the same set of processes. It buys the appearance of a boundary and
  moves nothing.
- **Why not**: it would make `docs/api.md`'s old claim true without making the machine safer, and
  four working clients would need changing to get there.

### A token ACL'd to SYSTEM only
- **Pros**: a real restriction.
- **Cons**: breaks all four first-party clients, none of which is elevated. The remaining caller
  would be the daemon talking to itself.
- **Why not**: it authenticates by excluding the actual users of the API.

### Windows named-pipe ACL instead of HTTP
- **Pros**: OS-enforced, no secret to store, genuinely restricts by identity.
- **Cons**: the pipe has no code and, on measurement, no demand — it is already recorded as dropped
  in the roadmap. Every client is a web view; moving them to a pipe means a bridge process, which is
  a new binary, which Smart App Control refuses.
- **Why not**: the cost lands on exactly the constraint this project is built around.

## Consequences

- ⚠️ **What the allowlist does and does not do, so nobody mistakes one for the other.** CORS is
  enforced *by browsers*. It stops "a web page the user visited", which was the vector that actually
  existed here. It does **nothing** against a local process, which can still reach `127.0.0.1:8787`
  with `curl` and call every route. That residual risk is accepted knowingly: a local process running
  as the user can already read any token we could give it.
- `docs/api.md` no longer claims a bearer token. A document asserting a mechanism that is absent is
  this project's cardinal sin, and it had been committing it since the file was written.
- **This constrains future work**, which is why it is an ADR and not a comment: the API must stay
  loopback-only. The moment anything binds a routable interface — a remote dashboard, a fleet
  feature, a phone client — this decision is void and auth becomes mandatory. Reopen this ADR rather
  than adding a listener.
- Mutating routes that do something irreversible or outward-facing still need their own guards
  regardless of transport. See the pending-features plan on why `POST /jobs` will never execute an
  arbitrary command line and why webhook registration, if it ever happens, belongs in a file under
  `%ProgramData%` where file ACLs do the authorising.
