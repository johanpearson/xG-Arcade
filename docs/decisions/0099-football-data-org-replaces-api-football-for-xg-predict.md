# ADR-0099: football-data.org replaces API-Football as xG Predict's fixtures/results data source

- **Status:** Accepted (with one open action item before launch — see
  Consequences)
- **Date:** 2026-08-31
- **Related requirements:** REQ-1301, REQ-1305
- **Related components:** COMP-15 (Games.XGPredict), COMP-07 (DataSync.Clients)
- **Supersedes:** ADR-0094 (Decision items 1-2 specifically — API-Football
  as the chosen provider, and the free-tier-sufficiency judgment; item 3's
  "an account/key precondition, additive to `MVP-SCOPE.md`, specific to xG
  Predict" reasoning and item 5's caching/polling posture both carry
  forward unchanged, now against football-data.org instead)

## Context

ADR-0094 (2026-08-30) chose API-Football as xG Predict's fixtures/results
data source and judged its free tier ("100 requests/day, no credit card
required, all endpoints included") sufficient for this game's usage. That
judgment was never verified against a live API response — this sandbox's
egress proxy blocked api-football.com even then, so ADR-0094's own Context
section already flagged the free-tier assessment as "confirmed via web
search... rather than a direct page read."

On 2026-08-31, the first real deploy with a configured API-Football key hit
this in production: `/internal/generate-round` for `xg-predict` returned
500, `"API-Football returned no current round name — check the
ApiFootball:LeagueId/Season configuration."` League ID 39 (Premier League)
and season 2026 were confirmed correct against the account's own dashboard
(a `/leagues?id=39` lookup showed season 2026, 2026-08-21 to 2027-05-30,
marked as the league's current season). The actual cause, confirmed via
api-football.com's own support chatbot: **the free plan does not include
the current season at all** — "The free plan restricts you to seasons
within a specific range: only the last 2-4 seasons are available... you
cannot access the current season or any future seasons." No season
parameter this backend could send would ever reach the current gameweek's
fixtures on a free-tier key. This is a structural limitation, not a bug —
API-Football's free tier is fine for historical/archival use, genuinely
unusable for a live prediction game without a paid plan.

This makes ADR-0094 Decision item 2 ("the free tier is accepted as
sufficient... no paid API-Football plan is needed") factually wrong, not
merely outdated. The options were: upgrade to a paid API-Football plan,
pause xG Predict pending a plan decision, or find a different free provider
that actually includes the current season. The product owner chose to look
for an alternative provider first rather than pay for API-Football.

## Decision

Replace API-Football with **football-data.org** as xG Predict's fixtures/
results client, keeping ADR-0094's isolation boundary (a single client in
`DataSync.Clients`, COMP-07) and every other reasoning in that ADR intact:

1. `DataSync.FootballData.FootballDataClient` (implementing
   `IFootballDataClient`) replaces `DataSync.ApiFootball.ApiFootballClient`
   (`IApiFootballClient`) entirely — same two-method shape
   (`GetUpcomingGameweekFixturesAsync`/`GetFixtureResultAsync`), same
   narrow surface (fixture list, status, final score only), same
   point-in-time/never-cache-permanently posture (ADR-0094's "For AI
   agents" section, carried forward unchanged).
2. football-data.org's free tier is accepted as sufficient for Tier 0: 12
   competitions including the Premier League, **current season included**
   (unlike API-Football's free tier), 10 requests/minute, no credit card.
   This game's usage (1-2 calls per round generation, plus modest polling
   during each round's live window) is comfortably under that rate limit —
   an easier budget story than API-Football's 100/day cap, since
   football-data.org's limit is a steady-state rate, not a daily total.
3. Config simplifies as a direct consequence of the swap, not a separate
   choice: football-data.org's `GET /v4/competitions/{code}` response
   carries `currentSeason.currentMatchday` directly, so
   `FootballDataOptions` needs only a competition code (`"PL"`), never a
   separately-configured/computed season year the way `ApiFootballOptions`
   needed (ADR-0094's own by-start-year season computation, and its
   "needs a human to sanity-check each pre-season" caveat, are both gone).
4. **Action item, required before public launch (not before development —
   same framing ADR-0094/ADR-0008 both used):** this sandbox's egress
   proxy blocks football-data.org/docs.football-data.org entirely (same as
   it blocked api-football.com for ADR-0094/ADR-0008), so **the actual
   terms of service have not been read** — only secondhand summaries via
   web search, which were inconsistent about whether the free tier permits
   commercial use at all. A human with real network access must read the
   real terms and confirm xG Arcade's use is compatible before relying on
   this in production, following this repo's standing rule ("New external
   data sources need a terms-of-service check first... don't assume a new
   source is fine by analogy") even more literally than usual, since this
   ADR could not do that check itself. Attribution is already known to be
   required regardless of the commercial-use answer: "Football data
   provided by the Football-Data.org API" somewhere in the frontend.
5. Caching/polling posture unchanged from ADR-0094 item 5: cache a
   gameweek's fixture list once per round generation, poll live/result
   status only for that round's own 5 fixtures during their live window,
   stopping once each fixture's result is confirmed.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Upgrade to a paid API-Football plan | Zero code change — the existing client already worked correctly once a key with current-season access existed | Recurring cost for a Tier 0 hobby project; the client itself was already fully built and tested, so this was the "cheapest in engineering time" option | Product owner chose to look for a free alternative first rather than pay |
| Pause xG Predict pending a plan decision | No new provider risk, no rewrite | Leaves a fully-built game (S-190 through S-198) dark indefinitely for a data-source problem, not a real product decision | A working free alternative existed; no reason to sit idle once found |
| Sportmonks (free tier) | Same "free, no credit card" shape as football-data.org | Free tier explicitly does not include the Premier League (only Danish Superliga/Scottish Premiership) — confirmed via their own free-plan page | Doesn't solve the actual need at all |
| "Always free, no rate limit" providers found via search (e.g. BSD, footballdata.io) | Claimed no restrictions at all | Low confidence in reliability/legitimacy for a real product — thin documentation, unfamiliar/unverifiable operators, no track record this review could establish | Not worth the risk versus a well-known, widely-used provider (football-data.org) for a marginal convenience gain |
| football-data.org (chosen) | Free tier explicitly includes the Premier League's current season, well-established/widely-used provider, simpler config (no season computation needed) | Its own real ToS still unverified from this sandbox (egress blocked, same as API-Football); 10 req/min is a tighter per-minute rate limit than API-Football's 100/day, though still ample for this game's usage | Best available option once Sportmonks and the low-confidence "unlimited free" providers were ruled out |

## Consequences

- Positive: solves the actual blocker (current-season fixture access) at
  no ongoing cost, using a provider whose free tier is well-documented and
  widely relied on by other hobby projects for exactly this reason.
- Positive: config gets simpler, not just swapped — no season-year
  computation/pre-season sanity-check burden the way ADR-0094's
  `ApiFootballOptions` needed.
- Positive: reuses `DataSync.Clients`' existing isolation boundary
  (COMP-07) — the swap touched exactly the files ADR-0094's own
  Consequences section anticipated a provider swap would touch (the client
  itself, its DI registration, and the two xG Predict consumers), nothing
  in `XGArcade.Games.XGGrid`/`XGArcade.Games.XGPath`.
- Negative / trade-offs accepted: a second external-data-source ToS check
  in as many days that this repo's own sandbox cannot actually perform —
  the "confirm via a real fetch" verification loop this repo's process
  calls for is structurally unavailable here, so this ADR is explicit
  about what it could and couldn't confirm rather than asserting a false
  confidence the way ADR-0094 unintentionally did the first time.
- Negative / trade-offs accepted: `xg-predict` round generation was
  broken in the deployed dev environment from the first real key
  configuration (2026-08-31) until this swap shipped and a new
  `FOOTBALL_DATA_API_KEY` was configured — a real, if short, production
  gap, not merely a development-time correction.
- Follow-up: obtain a genuine, human-read confirmation of football-data.org's
  free-tier terms (commercial use, caching/retention, attribution) before
  public launch, and add the required attribution line to the frontend;
  both tracked in `TODO.md`. If that reading turns out unfavorable, the
  same swappable-client-layer escape hatch this ADR itself just used
  remains available.

## For AI agents

Do not reuse `WikidataClient`'s "fetch once, cache permanently" assumption
for fixture/result data — a fixture's status and score are point-in-time
facts that must be re-checked until REQ-1305's grading confirms them, not
fetched once and trusted forever (unchanged from ADR-0094). Do not call
`IFootballDataClient` from a per-request or per-user code path — every call
is server-side and shared. Treat `NotYetConfirmed` as a retry-later state
in REQ-1305's grading logic, never as a permanent failure. Before trusting
this ADR's ToS summary as sufficient, check `TODO.md` — the real terms have
not been read from this sandbox, and that gap is tracked there, not
silently assumed closed. Before adding any further football-data.org
endpoint beyond competitions/matches (e.g. standings, team crests) for this
or another game, re-check terms for that specific use rather than assuming
this ADR's incomplete review already covers it.
