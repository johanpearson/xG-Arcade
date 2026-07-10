# ADR-0018: REQ-211 (guess-time live verification) implemented in Tier 0, without its PlayerNameIndex gate

- **Status:** Accepted
- **Date:** 2026-07-10
- **Related requirements:** REQ-211 (revised status), REQ-101, REQ-103, REQ-207 (unaffected — still deferred)
- **Related components:** COMP-05 (Games.XGGrid), COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Wikidata)

## Context

A player reported three genuinely correct guesses (Messi for
Argentina×Barcelona, among others) marked incorrect on a live grid. Root
cause, exactly as ADR-0010 already anticipated: `GridGameModule.GetMatchCountAsync`
only queries Wikidata live when a country×club pair has zero cached
matches (REQ-103/S-007). Once a pairing already clears `MinValidAnswers`
from whichever players happened to get cached before, it never queries
again — so a real, correct player can have no `PlayerAttribute` data at all
for a specific cell (either because they were never synced, or because
they already exist with one category cached from an unrelated cell but not
this cell's other one) and get wrongly rejected at guess time.

This is precisely `MVP-SCOPE.md`'s documented trigger for building REQ-211
early ("you find one that was actually correct, more than a rare fluke") —
three in one grid clears that bar. But REQ-211 as fully specified (and as
ADR-0010 designed it) gates the live lookup on a `PlayerNameIndex` match
first, to keep the trigger narrow against a scarce lookup budget.
`PlayerNameIndex` (COMP-10, REQ-207) is itself Tier 1 and not built —
pulling it forward just to gate this fix would mean building two deferred
features instead of one, which `CLAUDE.md`'s build-order rule says to flag
rather than do quietly.

## Decision

Implement REQ-211's guess-time live lookup now, in Tier 0, **without** the
`PlayerNameIndex` gate:

- `GridGameModule.ScoreSubmissionAsync` first checks cached data exactly as
  before (REQ-203/208/209). If that fails to find a match, it re-runs the
  cell's own country×club Wikidata intersection query (the same
  `WikidataLookupService` call `GenerateInstanceAsync` already uses) once,
  then re-checks cached data again.
- This is an upsert, not a fresh insert — it fixes both "player never
  synced at all" and "player exists but is missing this cell's other
  category" in one call, and leaves the cell's whole answer key complete
  for later guesses on the same cell too.
- The trigger is "cached data didn't already answer this guess" rather
  than "name matched `PlayerNameIndex`." This is bounded by REQ-210's
  existing 2-attempts-per-cell cap (at most 2 extra Wikidata calls per
  cell), and Wikidata has no hard daily request cap per ADR-0011 (throttled
  by query-time, not count) — the scarce-budget concern that motivated
  `PlayerNameIndex`-gating in ADR-0010 applies to API-Football, not to
  Wikidata, so it doesn't force the same prerequisite here.
- API-Football fallback and `ExternalApiUsage` budget-gating (REQ-211's
  other acceptance criteria) remain out of scope, same as REQ-103's
  existing Tier 0 status note — there is still only one live source wired
  up.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Build `PlayerNameIndex` first, then full REQ-211 | Matches the original spec exactly | Pulls a second Tier 1 story (REQ-207) forward for no reason other than gating; much larger change for the same user-facing fix | `CLAUDE.md`'s "don't pull Tier 1 forward to make Tier 0 work" rule — flagged to the user, who chose the narrower fix |
| Leave it as a documented Tier 0 limitation, no code change | Zero risk | The trigger condition for fixing this was explicitly met; leaving genuinely correct guesses marked wrong is a real trust problem (ADR-0010's own words) | Rejected by the user once the trigger fired |
| Always re-query Wikidata on every guess, even ones cache already answers | Simplest to write | Wastes a call on every already-correct guess, no benefit | Only fall back when cache didn't already resolve the guess |

## Consequences

- Positive: closes the exact gap the user reported, reusing 100% of
  existing plumbing (`WikidataLookupService`, `ICategoryValueRepository`) —
  no new client, no new entity, no new DI wiring
- Negative / trade-offs accepted: the live-lookup trigger is broader than
  full REQ-211's spec (no name-index pre-filter) — a guess for a name that
  matches nothing real still costs one extra Wikidata call per cell per
  attempt, rather than being filtered out before the network call. Judged
  acceptable given Wikidata's generous throttling model and REQ-210's
  2-attempt cap already bounding the worst case.
- Follow-up: if `PlayerNameIndex` (REQ-207) is ever built for autocomplete,
  revisit whether to add it as a pre-filter here too, purely as a latency
  optimization — not required for correctness, since this fix's fallback
  is already an upsert and already bounded.

## For AI agents

If you build `PlayerNameIndex` (REQ-207) later, you may add it as a
pre-check here to skip a pointless live lookup for outright unreal names —
but that is a latency optimization on top of this decision, not a
prerequisite for it. Don't remove the fallback path itself pending that
work. Don't extend this fallback to trigger API-Football (still not
wired up here) without re-reading REQ-211's full budget-gating criteria
and ADR-0011's waterfall order first.
