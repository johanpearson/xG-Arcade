# ADR-0094: API-Football fixtures/results as xG Predict's data source

- **Status:** Accepted (with one open action item before launch)
- **Date:** 2026-08-30
- **Related requirements:** REQ-1301, REQ-1305
- **Related components:** COMP-15 (Games.XGPredict), COMP-07 (DataSync.Clients)

## Context

xG Predict (see `docs/requirements-document.md` §4.14, REQ-1301-1305) needs
two things nothing in this codebase has ever needed before: an upcoming
Premier League gameweek's fixture list (to select the 5 matches for a
round, REQ-1301), and each match's final score after it finishes (to grade
predictions, REQ-1305). Every existing data source in this repo — Wikidata,
via `WikidataClient` (COMP-07) — is career/biography data: who played
where, when. It has no concept of a scheduled match or a live/final score.
This is a genuinely new data domain for the platform, not a variation on
the existing player-lookup pattern.

API-Football already has a relationship with this project: ADR-0011 names
it as xG Grid's Tier-1 fallback source for player data, and ADR-0008
reviewed its terms of service — but only for that use (permanently caching
player bio data). Tier 0 never actually triggered API-Football usage at
all (`MVP-SCOPE.md`: Wikidata-only), so as of this ADR, no code in this
repo has ever called API-Football for anything.

Per this repo's standing rule (CLAUDE.md: "New external data sources need
a terms-of-service check first... don't assume a new source is fine by
analogy"), fixtures/live-score polling is a different enough usage pattern
from ADR-0008's permanent-caching review (short-lived, point-in-time data
that must be re-checked until confirmed, not fetched once and kept
forever) that it needs its own look before being relied on, even though
it's the same provider.

**Terms and free-tier check, done this session (2026-08-30):**
- Free plan: 100 requests/day (resets 00:00 UTC), no credit card required,
  all endpoints included (fixtures among them) — not a crippled trial tier.
  Source: api-football.com's own pricing page.
- Recommended polling cadence per their own documentation: roughly 1
  call/minute for a fixture actually in progress, otherwise ~1/day; some
  competitions can take up to 48h to fully confirm a final result.
- Direct fetch to api-football.com was blocked by this sandbox's egress
  proxy; the above was confirmed via web search against their own
  published pricing/documentation pages rather than a direct page read.
- ADR-0008's own reading of their terms already found "fantasy soccer
  games"-shaped products explicitly within their stated intended use, and
  found nothing prohibiting a gameplay product built on top of their data
  (as opposed to reselling the raw data) — that reasoning applies here too,
  but was written with permanent player-data caching in mind, not
  live-score polling specifically, hence this ADR's own action item below
  rather than assuming ADR-0008's confirmation email (never actually sent,
  since Tier 0 never used API-Football) already covers it.

**Usage estimate for xG Predict at Tier 0 scale:** all calls are
server-side and shared across every player (no client ever calls
API-Football directly), so usage scales with rounds, not with player
count. Roughly 1-2 calls to pull a gameweek's fixture list per round
generation (REQ-1301), plus polling for the round's own 5 (deliberately
kickoff-clustered) fixtures during their live window — well under 100/day
even polling every 5-10 minutes through a multi-hour window, with room
left for a trailing check on any fixture not yet confirmed (REQ-1305).

## Decision

1. A new client in `DataSync.Clients` (COMP-07) wraps API-Football's
   fixtures endpoint, isolated from `WikidataClient` the same way every
   existing provider client in this component already is — the fixture
   list, "in progress"/"finished" status, and final score are the only
   fields xG Predict needs, no broader API-Football surface is adopted.
2. The free tier is accepted as sufficient for Tier 0 — no paid API-Football
   plan is needed to build or run xG Predict at current scale. Revisit only
   if round frequency or matches-per-round meaningfully grows beyond what
   REQ-1301 currently specifies (one 5-match round per gameweek).
3. **New precondition, additive to `MVP-SCOPE.md`, not an acceleration of
   xG Grid's own Tier 1 trigger:** an API-Football account and API key are
   required for xG Predict specifically, from its first build, independent
   of xG Grid's own Wikidata-only Tier 0 status and its separate,
   still-unfired Tier 1 API-Football trigger (ADR-0011). `MVP-SCOPE.md`
   gets a note reflecting this as its own precondition, worded so it does
   not imply xG Grid's Tier 0 scope changed.
4. **Action item, required before public launch (not before development —
   same framing ADR-0008 used):** send API-Football support written
   confirmation covering this specific use — fetching and short-term
   polling of fixture/live-score data for grading a prediction game, not
   just the permanent player-data caching ADR-0008 already asked about.
   File the confirmation in `docs/decisions/correspondence/` alongside the
   existing API-Football confirmation email.
5. **Caching/polling posture:** cache a gameweek's fixture list once per
   round generation (REQ-1301); poll live/result status only for that
   round's own 5 fixtures, at a modest interval during their live window,
   stopping once each fixture's result is confirmed — matching API-Football's
   own "poll in-progress fixtures periodically, otherwise ~1/day" guidance
   rather than continuous polling.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| A different fixtures/results provider (e.g. football-data.org, Sportmonks) | Might have cleaner terms | No evidence any of them is actually clearer than API-Football (same caveat ADR-0008 already noted for player data); adds a second provider relationship to maintain for no demonstrated benefit | API-Football's terms are already reviewed once (ADR-0008) and found workable; reusing the existing relationship is cheaper than vetting a new one from scratch |
| Proceed without a fresh terms check, relying on ADR-0008 by analogy | No extra effort | Directly contradicts this repo's own standing rule against assuming a new use is fine by analogy to a prior, differently-scoped review | The rule exists specifically to catch this shortcut |
| Skip the free tier, go straight to a paid plan | Removes any doubt about rate limits | No evidence the free tier is insufficient — the usage estimate above is comfortably under budget; paying for headroom that isn't needed yet contradicts `MVP-SCOPE.md`'s build-the-minimum-first principle | Revisit only if real usage data shows otherwise |

## Consequences

- Positive: reuses `DataSync.Clients`' existing isolation boundary (COMP-07)
  — if the eventual written ToS confirmation turns out unfavorable, or a
  provider swap is ever needed, the existing pattern (an isolated,
  swappable client layer) makes that cheap, same as ADR-0008 already
  established for the player-data client.
- Positive: zero new cost — the free tier covers this game's actual usage
  with real margin.
- Negative / trade-offs accepted: a new external dependency and a new
  account/key precondition specific to this one game; a genuinely new
  "poll for a real-world outcome after the fact, possibly not yet
  available" pattern (REQ-1305) that nothing in this codebase has needed
  before — `WikidataClient`'s "fetch once, cache forever" assumption does
  not apply here.
- Follow-up: obtain the written ToS confirmation described above before
  public launch; if that response is unfavorable, revisit via the same
  swappable-client-layer escape hatch ADR-0008 already relies on.

## For AI agents

Do not reuse `WikidataClient`'s "fetch once, cache permanently" assumption
for fixture/result data — a fixture's status and score are point-in-time
facts that must be re-checked until REQ-1305's grading confirms them, not
fetched once and trusted forever. Do not call the fixtures endpoint from a
per-request or per-user code path — every call is server-side and shared,
the same discipline `WikidataClient` already follows. Treat "not yet
confirmed" (per the up-to-48h window noted above) as a retry-later state in
REQ-1305's grading logic, never as a permanent failure. Before adding any
further API-Football endpoint beyond fixtures (e.g. odds, statistics) for
this or another game, re-check terms for that specific use rather than
assuming this ADR's review already covers it.
