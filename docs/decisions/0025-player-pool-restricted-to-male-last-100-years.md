# ADR-0025: Player pool restricted to male footballers born within the last 100 years

- **Status:** Accepted
- **Date:** 2026-07-13
- **Related requirements:** REQ-112
- **Related components:** COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients)

## Context

The user identified two issues/limitations with the player pool being
sourced from Wikidata without restriction: it can surface female
footballers alongside male ones, and it can surface players from far
outside any period a typical player would recognize (Wikidata's football
coverage reaches back over a century). Both are genuine gameplay-scope
problems for xG Grid specifically — a grid built around men's club/country
football history that unexpectedly surfaces an unfamiliar 19th-century or
women's-football name breaks the "I should be able to reason my way to
this answer" experience the game depends on. This is a scoping decision
about what the *product* covers, not a bug in how existing code already
works — Q937857 ("association football player") on its own doesn't imply
"male," and nothing in the existing SPARQL queries constrained by date at
all.

Two decisions had to be made concrete rather than left as vague policy:
how "male" is expressed against Wikidata's actual data model, and what
"the latest 100 years" means precisely enough to implement — a fixed
cutoff year, a rolling window measured from date of birth, or something
based on career/active-play dates instead.

## Decision

Both `WikidataClient` SPARQL query builders
(`BuildCountryClubIntersectionQuery`, `BuildClubClubIntersectionQuery`)
gained two additional triple/filter pairs: `?player wdt:P21 wd:Q6581097`
(P21 = sex or gender, Q6581097 = male) and `?player wdt:P569 ?dateOfBirth`
with `FILTER(?dateOfBirth >= "<cutoff>"^^xsd:dateTime)`, where `<cutoff>`
is computed as `TimeProvider.GetUtcNow().AddYears(-100)` at query time —
a rolling window recomputed on every call, not a fixed year baked into the
query text. `WikidataClient` gained an optional `TimeProvider` constructor
parameter (same optional-defaulting-to-`TimeProvider.System` shape as
`GridGameModule`) so tests can pin this to a fixed instant.

Existing cached data (`Player`, and everything that cascades from it —
`PlayerData`/`PlayerOverride`/`PlayerAttribute`/`PlayerAlias`) was fetched
before these filters existed and can't be selectively corrected the way a
single wrong club QID could (S-037) — there's no reliable way to tell
"already known to satisfy the new filters" from "never checked" for an
existing row, and re-deriving it from stored data would require a live
Wikidata re-check per player anyway. So the whole player pool is deleted
and rebuilt from scratch under the new queries: a new `purge-player-pool`
CLI verb (S-038) does the bulk delete, gated behind a required, exact
confirmation-phrase argument (`"delete all player data"`) — the same
extra-friction-for-a-destructive-write pattern
`infra/scripts/promote-dev-to-prod.sh` already uses (`"promote to prod"`)
for its own bulk write to real player-facing data — followed by a normal
`warm-player-cache.yml` run to repopulate the pool.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Fixed year cutoff (e.g. "born 1926 or later," baked into the query text) | Simplest possible implementation; deterministic, easy to test without a `TimeProvider` | Silently drifts out of sync with "the latest 100 years" as real time passes — five years from now it would mean "the latest 105 years" unless someone remembers to edit the query text | Not chosen: the whole point of "latest 100 years" is a moving window, and nothing about football reference data changes fast enough to justify a periodic manual bump instead of just computing it |
| Career/active-period filter instead of date of birth | Closer to the literal phrase "footballers from the latest 100 years" — a player born just outside 100 years ago who was still actively playing recently would be included | Wikidata's career-span properties (P54 qualifiers like start/end time) are far less consistently populated across players than P569 (date of birth) — this would silently and unpredictably exclude real, in-scope players with incomplete career-date data much more often than the date-of-birth proxy does | Not chosen: date of birth is a reliable single-property proxy for "modern enough to plausibly be recognized," and 100 years is generous enough (a player born 100 years ago could have played into their 40s, i.e. plausibly as late as the 1960s-70s) that the proxy's imprecision at the margins doesn't materially change what the game covers |
| Selectively re-check/filter existing cached data instead of a full purge | No downtime on the pool; avoids re-spending the live-Wikidata-call budget the original fetch already spent | Nothing in `PlayerAttribute`/`PlayerData` records the sex/date-of-birth facts that would be needed to filter in place — doing this without a full purge would mean a live Wikidata re-check per already-cached player anyway, which is strictly more total live-query work than just re-running the (already-existing, already-scheduled) `warm-player-cache` pass fresh | Not chosen: the "selectively fix" playbook only worked for S-037's wrong-QID case because the fix was scoped to 4 named clubs; a blanket eligibility-criteria change touches the entire pool, where "selective" degenerates into "everything" anyway |
| No confirmation-phrase gate on `purge-player-pool` — same bare argument-presence check as `clean-stale-club-attributes` | Less code, consistent with the other CLI verbs' argument handling | `clean-stale-club-attributes` is scoped to caller-named clubs and is explicitly designed to be safe to re-run; `purge-player-pool` is an unscoped, unrecoverable-without-a-full-rewarm delete of the entire pool triggered by a single GitHub Actions text input — a typo or wrong-workflow click has a much larger, harder-to-undo blast radius | Not chosen: this repo already has a precedent for exactly this risk profile (`promote-dev-to-prod.sh`'s confirmation phrase) and reusing it is cheaper than inventing a new safety mechanism |

## Consequences

- Positive: the player pool going forward is scoped to what the game
  actually needs — no more surfacing an unfamiliar 19th/early-20th-century
  or women's-football name a player has no realistic way to reason their
  way to.
- Positive: the 100-year window is self-maintaining — no periodic manual
  update needed as real time passes, unlike a fixed-year cutoff would have
  required.
- Negative / trade-offs accepted: every existing cached `Player` row (and
  the `PlayerAttribute`/`PlayerData`/`PlayerAlias`/`PlayerOverride` rows
  hanging off it) must be deleted and re-fetched from scratch — there is no
  cheaper incremental path, since neither sex nor date of birth was ever
  recorded on the cached rows to filter against directly.
- Negative / trade-offs accepted: any existing `Guess` row whose
  `PlayerAnswerId` pointed at a since-purged player keeps its
  already-computed `IsCorrect`/score (per the user's own scoping choice —
  game history is explicitly out of scope for this purge) but can no
  longer display which player that answer actually was, since
  `Guess.PlayerAnswerId` has no FK constraint on `Player` to cascade or
  restrict against in the first place.
- Follow-up: if a future Tier 1 data source (API-Football, ADR-0011's
  fallback) is added, its own query/fetch logic will need the equivalent
  male/last-100-years filtering applied independently — this ADR's
  SPARQL-specific implementation doesn't automatically cover a
  differently-shaped API.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change. In
particular: any new or modified Wikidata SPARQL query that returns
candidate players for the pool must include both the `wdt:P21 wd:Q6581097`
(male) triple and a `wdt:P569`/`FILTER(?dateOfBirth >= ...)` rolling
100-year-cutoff pair — do not add a query path that bypasses these filters
without discussing it first, and do not hardcode the cutoff year as a
literal instead of computing it from `TimeProvider.GetUtcNow()`.
