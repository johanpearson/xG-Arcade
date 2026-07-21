# ADR-0035: National teams (P1532) are a per-row flag on `CountryDefinition`, not a separate category type

- **Status:** Accepted
- **Date:** 2026-07-21
- **Related requirements:** REQ-114
- **Related components:** COMP-05 (Games.XGGrid), COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients)

## Context

`MVP-SCOPE.md`'s Tier 1 backlog has always flagged "National teams as
distinct footballing entities" (England, Scotland, Wales, Northern Ireland)
as a likely future addition, with the mechanical direction already sketched
out: none of the four home nations are sovereign states, so they can't be
queried via Wikidata's `P27` ("country of citizenship") the way every other
seeded country can — English/Scottish/Welsh/Northern Irish players' `P27`
is uniformly United Kingdom (`Q145`, already seeded). The property that
actually means "country represented in international competition" is
`P1532` ("country for sport"). This was pulled forward by explicit product
decision (not a triggered event from `MVP-SCOPE.md`'s own trigger list).

Two real questions had to be settled:

1. **Is a home nation a new category type, or a new value within the
   existing "Country" category type?** Wikidata gives a strong answer here:
   `P1532` is a different *query property* for an existing kind of thing
   ("what country/team does this player represent"), not a different kind
   of category. A football trivia game's "England" card means exactly the
   same thing as "France" — a country a player represented — the only
   difference is which Wikidata property answers "did this player
   represent it."
2. **Where does the P27-vs-P1532 decision get made, and how far does it
   need to be threaded?** `GridGameModule`'s generation and guess-time
   fallback paths both eventually call
   `IWikidataLookupService.LookupAndPersistAsync(CountryDefinition, ClubDefinition, ...)`
   for every Country × Club pairing, regardless of category type — whatever
   the mechanism, it must not force `GridGameModule` to special-case a
   national-team row.

## Decision

**Category type:** England/Scotland/Wales/Northern Ireland are seeded as
four *additional* `CountryDefinition` rows, existing alongside — never
replacing — United Kingdom. `CountryDefinition` gains one new field,
`UsesCountryForSportProperty` (`bool`, default `false`), set `true` only
for these four rows. Every other seeded country keeps the default and is
completely unaffected. This keeps "Country" a single category type: no
changes to `CategoryPairingRules`, `SelectPairing`, or any pairing-selection
logic in `GridGameModule` — a flagged country is picked, paired, and
validated exactly like any other `CountryDefinition` row. Matched players
persist under the same `PlayerAttribute.AttributeType = "nationality"`
vocabulary as every other country: "England" is just another value in that
vocabulary, the same way "United Kingdom" already is.

**Query dispatch:** `IWikidataClient` gains a second query method,
`QueryNationalTeamClubIntersectionAsync` (`P1532`-based), parallel to —
never replacing — `QueryCountryClubIntersectionAsync` (`P27`-based).
`WikidataLookupService.LookupAndPersistAsync` is the single place the
choice between them is made: it checks
`country.UsesCountryForSportProperty` and calls the matching method. This
is the entry point every caller already uses for Country × Club, so
`GridGameModule`'s dispatch call site needs no change at all — only the
`CountryDefinition` it constructs needs the flag threaded through
correctly.

**Threading the flag from generation to dispatch:** `GridGameModule`'s
internal `CategoryCandidate` record struct (already used to abstract
Country/Club/Trophy row/column candidates uniformly) gains a third field,
`UsesCountryForSportProperty` (default `false`, meaningless for Club/Trophy
candidates). It's populated once, at the point each candidate is first read
from `CategoryValueRepository` (`GenerateInstanceAsync`'s initial country
fetch, and `ResolveCandidateAsync`'s per-guess re-fetch for REQ-211's
fallback), and carried unchanged through `PickHeadersAsync`/
`GetMatchCountAsync`/`LookupLiveMatchesAsync` to the point a `CountryDefinition`
is reconstructed for the `LookupAndPersistAsync` call.

The alternative — re-resolving the full `CountryDefinition` row by name at
the dispatch site inside `LookupLiveMatchesAsync`, instead of extending
`CategoryCandidate` — was rejected: `ResolveCandidateAsync` (the guess-time
path) already does exactly one such re-fetch per guess, which is cheap, but
`LookupLiveMatchesAsync` is also called from `GetMatchCountAsync`, inside
`PickHeadersAsync`'s hot loop (once per candidate tried during generation).
Adding a repository round-trip there for every candidate, just to recover a
`bool` this codebase already had in hand two calls up the stack, is a real,
avoidable extra query cost for no correctness benefit — extending the
already-passed-around `CategoryCandidate` is the smaller, cleaner diff.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Extend `CountryDefinition` with a per-row flag; extend `CategoryCandidate` to carry it (chosen) | Zero change to pairing logic, `CategoryPairingRules`, or `GridGameModule`'s dispatch call site; reuses the existing "nationality" attribute vocabulary; the query-property decision lives in exactly one place (`WikidataLookupService.LookupAndPersistAsync`) | `CategoryCandidate` carries a field that's meaningless for two of its three uses (Club/Trophy) | The field costs nothing to ignore, and the alternative (a new category type) costs a lot more surface area for a distinction Wikidata itself treats as "same kind of thing, different property" |
| A separate `NationalTeamDefinition` table/category type | Cleanly separates "sovereign state" from "footballing entity" if that distinction ever needs to grow (e.g. more properties, richer metadata) | A new category type ripples through `CategoryPairingRules`, `SelectPairing`'s feasibility matrix, `GridGameModule.PoolFor`/`MapAttributeType`, a new repository method, a new `IWikidataLookupService` method pair, and a REQ-107-style pairing-ban question ("can a national team pair with United Kingdom on the same axis?") that doesn't actually exist for this data — England and United Kingdom are just two different `CountryDefinition.Name` values, never compared against each other by category type | The problem is genuinely "same category, different query property for four rows," not "a new kind of category" — modeling it as the latter adds real, ongoing maintenance surface (every future Country-touching change would need a "...and TrophyDefinition-shaped NationalTeamDefinition too?" check) for no behavioral benefit this system needs today |
| Re-resolve the full `CountryDefinition` row by name at each `LookupLiveMatchesAsync` dispatch, instead of extending `CategoryCandidate` | `CategoryCandidate` stays exactly as small as it was | An extra `CategoryValueRepository` round-trip per candidate tried inside `PickHeadersAsync`'s hot loop, recovering data the caller already had two stack frames up | Real, avoidable query-count regression during generation for a smaller struct — not worth it |

## Consequences

- Positive: no change to `CategoryPairingRules`, `SelectPairing`, or any
  pairing-selection/validation logic — a home nation is indistinguishable
  from any other country everywhere except the one query-dispatch decision
  point. Citizenship (`P27`) and country-represented (`P1532`) stay two
  genuinely separate concepts at the data-fetch level, matching
  `MVP-SCOPE.md`'s own reasoning for dual nationals/naturalized players.
- Negative / trade-offs accepted: `CategoryCandidate` now carries a field
  that only one of its three uses (Country) ever reads — a small,
  deliberate impurity accepted in exchange for not re-fetching reference
  data inside generation's hot loop. `WikidataClient` now has two
  structurally similar SPARQL query builders (`P27`+`P54` vs. `P1532`+`P54`)
  that must be kept in sync if the shared `P54`/male/date-of-birth
  predicates (`BuildIntersectionQuery`) ever change — both already route
  through that one shared builder, so this risk is the same one every other
  intersection-query pair (Trophy × Country, Trophy × Club) already
  carries, not a new class of risk.
- Follow-up: `LookupAndPersistTrophyCountryAsync` (Country × Trophy) does
  **not** yet honor `UsesCountryForSportProperty` — a national-team country
  in that pairing would silently fall back to `P27` semantics. This is
  currently unreachable in production (the seeded trophy pool is too small
  for any Trophy pairing to ever be selected — see `GridGameModule.SelectPairing`'s
  own comment), so it's tracked as follow-up work, not fixed here. Extend
  `BuildTrophyCountryIntersectionQuery` with a `P1532` counterpart and
  thread the flag through that dispatch branch too whenever the trophy pool
  grows enough to make the pairing reachable.
- Follow-up: all four seeded QIDs (`Q21`/`Q22`/`Q25`/`Q26`) are
  training-knowledge values, not verified against live Wikidata pages this
  session (no network access to wikidata.org from this sandbox) — a human
  must verify them before this is relied on in a real deployment, same
  process S-037 already established for a wrong club QID.

## For AI agents

A home nation is a `CountryDefinition` row with `UsesCountryForSportProperty
= true`, never a new category type — do not add a `NationalTeamDefinition`
table, do not add a new `CategoryPairingRules` category, and do not special-
case a home nation anywhere in `GridGameModule`'s pairing-selection logic.
The P27-vs-P1532 decision is made in exactly one place,
`WikidataLookupService.LookupAndPersistAsync` — do not duplicate that branch
elsewhere, and do not merge the two query paths into one (citizenship and
country-represented must stay distinguishable at the data-fetch level, per
REQ-114). If you extend Country × Trophy to also honor this flag, update
this ADR's follow-up note, not just the code.
