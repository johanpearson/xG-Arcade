# ADR-0113: Trophy coverage for xG Higher/Lower needs a broad per-player sweep, not the existing byproduct-only population — extends ADR-0061/ADR-0111/ADR-0112

- **Status:** Accepted
- **Date:** 2026-09-10
- **Related requirements:** REQ-1501, REQ-1506, REQ-1507
- **Related components:** COMP-06 (Data.PlayerStore), COMP-18 (Games.XGHigherLower)

## Context

REQ-1507's real coverage numbers (`report-international-stats-coverage`,
run against the full dev player pool, 2026-09-10) surfaced a pre-existing
gap unrelated to S-231's own caps/goals work: of 170,678 players, only
**20** have any `"trophy"` `PlayerAttribute` row at all — versus 31,509
for `"international-caps"`. `"trophy"` is not a new category (ADR-0111,
S-224) and its 3 seeded `TrophyDefinition` rows are not just Ballon d'Or
— Ballon d'Or (individual award), FIFA World Cup, and UEFA Champions
League (both team competitions, added by ADR-0061) — so the gap isn't
"too few trophies."

The real cause: `"trophy"` `PlayerAttribute` rows are only ever written
as a **byproduct** of `WikidataLookupService`'s xG Grid candidate-matching
queries (`IntersectionQuerySpecs.TrophyCountry`/`TrophyClub`/
`TeamTrophyCountry`/`TeamTrophyNationalTeam`/`TeamTrophyClub`/
`TrophyNationalTeam`) — each of which starts from one specific
trophy+country/club pairing and finds matching players, the way an xG
Grid round actually needs it. Nothing sweeps the whole player pool for
trophy data the way `PlayerCareerPrefetchService` does for club/
nationality, or the way ADR-0112's new `PlayerInternationalStatsRefreshService`
now does for caps/goals — trophy data for any given player only exists
if some earlier xG Grid round happened to search for that exact pairing
and that player happened to match.

REQ-1506's caps>=10 floor now applies to every xG Higher/Lower category,
including `"trophy"` — so a trophy round needs a player with both a real
trophy value AND 10+ caps. With only 20 trophy-holders in the entire
pool, `"trophy"` will essentially never win the category shuffle in
practice (REQ-1502 quietly falls through to caps/goals when it can't
build a full sequence), even though nothing is technically broken.

## Decision

**A new broad per-player-batch trophy sweep**, mirroring ADR-0112's
`PlayerInternationalStatsRefreshService`/`PlayerInternationalStatsBackfillService`
shape exactly (`IPlayerTrophyStatsRefreshService`/
`PlayerTrophyStatsRefreshService`/`PlayerTrophyStatsBackfillService`,
same batch-by-player-IDs/`throwOnFailure` contract, same
`workflow_dispatch`-only CLI-verb job, ADR-0024) — checks every seeded
`TrophyDefinition` against a batch of already-known players and writes
the SAME `"trophy"` `PlayerAttribute` row shape
`WikidataLookupService`'s byproduct path already writes (one row per
`(player, trophy name)` they've actually won) — **no change to
ADR-0111's COUNT-of-rows derivation rule**, this is purely a new
sourcing path feeding the same data shape.

