# ADR-0061: Team-competition trophies query via tournament-edition participation + winner join, not a direct player property

- **Status:** Accepted
- **Date:** 2026-08-09
- **Related requirements:** REQ-108
- **Related components:** COMP-05 (Games.XGGrid), COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients)

## Context

S-031 shipped Trophy as a v1 category type, deliberately scoped to
individual awards only (Ballon d'Or): "received this award" maps cleanly
to Wikidata's `P166` ("award received") on the player item directly —
the same simple `BuildIntersectionQuery` shape as every other category
pairing (a single joining property, or two for a club-involving pairing).
Team-competition trophies (FIFA World Cup, UEFA Champions League) were
explicitly deferred at that point because no single Wikidata property
links a player directly to "won this tournament" the way `P166` does for
an individual award — winning a team competition is a fact about a
*squad and a specific tournament edition*, not a standing fact about the
player item itself.

This ADR is the deferred follow-up: how a team-competition trophy is
actually queried, now that it's being built.

**What Wikidata actually models:** there is no `P166`-equivalent
"trophies won" statement on player items for team competitions. Instead:

- A player who took part in a specific edition of a competition (e.g.
  "2014 FIFA World Cup," "2014–15 UEFA Champions League") has a `P1344`
  ("participant of") statement pointing at that edition's own item.
- Each edition item is linked to its parent competition/series item via
  `P3450` ("sports season of league or competition") — e.g. "2014 FIFA
  World Cup" `P3450` → "FIFA World Cup."
- Each edition item has a `P1346` ("winner") statement. For the World Cup
  this points at a *national football team* item (e.g. "Brazil national
  football team"), not the country item itself. For the Champions League
  it points directly at the winning *club* item — the same item already
  stored as `ClubDefinition.WikidataQid`.
- A national-team item is connected back to the country/nation it
  represents via `P1532` ("country for sport") — the identical property
  ADR-0035 already introduced for England/Scotland/Wales/Northern
  Ireland's player-side citizenship problem, here used on the *winner*
  side instead of the player side.

So "this player won this team trophy for this country" is a **join
across three things** — the player's own participation, the edition's
membership in the trophy's series, and the edition's winner matching the
target country/club — not a single joining property. This is exactly the
"squad membership + tournament result" shape flagged as the reason for
deferring at S-031.

## Decision

**Reuse the existing entry points, branch on data, not on a new
interface shape.** `TrophyDefinition.IsTeamTrophy` already exists in the
schema (added at S-031 for future use, unused until now). `GridGameModule`
and `WikidataLookupService.LookupAndPersistTrophyCountryAsync`/
`LookupAndPersistTrophyClubAsync` keep their existing signatures
unchanged — no caller needs to know which query shape is used underneath.
`WikidataLookupService` is the single dispatch point (same precedent as
`UsesCountryForSportProperty` in `LookupAndPersistAsync`, ADR-0035): it
checks `trophy.IsTeamTrophy` and calls a different `IWikidataClient`
method for the team-competition shape instead of the existing `P166`
individual-award one.

**`IWikidataClient` gains three new methods, additive only:**

- `QueryTeamTrophyCountryIntersectionAsync(trophyQid, countryQid, ...)` —
  player-side `P27` (citizenship).
- `QueryTeamTrophyNationalTeamIntersectionAsync(trophyQid, countryQid, ...)`
  — player-side `P1532` (country for sport), the team-trophy counterpart
  of `QueryNationalTeamClubIntersectionAsync`, for England/Scotland/Wales/
  Northern Ireland rows.
- `QueryTeamTrophyClubIntersectionAsync(trophyQid, clubQid, ...)` — no
  player-side branch needed; a club's identity is unambiguous, unlike a
  country's citizenship-vs-represented split.

Query shape (Country variant; National-team variant swaps `P27` for
`P1532` on the player line only):

```sparql
?player wdt:P106 wd:Q937857.
?player wdt:P27 wd:{countryQid}.        # or wdt:P1532 for a home-nation row
?player wdt:P1344 ?edition.
?edition wdt:P3450 wd:{trophyQid}.
?edition wdt:P1346 ?winner.
?winner wdt:P1532 wd:{countryQid}.      # winner side ALWAYS P1532, regardless
                                          # of player-side property — a
                                          # national team's own P1532 is what
                                          # ties a P1346 winner value back to
                                          # a country/nation, independent of
                                          # which property identifies the
                                          # PLAYER's side of the match
```

Club variant, using the trophy's edition winner directly against the
already-stored `ClubDefinition.WikidataQid` (no extra indirection needed
— a club competition's winner item IS the club item):

```sparql
?player wdt:P106 wd:Q937857.
?player p:P54 ?clubStatement.
?clubStatement ps:P54 wd:{clubQid}.
MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
?player wdt:P1344 ?edition.
?edition wdt:P3450 wd:{trophyQid}.
?edition wdt:P1346 wd:{clubQid}.
```

The club variant deliberately keeps the `P54` club-membership clause
*alongside* the `P1344`/`P1346` edition-winner join, not instead of it —
`P1344` alone ("participated in this Champions League edition") is true
for every player on every club that reached that edition's group stage,
not just the winning squad; requiring club membership too narrows this
back down to "played for the specific club that won it." This is not
airtight (see Consequences) but is the same class of best-effort
narrowing REQ-109 already accepts for the senior/youth-team QID problem,
not a new kind of risk.

**`TrophyDefinition.WikidataQid` for a team trophy must resolve to the
general competition series item** (e.g. "FIFA World Cup," "UEFA Champions
League"), never a specific edition — the query joins editions to it via
`P3450`, so a per-edition QID would silently match nothing. This is a
semantic difference from what the same field means for `IsTeamTrophy =
false` rows (where it's the award item itself, queried directly via
`P166`) — both are documented on `TrophyDefinition.WikidataQid` itself.

**No new database schema.** `IsTeamTrophy` already exists (S-031);
`WikidataQid` already exists and is reused with the series-item meaning
above; no edition QIDs are ever stored — the edition→series and
edition→winner joins happen entirely inside the SPARQL query, resolved
fresh each time, consistent with this system's "never persist an
intermediate Wikidata lookup artifact" pattern.

**ADR-0035 follow-up resolved in the same story.** ADR-0035 explicitly
flagged that `LookupAndPersistTrophyCountryAsync` doesn't honor
`CountryDefinition.UsesCountryForSportProperty`, tracked as unreachable
follow-up work "whenever the trophy pool grows enough to make the
pairing reachable" — with an explicit instruction to update that ADR's
own follow-up note, not just the code, whenever this happens. Seeding
World Cup and Champions League (below) grows the trophy pool from 1 to 3,
which crosses `SelectPairing`'s `trophyCount >= size` feasibility check
for the default `GridSize = 3` — Country × Trophy becomes reachable in
production for the first time as a direct, foreseeable consequence of
this story's own seeding. Fixing it here (thread
`UsesCountryForSportProperty` through `LookupAndPersistTrophyCountryAsync`,
same as `LookupAndPersistAsync` already does) is therefore in scope for
this story, not separate follow-up work — shipping the trophy-count
increase without it would knowingly reintroduce the exact bug ADR-0035
already named and described.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Store per-edition winner QIDs as new reference data (e.g. a `TrophyWinner` table: trophy, country/club, edition year) | No live join needed at query time; simplest SPARQL per lookup | Reintroduces a bulk-import-shaped problem ADR-0001/ADR-0012 both avoid for exactly this reason — "who won which edition" is itself data that needs sourcing, curating, and keeping current as new tournaments are played, for a system whose whole design principle is "resolve once from Wikidata, don't hand-maintain a shadow copy of data Wikidata already has" | Directly contradicts this system's established data-sourcing philosophy; the live `P1346` join costs one more triple pattern, not a new maintenance burden |
| Drop the `P54`/club-membership clause from the Club variant, rely on `P1344` alone | Simpler query, fewer false negatives if a player's `P54` qualifiers for that specific season are incomplete | Real false positives: any player who "participated in" a Champions League edition for a club eliminated in an earlier round would incorrectly satisfy "won it" for whichever club they're being checked against, since `P1346`/`P1344` alone don't scope the edition to one team's specific run through it | The false-positive direction is worse than the false-negative direction for a game whose scoring correctness is REQ-203's core contract — narrowing with `P54` is the safer default, same trade-off REQ-109 already accepts for youth-academy QIDs |
| Match the winner directly against the country item (`?edition wdt:P1346 wd:{countryQid}`), skip the `P1532` indirection | One fewer join | Wrong in practice for the World Cup: `P1346` values are national-team items (e.g. "Brazil national football team"), not the country item (Q155) itself — a direct QID match would silently return zero winners for every country, the query would "work" (no error) but never match anything, a much harder failure to notice than an explicit join | `P1532` is exactly the property Wikidata itself provides to bridge a national-team item back to the country/nation it represents (the same property ADR-0035 already established as reliable for this purpose) |
| A single `QueryTeamTrophyIntersectionAsync` method with an internal `useCountryForSportProperty` flag, instead of two methods | Fewer public methods | Breaks the established precedent (`QueryCountryClubIntersectionAsync` vs. `QueryNationalTeamClubIntersectionAsync`, ADR-0035) of one method per query shape, decided by the caller from data it already has — introducing a different dispatch style for team trophies than for every other P27-vs-P1532 split would be a real, unnecessary inconsistency | Matching the existing method-per-shape precedent keeps `WikidataLookupService` the single, consistent dispatch point ADR-0035 already established |

## Consequences

- Positive: completes REQ-108's full v1 category-type definition —
  World Cup and Champions League join Ballon d'Or as seeded values;
  Trophy pairing (Country × Trophy, Club × Trophy) becomes reachable in
  production for the first time, not just mechanically wired up.
- Positive: no schema change, no new persisted "trophy winner" data to
  keep in sync — the edition/winner join is resolved live, same
  resolve-once-per-query-not-per-cache-miss philosophy as every other
  intersection query (results are still cached into `PlayerAttribute`
  afterward, same as always).
- Negative / trade-off accepted: the Club variant's `P1344`+`P54`
  combination is a best-effort narrowing, not a guarantee — a player
  whose `P54` club-membership qualifiers are missing or wrong for the
  specific season, but who has a `P1344` statement for that edition
  (e.g. loaned elsewhere mid-run, or Wikidata data simply incomplete),
  could still be a false positive or false negative. Not solved with
  season/date-qualifier matching between `P54`'s `P580`/`P582`
  qualifiers and the edition's own year — that's real added complexity
  this story doesn't take on; flagged as a known, accepted gap, same
  class as REQ-109's senior/youth-team caveat.
- Negative / trade-off accepted: `P1344` ("participant of") coverage for
  club-competition squads is less consistently populated on Wikidata than
  international-tournament participation (which is well-documented for
  essentially every World Cup squad member) — this could make Champions
  League Trophy × Club pairings return sparser results than World Cup
  Trophy × Country ones in practice. Not verifiable from this sandbox
  (no network access to wikidata.org); if real play shows Champions
  League trophy cells consistently starved of matches, that's a data-
  coverage problem for a future story to investigate, not a sign this
  query shape is wrong.
- Negative / trade-off accepted (see ADR-0035 update): fixing the
  `UsesCountryForSportProperty` gap for Trophy × Country in this same
  story is a deliberate scope decision, not creep — shipping the trophy-
  pool growth without it would have shipped a known bug into newly-
  reachable production code.
- Follow-up: the World Cup (`Q19317` — training-knowledge guess) and
  Champions League (`Q18756` — training-knowledge guess) series QIDs, and
  the `P1344`/`P3450`/`P1346`/`P1532` property IDs this whole design
  depends on, were **not independently verified against live Wikidata
  pages this session** — same sandbox limitation already documented for
  every prior QID in this codebase (Ballon d'Or, four club QIDs that
  turned out wrong, four home-nation QIDs). A human must verify all of
  these against live Wikidata pages before this is relied on in
  production. If `P3450` turns out not to be the property actually used
  to link editions to series for one or both competitions on Wikidata
  (editions are sometimes modeled with `P361` "part of" instead, and
  modeling consistency across competitions/years is not guaranteed), the
  query will simply return no matches for that trophy rather than erring
  — REQ-101's retry logic absorbs this the same way it absorbs any other
  sparse-match pairing, but it should be checked against real data before
  trusting the feature works as designed.

## For AI agents

A team-competition trophy (`TrophyDefinition.IsTeamTrophy = true`) is
never queried via `P166` — that property is specific to individual
awards (S-031's scope) and does not exist for team competitions on
Wikidata. Do not add a `P166` statement expectation for World Cup/
Champions League seed rows. `TrophyDefinition.WikidataQid` for a team
trophy must be the competition **series** item, never a specific edition
— if you need an edition-specific QID for anything, that's a sign this
design is being misapplied. The player-side `P27`-vs-`P1532` choice and
the winner-side `P1532` join are two independent uses of the same
property for two different entities in the same query — do not collapse
them into one branch or assume they always agree. If you touch
`LookupAndPersistTrophyCountryAsync`, keep `UsesCountryForSportProperty`
threaded through per this ADR's decision — do not silently drop it back
to always-`P27`. If code you are about to write would contradict this
decision, stop and flag it rather than silently working around it.
