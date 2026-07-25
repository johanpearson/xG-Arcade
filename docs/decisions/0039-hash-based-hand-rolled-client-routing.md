# ADR-0039: Hash-based, hand-rolled client-side routing for URL-reflected navigation

- **Status:** Accepted
- **Date:** 2026-07-25
- **Related requirements:** REQ-721, REQ-303, REQ-719, REQ-720
- **Related components:** None (frontend-only; no `XGArcade.Core`/game/backend component boundary changed)

## Context

`frontend/src/App.tsx`'s `Screen` union (`'game-select' | 'grid' |
'leaderboard' | 'leagues' | 'settings' | 'admin'`) is pure React state —
there is no router, the browser URL never changes as a player navigates
between screens, and a page reload always resets to `'game-select'` (or,
for an unauthenticated visitor, the splash screen, REQ-719). The product
owner asked for the current screen to be reflected in the URL so a reload
(or a shared/bookmarked link) restores it, and explicitly asked whether
`/` or `#` should be used. REQ-721 specifies the observable behavior only
and defers the implementation choice to this ADR — this is the first
router/URL-state mechanism the frontend has ever had, so nothing existing
constrains the answer by precedent.

Two independent questions had to be settled: (1) hash-based vs.
path-based URLs, and (2) a routing library vs. a hand-rolled mechanism.

The frontend is a static Vite/React build served as an Azure Static Web
App (`infra/bicep/modules/static-web-app.bicep`, ADR-0004). No
`staticwebapp.config.json` or `navigationFallback` rule exists in this
repo today. Azure Static Web Apps does have a documented default fallback
to `index.html` for unmatched routes, but nothing in this repo currently
verifies or exercises that default. This project has a real track record
of "worked until the first actual deploy" surprises that only surfaced
against the live environment (the `swedencentral`/Static Web App region
restriction, the `GHCR_TOKEN` vs `GITHUB_TOKEN` expiry issue, the Npgsql
connection-string format issue — all documented in `infra/README.md` /
`NOTES.md`). Playwright E2E (`frontend/playwright.config.ts`, `ci.yml`)
runs against the Vite *dev* server, which has its own built-in
history-API-fallback behavior — so a path-based routing bug would pass
local dev, Vitest, and E2E cleanly and only fail once deployed to the real
Static Web App, the one environment nothing in CI actually exercises a
deep-link reload against.

Separately, `frontend/package.json` has no router dependency today, and
`Screen` is a flat 6-value union with no nesting and no per-entity path
parameters in scope. REQ-721 explicitly excludes browser back/forward
support from this requirement — which is a routing library's main value
proposition (integrating with the browser history stack).

## Decision

1. **Hash-based URLs** (e.g. `#/grid`), not path-based URLs via the
   History API. The fragment after `#` is never sent to the server, so
   the server only ever sees `GET /` regardless of which screen the URL
   encodes — this needs zero server-side configuration, in any
   environment, ever, and adds no new Azure-specific artifact to maintain
   or re-verify if hosting ever changes (consistent with ADR-0004's
   hosting-agnostic spirit for the frontend host).
2. **Hand-rolled**, not a routing library (e.g. `react-router`): one
   lookup table mapping `Screen` to its hash string and back, read once
   on mount (gated on `accessToken`/REQ-719's splash-gate resolution
   having already settled) to pick the initial `screen`, and written via
   `location.hash = ...` at each existing `setScreen(...)` call site. No
   `popstate`/`hashchange` listener is added, since browser back/forward
   is explicitly out of scope per REQ-721.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Path-based URLs (History API) | Conventional, "cleaner" URL shape; no visible `#` | Depends on an unverified Azure Static Web App SPA-fallback default for reload-on-a-deep-path; the one place nothing in CI exercises this (Playwright runs against Vite dev server, which has its own fallback) is the real deployed host — exactly this project's recurring "fine until the first real deploy" failure pattern | Not chosen: real, currently-unverified infra risk for a purely cosmetic gain, given REQ-721 has no need for `/grid/:id`-style nested/parameterized paths |
| `react-router` (or similar library) | Handles browser history/back-forward, nested routes, param matching, link components | Buys history-stack integration REQ-721 explicitly disclaims (back/forward out of scope); `Screen` is a flat 6-value union with no nesting or params to justify it; would mean retrofitting `App.tsx`'s single ternary onto an element-based routing model for a requirement that doesn't need it | Not chosen: pays for capability this requirement explicitly doesn't want, against a Tier 0 app with no nested/parameterized route shape today |
| `popstate`/`hashchange` listener for back/forward support | Slightly more "app-like" navigation feel | REQ-721 explicitly scopes browser back/forward out — adding this would build a guarantee nobody asked for and nobody is testing | Not chosen: out of scope per REQ-721; add only if a future requirement brings it back in |

## Consequences

- Positive: no server-side or infra change is needed in any environment
  (dev, CI, or the deployed Azure Static Web App) — the hash mechanism
  works identically everywhere the app is served from `/`.
- Positive: no new dependency added to `frontend/package.json`; the
  mechanism is small enough (~20 lines: one lookup table, one read-on-mount
  effect, one write per `setScreen` call site) to review and test as part
  of `App.tsx` directly, matching this project's Tier 0 discipline against
  pulling forward unneeded complexity (`MVP-SCOPE.md`).
- Negative / trade-off accepted: browser back/forward has no defined
  behavior after this change — a player pressing back may land somewhere
  unexpected. Accepted because REQ-721 explicitly scopes this out rather
  than assuming it for free.
- Negative / trade-off accepted: the URL carries a `#` rather than a
  clean path — considered a purely cosmetic cost, not a functional one,
  and one the product owner was explicitly open to accepting.
- Follow-up: if a future requirement needs browser back/forward support,
  or `Screen` grows nested/parameterized routes (e.g. a per-round or
  per-league detail URL), that is the trigger to introduce `react-router`
  and a new ADR superseding this one — not before.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.