Two new batch query builders (`SparqlQueryBuilders`), one per
`TrophyDefinition.IsTeamTrophy` value, each batching every seeded
trophy of that kind into one `VALUES` clause (not one query per trophy —
keeps total Wikidata round-trips to roughly 2 per player-batch, the same
order of magnitude as ADR-0112's own sweep, not 2-3x it):

1. **Individual awards** (`IsTeamTrophy = false`, e.g. Ballon d'Or):
   truthy `?player wdt:P166 ?trophy.` with `VALUES ?trophy { ...every
   individual TrophyDefinition QID... }` — same truthy-is-safe reasoning
   `BuildTrophyCountryIntersectionQuery`'s own comment already
   established (no preferred-rank convention on repeatable-award
   statements).
2. **Team competitions** (`IsTeamTrophy = true`, e.g. World Cup,
   Champions League): `?player wdt:P1344 ?edition. ?edition wdt:P3450
   ?trophy. ?edition wdt:P1346 ?winner.` with `VALUES ?trophy { ...every
   team TrophyDefinition QID... }`, THEN a `UNION` of the three
   "was the player themselves part of the winning side" checks the
   existing candidate-search queries already use separately (club via
   full-statement-path `P54`, country via `P27`, UK-home-nation via
   `P1532`) — since a broad per-player sweep doesn't have a
   pre-known club/country target to anchor on the way
   `TeamTrophyClub`/`TeamTrophyCountry`/`TeamTrophyNationalTeam` do, it
   must check all three ways a player's own side could match `?winner`.
   No new join logic invented — this is the same three existing
   `IntersectionQuerySpecs` builders' own winner-side matching,
   restructured from "one target, find players" to "one player batch,
   check against the winner."

**Idempotency, correct from the start (the lesson this ADR exists to
apply, not rediscover — see NOTES.md's 2026-09-10 backfill-idempotency
entry):** the overwhelming majority of players will have won NONE of the
3 seeded trophies, so — exactly like caps/goals before its own bug fix —
"no trophy `PlayerAttribute` row" would be indistinguishable between
"never checked" and "checked, won nothing" if this reused only the raw
row as its completion signal. `PlayerTrophyStatsRefreshService` MUST
write its own `PlayerData`-only "checked" marker
(`PlayerData.TrophyStatsCheckedField`, a new shared constant alongside
`InternationalStatsCheckedField` on that same entity, same "writer/
reader bookkeeping flag, not stable domain vocabulary" reasoning) for
every player in a successfully-queried batch, and
`GetPlayersMissingTrophyStatsAsync`'s "missing" filter MUST check for
the absence of BOTH the marker AND a real `"trophy"` row (the second
check keeps it correct for the 20 players whose trophy row already
exists from the byproduct path, which predates this marker and has
none) — do not ship this without it.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| **Broad per-player sweep, `VALUES`-batched per trophy kind (chosen)** | Fixes the real gap (byproduct-only population); reuses the existing, already-reviewed join patterns for both trophy kinds; no new `PlayerAttribute` derivation rule | Team-competition query is genuinely more complex (a 3-way `UNION`) than any single existing intersection query | Chosen: the only option that actually grows real, broad trophy coverage rather than leaving it to chance |
| Leave `"trophy"` byproduct-only, just add more trophies (e.g. domestic league titles) to the seeded list | Simple, no new query shape | Doesn't fix the actual bottleneck — more trophies in the list still only get *any* player data via the same rare byproduct path; REQ-1506's floor would still starve the category | Rejected: solves the wrong problem, per the Context section's own root-cause finding |
| Drop `"trophy"` from `HigherLowerGenerationService.CandidateStatCategories` entirely (mirrors how `"club"` was dropped in S-231) | Zero new work | Throws away a real, requested category (trophies ARE the "accomplishment" stat REQ-1501's own examples want, unlike club count) just because its data pipeline was never finished | Rejected: the product-owner's own direction (this ADR) was to fix the pipeline, not remove the category |

## Consequences

- Positive: `"trophy"` becomes a real, playable category instead of one
  that's technically present but never actually selectable.
- Positive: reuses every existing join pattern this codebase has already
  reviewed for correctness (P166 truthy-is-safe, P1344/P3450/P1346,
  P27/P1532/P54 winner-side matching) — no new SPARQL primitive.
- Negative / trade-off accepted: the team-competition query's 3-way
  `UNION` is more complex than any other single query builder in this
  codebase — worth a second look in review given that complexity, even
  though each branch individually is already-proven logic.
- Negative / trade-off accepted, inherited from ADR-0112: real coverage
  cannot be verified live from the implementing sandbox (no
  `query.wikidata.org` egress) — verify via a real backfill run plus
  `report-international-stats-coverage`-style follow-up before trusting
  the resulting trophy pool size.
- Follow-up: adding more trophies to `ReferenceDataSeeder.Trophies`
  (league titles etc.) remains a separate, legitimate way to grow the
  pool further, once this sweep proves the pipeline itself works — not
  a substitute for it, and not in this ADR's scope.

## For AI agents

Do not build this without the `PlayerData` "checked" marker from the
first commit — that is not an optional hardening pass, it is the
specific, already-proven-necessary fix for the exact bug ADR-0112's own
Amendment section already had to patch once for caps/goals. Do not
invent a new winner-side join for the team-competition query — reuse the
three existing `P54`/`P27`/`P1532` matching clauses from
`IntersectionQuerySpecs.cs` inside a `UNION`, don't design new matching
logic. Do not change `"trophy"`'s COUNT-of-rows derivation
(`GetEffectivePlayerCountsByAttributeTypeAsync`) — this ADR only adds a
new writer, never a new reader shape. If code you are about to write
would contradict this decision, stop and flag it rather than silently
working around it.
