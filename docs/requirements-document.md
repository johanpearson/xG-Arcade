---
doc_id: requirements-document
title: Requirements Document
version: "0.84"
status: draft
last_updated: 2026-07-20
owner: Johan
related_docs:
  - architecture-document.md
  - implementation-document.md
id_prefix: REQ
read_before: ["implementation-document.md", "architecture-document.md"]
update_when:
  - "A new user-facing behavior or business rule is added or changed"
  - "Acceptance criteria for an existing REQ change"
  - "A requirement is descoped or moved to a later phase"
---

# Requirements Document – xG Arcade (working title)

Version 0.75 · 2026-07-20

> **Naming note:** "xG Arcade" is a placeholder for the overall product name
> (users, leagues, rounds, scoring — everything shared across games).
> **xG Grid** is the name of the first game built on the xG Arcade, not the
> platform itself. When a real platform name is chosen, this is a
> find-and-replace of the word "xG Arcade" — the structure below does not change.

## 0. xG Arcade vs. game

- **The xG Arcade** owns: user accounts, authentication, leagues, the round
  scheduling engine, and the scoring/uniqueness engine. It has no football-
  specific logic of its own.
- **xG Grid** is the first game: an NxN grid where players combine two
  categories to guess a matching player. It plugs into the xG Arcade via the
  `IGameModule` interface (see `architecture-document.md`).
- A user has one xG Arcade account and can play any game hosted on it. Global
  and custom leagues, described below, belong to the xG Arcade, not to any
  single game — though a league may in practice only contain scores from
  games its members actually play.

> **For AI agents:** this document defines WHAT the system must do and how to
> verify it (testable acceptance criteria). It does not define HOW the system
> is built (see `implementation-document.md`) or WHY structural choices were
> made (see `architecture-document.md`). Every requirement has a stable ID
> (`REQ-xxx`) that must be referenced in test names and, where relevant, in
> commit messages and ADRs. Do not renumber existing REQ IDs — mark superseded
> ones as `Status: Deprecated` instead of deleting them.
>
> **This document describes the full system, not what's being built right
> now.** See `MVP-SCOPE.md` (repo root) for the actual build order — many
> requirements below are Tier 1/2 (deferred until real testing justifies
> them), not things to implement just because they're documented here.

## 1. Purpose and scope

The xG Arcade hosts football-based (and potentially other) guessing games
under one shared user base, league system, and scoring engine. **xG Grid**
is the first game: an NxN grid where the player combines two categories
(e.g. country × club) and guesses a player who satisfies both. Points are
awarded based on how unique the guess is compared to other players' guesses
during the same round.

The xG Arcade itself must be built so it can host additional games in the
future (shared user/league/scoring engine, pluggable game modules) without
xG Grid-specific logic leaking into that shared core.

**MVP scope:** the xG Arcade's core (accounts, Global League, custom leagues,
round engine, scoring engine) plus one game (xG Grid), live uniqueness
percentage, admin verification of data.

**Out of scope for v1:** paid tiers, mobile app, social sharing features, a
second game (the architecture must not block this later, but it is not built now).

## 2. Definitions

| Term | Meaning |
|---|---|
| Grid | An NxN grid with categories on rows and columns |
| Cell | The intersection of one row category and one column category |
| Round | A time-bound instance of a grid, with a start and end time |
| Guess | A player's answer for a cell |
| Uniqueness score | Share of *other* correct guessers who did NOT give the same answer for a cell — the guesser's own guess is excluded from the comparison, so a lone correct guesser is 100% (not 0%) unique (ADR-0020). **Note the points derived from it are inverted (ADR-0021): a HIGHER uniqueness score yields FEWER points — see "Points"/"Score" below.** |
| Points / Score | xG Arcade is scored like golf: LOWER is better, and a player's (or the leaderboard's) goal is to MINIMIZE their total, never maximize it (ADR-0021). A cell's most-unique possible correct answer scores 0 (best); an incorrect guess, an unanswered cell (for a round the player participated in), or the most commonly-shared correct answer all score `ScoringRules.MaxPointsPerCell` (worst, 100 by default) |
| Override | A manually corrected data point that always wins over synced data |
| Unverified data | Data fetched live during grid generation, not yet reviewed by an admin |

## 3. Data strategy note (context for requirements below)

The platform does **not** require a large, pre-seeded database before launch.
Player/attribute data is built incrementally, on demand: when a grid needs a
combination that isn't in the local cache yet, the system fetches it live,
stores it as `unverified`, and reuses it for all future grids. The local
store functions purely as a growing cache — this keeps infrastructure small,
keeps answer-checking fast and consistent for users, and avoids repeatedly
hitting rate-limited external APIs for the same lookups. See requirements
REQ-101 and REQ-103 below.

## 4. Functional requirements

Each requirement has a unique ID, a user story, testable acceptance criteria
in Given/When/Then format, and the test level that primarily verifies it
(Unit / API / UI / Manual).

---

### 4.1 Grid generation

**REQ-101 – Generate a valid grid**
> As a player, I want to always be presented with a grid where every cell has
> at least one correct answer, so that I never get stuck on an unsolvable cell.

- **Status note (2026-07-13, ADR-0023):** a real dev-environment run chained
  enough live-lookup misses to run for 4+ minutes before an infrastructure
  ingress killed the request — attempt count alone (`MaxAttempts`) never
  bounds wall-clock time in practice, since it's far higher than the
  reference-data pool can ever supply attempts for. `GridGameModule.PickHeadersAsync`
  now also aborts once `MaxDuration` (configurable, default 90s) of
  wall-clock time elapses, so generation always resolves — success or a
  clean, logged failure — within a bounded time, well under any known
  infrastructure request timeout. This is an additional abort condition,
  not a replacement for the attempt-count one below.
- Given an NxN grid is being generated with randomized categories per row/column
- When the combination of a row and column category for a cell has fewer than
  `MIN_VALID_ANSWERS` (configurable, default 5) matching players in the local cache
- Then that combination is discarded and a new combination is randomized for that cell
- And this repeats until all N×N cells are valid, or a maximum number of
  attempts (`MAX_ATTEMPTS`, configurable, default 500) is reached, or a
  maximum wall-clock duration (`MAX_DURATION`, configurable, default 90s,
  ADR-0023) elapses, at which point generation aborts and logs an error

**Test level:** Unit (combination validation, retry logic, MaxDuration
abort), API (endpoint never returns a grid with an invalid cell)

**REQ-102 – Configurable grid size**
> As an admin, I want to configure grid size (3x3, 4x4, 5x5) per GridTemplate,
> so the game can be varied over time.

- **Status: Partially implemented (Tier 0, S-007).** There is no admin CRUD
  for `GridTemplate` yet — the `size = N` part of this requirement is
  satisfied (the non-Production-only `POST /internal/grid/generate`
  endpoint, `XGArcade.Api.Grid.InternalGridEndpoints`, accepts a `Size` of
  3/4/5 and produces exactly N×N cells with N unique row and N unique
  column categories, per the acceptance criteria below), but "as an admin,
  I want to configure" is not: the endpoint find-or-creates a `GridTemplate`
  for the requested size on demand rather than an admin creating/managing
  templates through any dedicated interface. The rest of this requirement's
  acceptance criteria are recorded below as the full/long-term definition,
  not a claim of current behavior.
- Given a GridTemplate with `size = N`
- When a new grid is generated from this template
- Then exactly N×N cells are created, with N unique row categories and N
  unique column categories (no row category may be identical to a column
  category in the same grid)

**Test level:** Unit, API

**REQ-103 – Live-fetch fallback for missing data**
> As the system, I want to look up data live when a combination is missing
> from the local cache, so that more combinations become possible without
> blocking generation, and without requiring a large upfront import.

- **Status: Partially implemented (Tier 0, S-006/S-007).** Only the
  Wikidata half is built: `WikidataClient`/`WikidataLookupService`
  (`XGArcade.DataSync.Wikidata`) run the SPARQL intersection query
  (implementation-document.md §6a), persist matches, and upsert
  `skos:altLabel` results into `PlayerAlias`. The API-Football fallback
  client does not exist yet (Tier 1) — `GridGameModule.GetMatchCountAsync`
  (`XGArcade.Games.XGGrid`) only ever calls `WikidataLookupService`; there
  is no "Wikidata timed out/errored, try API-Football" branch to call yet.
  As of S-007, grid generation is now the real caller: a local cache miss
  (`CountPlayersWithBothAttributesAsync` returns 0) triggers a live
  Wikidata lookup during `GenerateInstanceAsync`, and a genuine 0-match
  result (Wikidata included) is treated as an ordinary failed candidate,
  discarded and retried per REQ-101 — this is the "if neither source finds
  a match, the combination is discarded" clause below, minus the
  "neither source" part, since there's still only one source. The rest of
  this requirement's acceptance criteria (the Wikidata/API-Football
  waterfall itself, `confidence`/`source` bookkeeping beyond what
  S-006 already persists) are recorded below as the full/long-term
  definition, not a claim of current behavior.
- **S-052/ADR-0029 deviation from the criteria below:** a match found this
  way is now stored `confidence="verified"`, not `"unverified"` as line
  "any matches are stored..." below still literally reads. This is a
  deliberate, later revision (not an oversight) — see ADR-0029: a routine
  cache-miss lookup is the same vetted query Tier 0's Wikidata-first design
  already treats as ground truth, so REQ-503's admin review queue no longer
  needs to include it. `confidence="unverified"` is still exactly right for
  REQ-211's guess-time fallback (a different call path, unchanged by this).
- **Status note (2026-07-20 — supersedes the last sentence of the bullet
  above; kept for history, not deleted):** "`confidence="unverified"` is
  still exactly right for REQ-211's guess-time fallback" is no longer
  current — see REQ-211's own 2026-07-20 status note (this reverses
  ADR-0029's fallback-specific carve-out; a new ADR superseding ADR-0029 is
  pending). As of that decision, **every** Wikidata-sourced write,
  including REQ-211's guess-time fallback, persists `confidence="verified"`
  immediately. This REQ (REQ-103, routine sync) is unaffected in
  substance — it already wrote `"verified"` under ADR-0029 and continues to.
- Given a combination has no match in the local cache
- When the system performs a live lookup against external sources
- Then Wikidata is tried first, with a timeout — it isn't meaningfully
  capped for this system's query volume, unlike the fallback source
- And API-Football is tried only if Wikidata times out, errors, or
  genuinely has no matching data (ADR-0011) — never queried first, never
  queried in parallel with Wikidata by default
- And any matches are stored in `PlayerData` with `confidence="unverified"`
  and `source` set to the specific provider that resolved it (`"wikidata"`
  or `"api_football"` — see implementation-document.md §5 for the full
  `Source` enum; there is no single generic `"live_lookup"` value)
- And the cell may be used in the grid even while unverified, but is flagged internally
- And if neither source finds a match, the combination is discarded (same
  flow as REQ-101)

**Test level:** Unit (mocked external sources, including the
Wikidata-fails/API-Football-fallback branch), API

**REQ-107 – Category pairing constraint**
> As a player, I want every grid to be answerable with a real footballer, so
> the puzzle stays fair and interesting rather than impossible or trivial.

- **Status note (2026-07-20):** Club × Club (`docs/backlog.md` S-030) and
  every Trophy pairing (S-031) are both implemented.
  `GridGameModule.GenerateInstanceAsync` picks a pairing per instance
  (`SelectPairing`) uniformly at random among whichever of five candidates —
  Country × Club, Club × Club, Country × Trophy, Club × Trophy, Trophy ×
  Trophy — the seeded reference data can support (a same-type pairing needs
  at least `2 × Size` distinct values, since REQ-102 forbids a value
  appearing on both axes; a mixed pairing just needs `>= Size` in each
  pool), falling back deterministically whenever only a subset is feasible.
  This generalizes S-030's two-way coin flip to an N-way choice. This was a
  scope restriction in `MVP-SCOPE.md`, not a limit this REQ ever imposed —
  `CategoryPairingRules.IsAllowedPairing` already permitted every one of
  these pairings before S-030/S-031 built the selection logic for them.
  **Load-bearing caveat:** with only one trophy seeded in production
  (Ballon d'Or, `ReferenceDataSeeder`), `trophyCount(1)` can never clear
  `Size` for any realistic grid, so every Trophy pairing is structurally
  infeasible today — Trophy is mechanically wired up but will not actually
  be selected until more trophies are added as reference data (a data
  change, not a code change, matching REQ-108's own design intent). See
  REQ-108's own status note for that requirement's full detail.
- Given a grid is being generated
- When row and column categories are assigned
- Then a Country × Country pairing is never generated (two nationality
  categories together produce cells with no fair, well-defined answer)
- And Club × Club, Club × Country, Trophy × Club, Trophy × Country, and
  Trophy × Trophy pairings are all allowed — v1 category types are Country,
  Club, and Trophy (REQ-108)
- And this constraint is checked before the matching-count check in
  REQ-101, not as a separate late-stage filter, so an invalid pairing is
  never even attempted against the data
- And an overly narrow Trophy × Trophy or Trophy × Club pairing that
  happens to have too few valid answers is handled by REQ-101's existing
  minimum-match retry logic, not a separate categorical ban — only
  Country × Country is banned outright, since that's a structural property
  (most players hold one nationality), not a data-sparsity issue

**Test level:** Unit

**REQ-108 – Trophy as a v1 category type**
> As a player, I want trophies to be a category alongside country and club,
> so grids have more variety than just nationality/club combinations.

- **Status: Implemented (Tier 0, S-031, 2026-07-20), narrower than the
  acceptance criteria below.** `TrophyDefinition` gained a `(Name)` unique
  index; `ReferenceDataSeeder` seeds exactly one trophy, **Ballon d'Or**,
  an individual award resolvable via Wikidata's `P166` ("award received") —
  the same simple query shape as the existing Country/Club intersection
  query (`WikidataClient.QueryTrophyCountryIntersectionAsync`/
  `QueryTrophyClubIntersectionAsync`, `IWikidataLookupService.
  LookupAndPersistTrophyCountryAsync`/`LookupAndPersistTrophyClubAsync`).
  `GridGameModule` treats Trophy as a third category type throughout
  generation, guess-scoring, and REQ-211's guess-time live-lookup fallback
  (Trophy × Trophy has no dedicated live-lookup method — see REQ-107's own
  status note, it's unreachable in practice anyway). Team-competition
  trophies (World Cup, Champions League, the rest of the example list
  below) need a structurally different query (squad membership + tournament
  result, no single property linking a player to "won this tournament") and
  remain deferred to a follow-up story, not part of S-031.
  **Two caveats, both load-bearing for what actually ships:**
  (1) **Structurally dormant in production** — `ReferenceDataSeeder` seeds
  only this one trophy, and `trophyCount(1)` can never clear `Size` for any
  realistic grid (`GridGameModule.SelectPairing`), so no Trophy pairing can
  actually be selected yet; this is expected per this REQ's own "a data
  change, not a code change" design, proven by injecting a larger fake
  trophy pool in `GridGameModuleTests`, not by anything production data
  will trigger today. (2) **Ballon d'Or's QID (`Q166177`) was not
  independently verified against a live Wikidata page this session** — this
  sandbox cannot reach wikidata.org (same limitation `ReferenceDataSeeder`'s
  own doc comment already documents for S-036/S-037's guessed club QIDs,
  4 of which turned out wrong) — a human must check it against the live
  page before this is relied on in a real deployment.
- Given the platform's list of recognized trophies (e.g. FIFA World Cup,
  UEFA Champions League, Ballon d'Or, UEFA European Championship, Copa
  América — an initial, extensible list, not hardcoded into game logic)
- When a grid is generated with a Trophy category
- Then "satisfies this category" means the player has a `PlayerAttribute`
  (or override) record of type `trophy` with that specific trophy as the value
- And the trophy list is stored as reference data (a `TrophyDefinition`
  table), so adding a new recognized trophy later is a data change, not a
  code change

**Test level:** Unit, API

**REQ-109 – Category value reference tables with resolved external IDs**
> As the system, I want a clear, curated source of truth for which
> countries/clubs/trophies can appear as category values, each with its
> external-source IDs resolved once, so grid generation has something
> concrete to pick from and live lookups can actually be constructed.

- Given `CountryDefinition`, `ClubDefinition`, and `TrophyDefinition`
  reference tables (ADR-0012)
- When grid generation picks a candidate row or column category
- Then the value is picked from these reference tables, never derived ad
  hoc from whatever happens to already be in `PlayerAttribute`
- And each value's external IDs (Wikidata QID, and for clubs, an
  API-Football team ID) are resolved once — countries via a one-time bulk
  import (a deliberate, narrow exception to REQ-103's "no bulk import"
  principle, given how small and stable the set of countries is), clubs
  incrementally when an admin adds one as an allowed value, trophies
  manually given the tiny size of that table
- And a club's `WikidataQid` must resolve to the **senior/first-team**
  item specifically, not a generic club-family concept — this is what
  makes "played for this club" mean senior career, not youth academy,
  for clubs whose youth setup has its own distinct Wikidata item. This is
  a best-effort exclusion, not a guarantee (see `implementation-document.md`
  §6a for the known residual gap and its mitigation)
- And a category value with no resolved Wikidata QID yet is not an error —
  the live-lookup waterfall (REQ-103/REQ-211) simply skips Wikidata for
  that value and uses the API-Football fallback instead, which doesn't
  need a Wikidata QID

**Test level:** Unit (grid generation only ever picks from the reference
tables; a null QID correctly falls through to the API-Football path
without erroring), API

**REQ-110 – Proactive player-attribute cache warming**
> As the system, I want the local player-attribute cache filled for every
> reference category-value pair ahead of time, not only as a side effect of
> a live round-generation attempt, so a generation request only rarely
> needs to gamble on an uncached row/column combination.

- **Status: Implemented (Tier 0, S-036).** Direct follow-up to S-011's own
  deferred "cache pre-warming job" note and ADR-0023's logged follow-up —
  both predicted this exact gap; a real dev-environment run confirmed it on
  2026-07-13 (`GridGenerationException: "Ran out of candidates before
  completing the grid."` — see NOTES.md). `PlayerCacheWarmingService`
  (`XGArcade.Games.XGGrid`) iterates every Country × Club and Club × Club
  pair the reference tables (REQ-109) can produce and, for any pair not
  already at or above `MinValidAnswers`, triggers the same live-lookup path
  REQ-103 already uses — the only difference is *when* it runs (proactively,
  ahead of any real generation attempt) and *how* it's triggered.
- Given the reference `CountryDefinition`/`ClubDefinition` tables
- When the cache-warming job runs (`dotnet run -- warm-player-cache`,
  triggered manually via `warm-player-cache.yml` — **not** an HTTP
  endpoint against the deployed backend, and **not** on a recurring
  schedule; see ADR-0024 for why running inside a synchronous request or a
  fire-and-forget background task would both be unsafe for this specific
  hosting setup)
- Then every Country × Club pair and every unique Club × Club pair is
  checked, and any pair not already meeting `MinValidAnswers` triggers a
  live Wikidata lookup, persisted the same way REQ-103 already persists one
- And a pair already meeting `MinValidAnswers` is skipped, not re-queried —
  idempotent and safe to re-run
- And a pair cached *below* `MinValidAnswers` is **not** distinguished from
  a never-checked pair and is re-queried on every run — a known, accepted
  gap for this first pass (there's no persisted "checked, genuinely low"
  signal yet), not a correctness bug

**Test level:** Unit (`PlayerCacheWarmingServiceTests.cs` — every pair
gets checked exactly once per run; an already-valid pair is skipped; a
below-threshold pair is re-queried, not skipped)

**REQ-111 – Recovery from a corrected reference-data QID**
> As the system, I want to purge PlayerAttribute/PlayerData rows fetched
> under a club's previously-wrong Wikidata QID once that QID is corrected,
> so re-fetching against the corrected QID isn't silently blocked by
> leftover data that can't otherwise be told apart from correct data.

- **Status: Implemented (Tier 0, S-037).** `StaleClubAttributeCleaner`
  (`XGArcade.Data.Seeding`), run manually via the `clean-stale-club-attributes`
  CLI verb — a one-off maintenance tool, not wired into any automatic
  migrate-and-seed or scheduled run, and not idempotent-forever the way
  REQ-110's cache warming or the other Seeding backfillers are (a wrong-QID
  row is indistinguishable from a correct one after the fact, so there's no
  "already fixed" marker to detect and skip on). Must be run for the
  specific corrected club name(s) before the next REQ-110 cache-warming
  pass — running it after a fresh warming pass would incorrectly wipe the
  new, correct data too, since nothing here can tell old from new.
- **Extended (2026-07-17):** a second incident class motivated an
  all-clubs mode (`clean-stale-club-attributes --all-clubs`,
  `StaleClubAttributeCleaner.CleanAllSeededClubsAsync`): REQ-113's truthy
  `wdt:P54` query-*shape* bug tainted the cached data of **every** seeded
  club at once, not one club's wrong QID — and hand-typing every seeded
  club name is exactly the typo surface where one misspelled name silently
  stays stale (the named mode cannot distinguish a typo from a club with
  nothing to clean; both remove zero rows and report success). Same
  manual, deliberate-friction character as the named mode: still a
  one-off CLI verb run before the next REQ-110 warming pass, never wired
  into any automatic migrate-and-seed or scheduled run.
- Given a `ClubDefinition` row's `WikidataQid` was corrected (REQ-109)
  after `PlayerAttribute`/`PlayerData` rows were already fetched and
  persisted under its old, wrong QID
- When the cleanup tool is run for that club's name
- Then every `PlayerData` row of type `club` with that club as its value is
  deleted, and every derived `PlayerAttribute` row of type `club` with that
  value is deleted too — regardless of whether any individual row happens
  to be correct, since nothing in a persisted row distinguishes data
  fetched under the old QID from data fetched under the corrected one
- And club names not included in the run are left untouched
- And running the tool again once nothing is left to clean deletes zero
  rows and does not error
- And when run in all-clubs mode (the literal `--all-clubs` instead of a
  name list), the club-name list is resolved at runtime from the
  `ClubDefinition` reference table (REQ-109) — every seeded club's rows
  are cleaned, scoped by the reference table exactly as the named form is
  scoped by its list (never "every `club`-type row regardless of value"),
  and attribute types other than `club` are untouched — and the resolved
  names are reported back so an operator can verify what was swept
- And all-clubs mode run against an empty `ClubDefinition` table fails
  loudly (errors, deletes nothing, produces no success summary) — zero
  seeded clubs signals a wrong database or a never-seeded one, not a
  genuine "nothing to clean"
- And in the named comma-separated form, a token that looks like a flag
  rather than a club name (a `-`-prefixed token, e.g. a mistyped
  `--all-club`) fails loudly before any deletion — it must never be
  treated as an ordinary club name that matches zero rows and produce a
  plausible-looking "removed 0 rows" success

**Test level:** Unit (`StaleClubAttributeCleanerTests.cs` — removes stale
rows and leaves zero cached matches; scopes strictly to the named clubs and
to `AttributeType == "club"`; safe to re-run; all-clubs mode resolves names
from `ClubDefinition`, cleans only seeded clubs' `club`-type rows, and
throws on an empty `ClubDefinition` table rather than cleaning nothing
silently). The named-form flag guard lives in the CLI verb's argument
handling (`Program.cs`), which has no unit-test seam today — verified
manually until one exists

**REQ-112 – Player pool restricted to male, born in 1939 or later**
> As a player, I want every candidate answer the grid could ever accept to
> be a male footballer from a period I could plausibly recognize, so a
> correct answer never turns out to be an unfamiliar early-20th-century or
> women's-football player I had no realistic way to reason my way to.

- **Status: Implemented (Tier 0, S-038, ADR-0025).** Both `WikidataClient`
  SPARQL query builders (`BuildCountryClubIntersectionQuery`,
  `BuildClubClubIntersectionQuery`) require `?player wdt:P21 wd:Q6581097`
  (P21 = sex or gender, Q6581097 = male) and `?player wdt:P569
  ?dateOfBirth` with `FILTER(?dateOfBirth >=
  "1939-01-01T00:00:00Z"^^xsd:dateTime)` (P569 = date of birth). A fixed
  date, not a rolling window relative to "now" — an earlier draft of this
  requirement used a rolling "latest 100 years" window before the user
  corrected it to this fixed cutoff, so there is no clock/`TimeProvider`
  dependency involved.
- Given any Country×Club or Club×Club intersection query
- When the query runs against Wikidata
- Then only players who are recorded as male (P21 = Q6581097) and whose
  date of birth (P569) is on or after 1939-01-01 are ever returned as
  candidates
- And a player missing either P21 or P569 entirely is excluded, not
  included by default — the filter triples are non-optional
- **Data migration note (not itself a test-level acceptance criterion):**
  because neither sex nor date of birth was ever recorded on already-cached
  `Player`/`PlayerAttribute` rows, this couldn't be applied retroactively
  to existing data — the entire player pool was purged
  (`purge-player-pool` CLI verb, ADR-0025) and rebuilt from scratch via a
  fresh `warm-player-cache` run once this filter shipped.

**Test level:** Unit (`WikidataClientTests.cs` — sent SPARQL query contains
the P21 male triple; sent query's date-of-birth cutoff is exactly
`1939-01-01T00:00:00Z`, for both query builders)

**REQ-113 – Club membership means "ever played for," at any career point**
> As a player, I want a guess to be correct for a club cell whenever that
> player genuinely played for the club at any point in their senior career,
> so a real former club is never scored incorrect just because the player
> has since moved on.

- **Status: Implemented (Tier 0, 2026-07-17 bugfix).** This semantics was
  always the intent (REQ-109's senior-career aside was the only place it
  appeared in writing before this requirement), but a real production
  incident showed it was never pinned: both `WikidataClient` SPARQL
  intersection builders (`BuildCountryClubIntersectionQuery`,
  `BuildClubClubIntersectionQuery`, `XGArcade.DataSync.Wikidata`) used
  Wikidata's truthy `wdt:P54` shortcut, and the truthy graph contains only
  best-rank statements — the moment a player's *current* club is marked
  preferred rank (routine Wikidata editing practice), every normal-rank
  historical club silently vanished from the result, reducing "ever played
  for" to "currently plays for" for exactly those players. A genuinely
  correct guess (e.g. Sandro Tonali × AC Milan) scored incorrect. Fixed by
  querying the full statement path (`p:P54`/`ps:P54`), excluding only
  deprecated-rank statements, in both builders. Cached data fetched under
  the old query shape was incomplete for **every** seeded club at once —
  recovered via REQ-111's `--all-clubs` cleanup mode followed by a fresh
  REQ-110 cache-warming pass.
- Given a player whose Wikidata item records a club membership (P54)
  statement for a club, at any statement rank other than deprecated
- When candidates are fetched for a cell involving that club — Country ×
  Club or Club × Club, whether during grid generation (REQ-101/103/110) or
  a guess-time live lookup (REQ-211), all of which share the same two
  query builders
- Then that player is returned as a match for the club — a normal-rank
  historical spell counts exactly the same as a preferred-rank current one
- And marking a player's current club preferred rank must never suppress
  their normal-rank historical clubs: club membership must never be
  fetched through a best-rank-only view (Wikidata's truthy `wdt:P54` graph
  is exactly such a view and must not be used for P54)
- And a deprecated-rank P54 statement never counts — deprecated is
  Wikidata's "recorded but wrong" marker, not a historical spell
- And this ever-played-for rule is specific to club membership — the other
  properties these queries use (nationality, sex, date of birth) deliberately
  keep best-rank semantics, where "current/best-supported" is the intent
- And "played for" remains scoped to the senior/first team by REQ-109's
  QID-resolution rule — this requirement governs which membership
  statements count for a club, not which club entity the cell asks about

**Test level:** Unit (`WikidataClientTests.cs` query-shape tests — both
builders' sent SPARQL uses the full `p:P54`/`ps:P54` statement path with
only `DeprecatedRank` excluded, and never contains truthy `wdt:P54`)

---

### 4.2 Guesses and scoring

**REQ-201 – Submit a guess**
> As a player, I want to guess a player for a cell, so I can participate in
> the round.

- **Status: Implemented (Tier 0, S-009).** `GuessSubmissionService`
  (`XGArcade.Core.Scoring`) plus `POST /rounds/{roundId}/cells/{cellId}/guesses`
  (`XGArcade.Api.Guesses.GuessEndpoints`) satisfy every acceptance criterion
  below for Tier 0's scope: a `Guess` row is stored with `UserId`, `CellId`,
  `SubmittedName` (the "answer"), and `CreatedAt`; the unique
  `(RoundId, UserId, CellId)` index plus overwrite-on-resubmit logic enforce
  "one active guess per cell per round"; correctness is determined and
  returned in the same response, not deferred. What "correctness" itself can
  currently determine is Tier 0-scoped — see REQ-203/208/209's own status
  notes for what name-matching does and doesn't yet cover.
- Given an active (not closed) round and a logged-in player
- When the player submits a guess for a cell
- Then the guess is stored with `user_id`, `cell_id`, `answer`, `timestamp`
- And a player can only have one active guess per cell per round (a new guess
  replaces the previous one, subject to the attempt limit and lock rules
  in REQ-210)
- And correctness is determined and shown to the player immediately upon
  submission (REQ-203) — it is not withheld until the round closes

**Test level:** Unit, API

**REQ-202 – Guess locking**
> As a player, I want to know whether I can change my guess or not, so I'm
> not confused by the rules.

- **Status: Implemented (Tier 0, S-009).** `GuessSubmissionService` checks
  REQ-210's lock/attempt-cap first (always taking precedence, per the
  acceptance criteria below) and only then `Round.AllowGuessChange`; the API
  layer (`GuessEndpoints`) maps each of `RoundNotFound`/`RoundNotActive`/
  `CellAlreadySolved`/`NoAttemptsRemaining`/`GuessChangeNotAllowed` to a
  distinct `ProblemDetails` title/detail — never one generic message for
  every rejection reason.
- Given the configuration `allow_guess_change = true/false` per Round
- When a player attempts to change an already-submitted guess
- Then the system either allows the change (overwrite) or rejects it,
  depending on configuration, subject to REQ-210's attempt limit and
  correct-answer lock taking precedence regardless of this setting
- And every rejection (config-disabled, attempt limit reached, or already
  correct) shows a distinct, specific reason — never a generic "can't
  change" message that leaves the player guessing why

**Test level:** Unit, API

**REQ-203 – Guess correctness validation**
> As a player, I want to know if my guess is valid for the cell, so I know
> whether I'll receive points.

- **Status: Partially implemented (Tier 0, S-009).** The effective-data
  check itself is fully built and enforces override precedence exactly as
  described below: `IPlayerStoreRepository.HasEffectiveAttributeAsync`
  (`XGArcade.Data`) checks `PlayerOverride` first and only falls through to
  `PlayerAttribute` when no override exists for that field — see ADR-0015
  for the exact precedence semantics (an override replaces its entire
  attribute type, not one value within it). Correctness is determined and
  shown immediately, and a correct guess locks the cell immediately
  (REQ-210), both as described below. This check only ever runs against
  candidates found by REQ-208's Tier 0-scoped name matching (no alias
  table, no fuzzy tolerance — see REQ-208's own status note). **As of the
  2026-07-10 REQ-211 follow-up (ADR-0018), a guess that doesn't resolve
  from cached data is no longer scored incorrect outright** — it now
  triggers a Tier-0-simplified version of REQ-211's live lookup first (see
  REQ-211's own status note for exactly what differs from the full spec);
  only a guess that still doesn't resolve after that fallback is scored
  incorrect. The "incorrect guess scores worst" acceptance criterion below
  isn't independently verifiable yet since point computation itself doesn't
  exist until S-011.
- Given a guess for cell X
- When the answer is checked against the effective data (an override always
  takes precedence over synced/unverified data)
- Then the guess is marked `correct = true/false` and this result is
  displayed to the player immediately — not deferred to round close
- And an incorrect guess yields the WORST possible score
  (`ScoringRules.MaxPointsPerCell`) regardless of uniqueness — **ADR-0021:
  xG Arcade is scored like golf (lower is better, lowest total wins)**, so
  0 is the *best* possible score and an incorrect guess must never be able
  to tie it
- And a correct guess immediately locks the cell against further guesses
  (REQ-210), even though its final score isn't computed until the round
  closes (REQ-205) — "locked from further guessing" and "final score" are
  separate moments, not the same event

**Test level:** Unit

**REQ-204 – Live uniqueness percentage**
> As a player, I want to see how unique my guess is, updated live, so I get
> immediate feedback.

- **Status: Implemented (Tier 0, S-011; extended S-018, S-019, S-022 formula fix).**
  `UniquenessCalculator.Calculate`
  (`XGArcade.Core.Scoring`) is the one place this formula is written, shared
  by both the live read path below and REQ-205's round-close lock so they
  can never disagree. **S-022 correction (ADR-0020):** the formula now
  excludes the guesser's own guess from both sides of the ratio — an earlier
  version compared each guesser against the *whole* correct-guess
  population including themselves, which meant a lone (or first) correct
  guesser was trivially "100% of the population sharing their own answer"
  and scored 0% unique / 0 points, backwards from the intent that being the
  only correct answer for a cell should score maximally, not minimally. See
  ADR-0020 for the full rationale and the previously-recorded "not a bug"
  decision it reverses. `GET /rounds/current`
  (`XGArcade.Api.Rounds.RoundEndpoints`) computes `UniquePercent` live, on
  every request, for any cell the requesting player has correctly guessed —
  never persisted until the round closes. Frontend: `CellState.tsx` shows
  "X% unique" plus "updates until round closes on [date/time]" for state 1
  (correct + round active), per `design-document.md` SCREEN-01a.
- **S-019 addition:** the text above is no longer always rendered — every
  unresolved cell showing its full live text at once was cluttered at real
  grid sizes. `CellState.tsx`'s new `LiveMetaDisclosure` sub-component now
  gates it behind a tap/long-press (a tap toggles it open/closed) or, on
  desktop, hover/focus (transient — closes again on mouseleave/blur)
  interaction. The green live-dot plus the word "live" remain permanently
  visible regardless of reveal state — only the uniqueness %/points/
  round-end text is gated, and its wording is unchanged from before, so
  this changes *when* it renders, never whether it exists as text. The
  toggle is a real `<button>` (`aria-expanded` reflects open/closed,
  `aria-live="polite"` on the revealed panel) so keyboard/screen-reader
  users have the same access as mouse/touch, per `design-document.md`
  SCREEN-01a and §6.
- **S-018 addition:** the same endpoint also computes `LivePoints` alongside
  `UniquePercent`, via the new `ScoringRules.PointsFromUniqueScore(double
  uniqueScore)` (`XGArcade.Core.Scoring`) — extracted in this story as the
  one place the `uniqueScore → points` formula is written, called by
  both this live path and REQ-205's `ScoreLockingService.LockRoundScoresAsync`
  when it locks `FinalPoints`, so the two literally share code rather than
  independently matching formulas. `LivePoints` is null whenever
  `UniquePercent` is (i.e. until the guess is correct) and is recomputed on
  every request, never persisted.
  `CellState.tsx` renders it in state 1 only, as "~N pts estimated"
  (the "~" and "estimated" are both always present) alongside the existing
  "X% unique" line — deliberately different wording from state 4's plain
  "X% unique · Y pts", so it can never read as a preview or promise of
  REQ-205's locked score, only as a provisional value that can still
  change before the round closes.
- **ADR-0021 correction (lowest-wins scoring):** `PointsFromUniqueScore` was
  `round(uniqueScore * MaxPointsPerCell)` (higher uniqueness -> higher
  points -> "more points is better"); it is now `round((1 - uniqueScore) *
  MaxPointsPerCell)` — xG Arcade is scored like golf, so a rarer/more-unique
  answer scores FEWER points (0 for the rarest possible), not more, and the
  player's/leaderboard's goal is to MINIMIZE total points. `UniquePercent`
  itself is unaffected (still ADR-0020's corrected uniqueness fraction) —
  only its mapping to `LivePoints`/`FinalPoints` is inverted. The frontend's
  "~N pts estimated"/"X% unique · Y pts" wording is unchanged text, but
  SCREEN-01a/SCREEN-03 now also state the lowest-wins framing explicitly
  (design-document.md) so a player doesn't assume the opposite from habit.
- **S-029 wording correction:** direct player feedback found "X% unique"
  confusing once paired with ADR-0021's golf-style points — a *higher*
  uniqueness percentage means *fewer* points, the opposite of what "unique"
  suggests on its own. The frontend (`CellState.tsx`) now shows the same
  number reframed as its complement — "N% of others guessed this too,"
  where N = `round((1 - uniqueScore) * 100)` — so the percentage and the
  point value move in the same direction (more people guessing the same
  answer reads as more common, and scores worse under golf rules). No
  formula changed on the backend; `UniquePercent`/`LivePoints` are
  unchanged API fields, this is a frontend display-wording fix only, applied
  everywhere this value is shown (state 1's live disclosure and state 4's
  locked "final" text).
- **Built as (`docs/backlog.md` S-033, 2026-07-14):** SCREEN-01a's state 3
  ("Incorrect, no attempts remaining" — both guesses wrong, cell locked)
  used to render no point value at all, unlike every other locked state —
  flagged as an acknowledged gap on 2026-07-12, fixed here. Reported
  directly by a player looking at the deployed app: a locked-incorrect
  cell visibly showed nothing where a point value belonged, and the
  header's running total (REQ-206) silently excluded it too, so a wrong,
  locked-out guess looked like it counted for nothing rather than the
  guaranteed worst-case score ADR-0021 actually locks it at.
  `CellState.tsx`'s state-3 branch now renders `{MaxPointsPerCell} pts` —
  a new frontend-side `MAX_POINTS_PER_CELL` in `lib/scoringRules.ts`,
  mirroring `ScoringRules.MaxPointsPerCell` the same way
  `MAX_ATTEMPTS_PER_CELL` already mirrors its backend counterpart, display
  only, never enforcement. **Simplified same-day, same feedback round:**
  the first version also kept "no attempts left" alongside the points
  ("no attempts left · 100 pts", matching `design-document.md`'s
  then-current mock); direct follow-up feedback judged that qualifier
  redundant once the points value itself communicated "this cell is
  done," the same way a correct cell needs no "correct" label alongside
  its own points — dropped in favor of the identical minimal "✕/✓ +
  points" structure a correct cell already uses. State 4's incorrect
  outcome was brought in line the same way (also just `{MaxPointsPerCell}
  pts`, no "final") — round-closed data still isn't reachable via
  `GET /rounds/current` today (S-011 scope gap) so this can't be exercised
  live yet, but it costs nothing to keep it consistent with state 3 now
  that both use the same frontend-known constant rather than a
  `FinalPoints` value that would need to come from the API. See REQ-206
  for the matching running-total fix.
- **Built as (`docs/backlog.md` S-040, 2026-07-14):** direct product
  feedback (screenshots of the deployed app on a phone, and separately on a
  wide/"desktop site" viewport) found two real problems, both fixed in this
  story. (1) States 1 and 4 (the only two that show a player name) rendered
  the name unconditionally at rest — on a narrow viewport, a long name plus
  badge/checkmark/live text in one cell forced the row-header column past
  its intended 88px cap, and a country name could render one character per
  line. (2) On a wide viewport, the grid read as small and stuck top-left
  within `.app`'s `max-width: 900px` cap, never actually art-directed for
  desktop; only `design-document.md` SCREEN-01's mobile single-column mock
  was ever built, not its documented desktop side-panel variant (still
  deferred to its own future story, not built here). Fix for (1): states 1
  and 4 now show only their checkmark/✕ + points at rest, name and
  %-breakdown text gated behind a tap/hover/focus toggle, on every screen
  size, not mobile-only. State 1 extends the existing S-019 toggle
  (`CellState.tsx`, renamed `LiveMetaDisclosure` -> `useRevealDisclosure` +
  `RevealToggle` in this story so both states could share it) to also gate
  the name; the live point estimate moved the opposite direction, from
  revealed-only to always-visible at rest. State 4 gained the same toggle
  from scratch — its closed-round branch previously had no reveal mechanism
  at all. Shrinking typical cell content this way did **not** fully fix (1)
  on its own — root-causing past the symptom found `Grid.css`'s
  `.grid-table__row-header` `max-width: 88px` was never actually enforced,
  because plain (browser-default) table auto-layout sizes a column from the
  widest cell content anywhere in that column, not from the header's own
  `max-width`; `overflow-wrap: anywhere` then broke the oversized header
  text mid-word. Fixed with `table-layout: fixed` plus an explicit
  `<colgroup>`/`<col>` (`Grid.tsx`/`Grid.css`, ≤480px breakpoint only), so
  the row-header column's width is now genuinely sourced from its own
  `<col>`, not any cell's content — plus stacking the flag/badge above the
  header text (rather than beside it) so the name gets the header column's
  full width to wrap on. A second, unrelated pre-existing CSS bug was found
  and fixed along the way: `.cell-state__reveal-toggle`'s `font: inherit`
  shorthand was silently resetting the toggle button's font-size to the
  browser's ~16px default instead of `.cell-state__meta`'s intended
  11px/10px — invisible while the button only ever held a dot and the word
  "live," but exposed as bad text wrapping once this story made the live
  point estimate always-visible at rest. Fix for (2): a new
  `@media (min-width: 960px)` breakpoint widens `.app`'s `max-width` (900px
  -> 1200px) and grid cell/header sizing (44px -> 64px touch targets, more
  padding) — deliberately not the SCREEN-01 desktop side-panel variant,
  which remains its own deferred story. `design-document.md` SCREEN-01a's
  state 1 and state 4 mocks were updated to 0.17 before this code was
  written, per the usual design-then-build discipline. Tests:
  `CellState.test.tsx` gained 4 new REQ-204-named tests (both states'
  at-rest/revealed content, plus two edge-case fallbacks — no live point
  estimate yet, and state 4 with no `uniquePercent`/`finalPoints` at all)
  and updated 3 pre-existing tests for the behavior change.
- **Redesigned (2026-07-14), building on S-040:** product feedback judged
  the "live"/"final" distinction itself unnecessary noise — a player
  doesn't need a dot, the word "live," or a "~"/"estimated" qualifier to
  know a cell is correct; they need the point value, full stop. States 1
  and 4 now render identically in structure at rest: a checkmark plus a
  **points** value only (state 1's live estimate or state 4's locked
  `FinalPoints`, never both, never a percent). This supersedes three of
  this requirement's acceptance criteria below, kept (not deleted) and
  explicitly marked **Superseded 2026-07-14** rather than silently
  rewritten, per this document's ID-stability discipline — the
  "always as text, never icon-only" at-rest indicator, the S-019/S-040
  tap-or-hover/focus disclosure of the %-breakdown and round-end-time
  text, and the "unmistakably provisional" wording requirement. The
  %-breakdown/round-end content that disclosure used to hold does not
  reappear anywhere per-cell — it moves to a new, general explainer
  (REQ-213). What a locked+correct cell now discloses on click/tap instead
  is the guessed player's name, which is a new, separate requirement
  (REQ-212) — no longer part of what REQ-204 itself governs, since it's
  not about the live/final point value at all.
- Given at least one correct guess has been recorded for a cell
- When the player views their guess for that cell
- Then the system calculates
  `unique_percent = 1 - (players_with_the_same_correct_player / players_with_a_correct_guess_for_this_cell)`
  on every page load — **the denominator counts only correct guesses, one
  per player**. Incorrect guesses and burned attempts (REQ-210) never enter
  the calculation in either position: uniqueness measures how rare your
  answer is among people who solved the cell, and letting wrong guesses
  inflate the denominator would distort everyone's scores based on how
  much *failing* happened, which has nothing to do with rarity
- And where the simplified Tier 0 disambiguation accepts a guess matching
  multiple fitting players (see `MVP-SCOPE.md`), the stored `PlayerId` is
  chosen deterministically (lowest Id among fits) so identical guesses by
  different players always group as the same answer for uniqueness
- **Superseded 2026-07-14 (kept for history, no longer current behavior):**
  "the cell is permanently, visually marked as 'live' at rest — a small
  pulsing green dot plus the text 'live,' both always present regardless of
  whether the detail below is currently disclosed (REQ-204's 'always as
  text, never icon-only' rule applies to this at-rest indicator too)." No
  dot, no word "live," anywhere on the cell as of 2026-07-14 — see the
  current-behavior bullets below.
- **Superseded 2026-07-14 (kept for history, no longer current behavior):**
  "(S-019) the uniqueness percentage plus 'updates until the round closes
  on [date/time]' text is disclosed only on tap/long-press (toggles
  open/closed) or, on desktop, hover/focus (transient) — never shown for
  every unresolved cell at once by default — but is still always real text
  once revealed, never an icon standing in for it, and the toggle itself is
  a focusable control exposing `aria-expanded`/`aria-live` so a keyboard or
  screen-reader user has the same access as a mouse/touch user." This
  per-cell disclosure (and its hover/focus peek) no longer exists at all —
  see REQ-212 (click/tap now reveals the guessed player's name instead) and
  REQ-213 (the %-breakdown/round-end explanation now lives in a general
  explainer, not per cell).
- And the value MAY change between page loads before the round closes —
  still true and still worth surfacing, now covered by REQ-213's explainer
  rather than per-cell microcopy
- And (S-018) a live, provisional point estimate is computed via
  `ScoringRules.PointsFromUniqueScore` — the same shared method REQ-205
  calls to lock `FinalPoints` at round close, never a second,
  independently-written formula
- **Superseded 2026-07-14 (kept for history, no longer current behavior):**
  "that estimate is worded so it is unmistakably provisional (e.g. '~N pts
  estimated'), visually and textually distinct from REQ-205's locked 'Y
  pts' — it must never read as a preview or promise of the final score."
- **Current behavior (2026-07-14):** at rest, a locked+correct cell shows
  only a checkmark plus a **points** value — state 1 (correct, round still
  active) shows the live point estimate above, state 4 (correct, round
  closed) shows `FinalPoints` (REQ-205) — never a percent, never both
  values, and with no dot, icon, "~", or "estimated"/"final" qualifier
  distinguishing one from the other on the cell itself. A player cannot
  tell, from the cell alone, whether a shown point value is still live or
  already locked — that distinction is explained generally, once, via
  REQ-213, not repeated per cell
- And no per-cell disclosure of the %-breakdown or round-end time exists in
  either state — clicking/tapping a locked+correct cell instead reveals the
  guessed player's name (REQ-212); this requirement (REQ-204) governs only
  the live/locked point *value* and its calculation, not the name-reveal
  interaction
- **Status note (2026-07-19, `docs/backlog.md` S-048, direct user feedback
  on the shipped photo treatment — "at rest, only picture"):** the
  "Current behavior" bullet's "at rest, a locked+correct cell shows only a
  checkmark plus a points value" claim is no longer true for a correct
  cell that has a photo (REQ-214) — on a photo cell specifically, the
  checkmark and points value are no longer shown at rest at all, only the
  photo itself; they only appear (alongside the name, without a checkmark)
  once the player clicks/taps the cell to reveal it (REQ-212). This is a
  real, deliberate narrowing of what REQ-204 guarantees is always visible:
  before this story, the checkmark+points was the one thing a player could
  see about *every* correct cell without clicking, regardless of whether
  it had a photo; that "always visible without clicking" guarantee no
  longer holds for the photo case. The trade-off — a photo already implies
  a correct, locked guess even without the points value, so some "this
  cell is done" signal survives, just not the score — is the user's own
  explicit choice, not one this document is inventing a justification for;
  recorded here, and in `design-document.md` `SCREEN-01a`'s matching S-048
  status note, rather than left as an undocumented behavior change. The
  no-photo case is completely unaffected — it still shows the checkmark
  plus points value at rest exactly as this requirement originally
  specifies.

**Test level:** Unit (calculation logic), API, UI (state 1 and state 4 at
rest render identically in structure — checkmark + points, no live
indicator of any kind, no percent — for a cell with no photo; a cell with
a photo shows neither at rest as of S-048, see that status note)

**REQ-205 – Score locking at round close**
> As a player, I want my final score to be fixed once the round closes, so I
> know my result is permanent.

- **Status: Implemented (Tier 0, S-011; formula extraction S-018;
  lowest-wins correction S-028/ADR-0021; scheduled trigger S-029/ADR-0022).**
  `RoundCloseService`
  (`XGArcade.Core.Rounds`) pulls `EndTime` forward (idempotently — never
  later than what's already scheduled) to force immediate closure, then
  delegates the actual score locking to `IScoreLockingService`
  /`ScoreLockingService` (`XGArcade.Core.Scoring`, COMP-04), added S-011:
  for every `Guess` in the round, a correct guess gets
  `FinalUniquenessScore` (via the same `UniquenessCalculator` REQ-204 uses)
  and `FinalPoints = ScoringRules.PointsFromUniqueScore(uniqueScore)`
  (`= round((1 - uniqueScore) * MaxPointsPerCell)` as of ADR-0021,
  `MaxPointsPerCell = 100`, a Tier 0 default — no document specified an
  exact value); `PointsFromUniqueScore` was extracted in S-018 so this same
  call also backs REQ-204's live `LivePoints` estimate. **S-022 correction
  (ADR-0020):** `uniqueScore` itself excludes the guesser's own guess from
  the comparison (see REQ-204's status note) — a lone correct guesser has
  `FinalUniquenessScore = 1.0`. **S-028 correction (ADR-0021 — xG Arcade is
  scored like golf, lowest total wins):** that now locks `FinalPoints = 0`
  (the *best* score), not `MaxPointsPerCell`. An incorrect guess gets
  `FinalUniquenessScore = null` and `FinalPoints = MaxPointsPerCell` (the
  *worst* score — previously 0, which would otherwise tie a wrong answer
  with the best possible correct one under the lowest-wins model). This is
  idempotent and safe to call again on an already-closed round. **S-029
  correction (ADR-0022):** direct play-testing found that, in the deployed
  dev environment, a completed grid's score never actually reached the
  leaderboard — nothing had ever called round-close automatically, so
  `Guess.FinalPoints` stayed null forever and every leaderboard total summed
  to 0. `RoundGenerationService.GenerateNextRoundIfNeededAsync` (the code
  `generate-round.yml`'s cron actually invokes, Tier 0's only production-
  scheduled trigger point) now also closes a round's predecessor before
  deciding whether to generate a successor — see ADR-0022 for why the round
  to close is never `latest` itself. REQ-806's non-Production-only
  `POST /internal/test-data/force-close-round/{roundId}` still exists too,
  unchanged, for manual/E2E use. Trade-off accepted, not fixed: any rounds
  that had already ended-but-never-closed *before* this fix shipped need one
  additional `generate-round.yml` cron cycle each to catch up (or can be
  force-closed immediately by hand via the endpoint above) — see ADR-0022.
  The UI's "clearly different styling/icon" clause is built for `CellState`'s
  closed state (`cell-state--final`, "final" label, "X% unique · Y pts"),
  but that state is only reachable via constructed props in
  `CellState.test.tsx`, not via the live API (`GET /rounds/current` only
  ever returns an Active round — same gap S-010's backlog entry already
  recorded). The rest of this requirement's acceptance criteria are
  recorded below as the full/long-term definition.
- Given a Round whose `end_time` has passed
- When the scoring job runs for the round
- Then each guess's `final_uniqueness_score` and `final_points` are saved as
  permanent fields (separate from the live-calculated values)
- And the UI displays the locked score with clearly different styling/icon
  compared to live values
- And after locking, no new guesses are accepted for the round (see REQ-201)

**Test level:** Unit, API, UI

**REQ-206 – Total score per round**
> As a player, I want to see my total score for the whole grid, so I can
> compare myself to others.

- **Status note (Tier 0, S-011; unanswered-cell correction S-028/ADR-0021;
  live grid-screen total S-029).**
  `ScoreCalculator.CalculateTotalPoints`
  (`XGArcade.Core.Scoring`) implements this exact formula (`SUM(FinalPoints
  ?? 0)`) and is unit-tested against it directly. Its contribution is
  reflected correctly in the global leaderboard's running total (REQ-401,
  via `GuessRepository.GetTotalFinalPointsByUserIdsAsync`'s equivalent
  database-side `SUM`/`GROUP BY`), and — as of ADR-0022's round-closing fix
  — that total now actually reaches the leaderboard in the deployed
  environment, not just in theory. **S-029 addition:** `GridScreen.tsx` now
  also shows a running, live "~N pts estimated" total while a round is still
  active — summed client-side from the same per-cell `LivePoints` REQ-204
  already returns for each correctly-guessed cell, using the same "~"/
  "estimated" wording convention as a single cell's own live estimate, so it
  is never mistaken for the locked total REQ-205 computes at round close.
  This isn't REQ-206's own per-round locked total — that total is only
  computed once every cell in the round is locked (REQ-205), and even then
  it is never surfaced as a distinct per-round figure: it is only ever
  folded, uncredited, into the global leaderboard's all-time running sum
  (REQ-401). This live estimate is instead the closest a player can get to
  "my total for this grid" while still playing it. There is still no
  per-round-specific *locked* total surfaced anywhere via API or UI once a
  round closes — Tier 0 has no "view a specific closed round" screen at all
  (`GET /rounds/current` only ever returns an Active round), so there is
  still nowhere to show one closed round's final total distinctly from the
  leaderboard's all-time running total. Not a regression — revisit once/if
  a past-round-detail view exists. **Bugfix (2026-07-14), reported directly
  by a player:** the S-029 live total above only ever summed correctly-
  guessed cells' `LivePoints`, silently excluding any locked-incorrect cell
  entirely — so a wrong, no-attempts-left guess contributed nothing to the
  displayed total, reading as if it scored 0 (the *best* possible score
  under ADR-0021's golf model) rather than the guaranteed worst-case
  `MaxPointsPerCell` it's actually locked at. `GridScreen.tsx`'s total now
  also adds `MaxPointsPerCell` (`lib/scoringRules.ts`) for each cell whose
  guess is `locked && !isCorrect` — matching the same value SCREEN-01a's
  state 3 now displays per-cell (see REQ-204's matching S-033 fix above).
  A correct guess still awaiting its `LivePoints` (submitted this instant,
  not yet re-fetched) remains genuinely excluded, unchanged — only a
  guess whose outcome is already fully known (correct-with-a-value, or
  locked-incorrect) contributes. **S-028 correction (ADR-0021):** "unanswered cells count as 0 points" was true
  under the higher-is-better model (0 was the worst score, matching "no
  credit"); under lowest-wins, 0 is the *best* score, so leaving unanswered
  cells at 0 would make skipping a cell entirely optimal. `ScoreLockingService
  .MaterializeUnansweredCellsAsync` now creates a real, `MaxPointsPerCell`-scored
  `Guess` row for each cell a round *participant* (someone with at least one
  guess in that round) never attempted, at round close — resolved via a new
  `IGameModule.GetCellIdsAsync(instanceId)` method (never by Core reaching
  into a game-specific table directly, per ADR-0003). A user who never
  opened the round at all is not penalized for it — this only applies
  within a round someone actually played.
- **Status note (2026-07-19) — the "revisit" flagged above is drafted:**
  the gap this note has flagged since S-029 ("Tier 0 has no past-round-
  browsing UI at all... there is still nowhere to show one closed round's
  final total distinctly from the leaderboard's all-time running total")
  is now addressed by two new requirements, not by changing this one:
  **REQ-408** gives a closed round its own browsable leaderboard, using
  exactly this REQ's own `SUM(final_points)` definition (unchanged) as the
  per-round total once every cell is locked — REQ-408 is a new way to
  *view* this REQ's existing number, not a new formula. **REQ-407**
  separately gives the *currently active* (not-yet-closed) round a live,
  provisional leaderboard — a genuinely different, recomputed-on-read
  number, not this REQ's locked total, since this REQ only ever applies
  once a round's cells are locked (REQ-205). **REQ-406** additionally
  changes REQ-401/404's shared, all-time leaderboard so it folds in the
  same live provisional contribution while a round is still active,
  instead of only counting `FinalPoints` once locked — see REQ-404's
  matching 2026-07-19 status note. This REQ-206 itself is unchanged and
  not superseded — it still defines the one true locked per-round total;
  REQ-406/407/408 all consume or parallel it, they don't replace it.
- **Status note (2026-07-20 — reviewed, no change made):** a 2026-07-20
  request asked whether a grid a player initiated but didn't finish should
  have its still-unguessed cells lock in at `MaxPointsPerCell` at round
  close, same as an exhausted-wrong cell, and whether that needs a new
  mechanism decision (e.g. materializing synthetic `Guess` rows vs.
  computing the credit separately by diffing grid cells against existing
  `Guess` rows). On review, this REQ's own bullet immediately below
  ("unanswered cells, for a player who participated in the round at all,
  count as `MaxPointsPerCell` points … same as an incorrect guess, per
  ADR-0021") and `ScoreLockingService.MaterializeUnansweredCellsAsync`
  (ADR-0021, S-028) **already implement exactly this** — a participant
  (≥1 guess anywhere in the round) has a real, synthetic `Guess` row
  inserted for every cell they never attempted, which then locks at
  `MaxPointsPerCell` through the same code path as any other incorrect
  guess. A non-participant (zero guesses in the round) is correctly
  excluded, unaffected. No acceptance criterion below needed to change, no
  code changes are implied, and the mechanism question (synthetic rows vs.
  a separate diff-based computation) does not need a new ADR — ADR-0021
  already made and recorded that exact choice; see its own
  alternatives-considered table.
- Given all cells in a round have been locked (REQ-205)
- When the total score is calculated
- Then the sum of `final_points` across all N×N cells for the player is shown
  as the round's total score
- And unanswered cells, for a player who participated in the round at all,
  count as `MaxPointsPerCell` points (the worst score) — same as an
  incorrect guess, per ADR-0021

**Test level:** Unit, API

**REQ-207 – Autocomplete must not leak answer validity**
> As a player, I want to be able to type any plausible player name, so that
> seeing a name suggested (or not) doesn't itself tell me whether it's the
> right answer.

- **Status: Implemented (Tier 1 pulled forward, S-032, 2026-07-17).**
  Builds exactly what ADR-0007 already specifies — a new `PlayerNameIndex`
  table (COMP-10, `IPlayerNameIndexRepository`/`PlayerNameIndexRepository`,
  never merged with `IPlayerStoreRepository`/COMP-06), populated via
  `PlayerNameIndexImporter`'s bulk, birth-year-sliced Wikidata query for
  `P106` (association football player; originally `LIMIT`/`OFFSET`-paged,
  replaced 2026-07-18 after every page timed out server-side in production
  — see `implementation-document.md` §6a) — the `import-player-name-index`
  CLI verb
  (ADR-0024), workflow_dispatch-only, no schedule yet, per ADR-0007's own
  follow-up note. `GET /players/autocomplete?query=&limit=` (bearer-token
  authenticated) queries `PlayerNameIndex` only; a query under 2 characters
  (after trimming) returns an empty array without querying the repository;
  `limit` defaults to 10, clamped server-side to a max of 25 regardless of
  what the caller requests. This story covers the suggestion-list UX only;
  REQ-208's alias/fuzzy-typo-tolerance clauses for guess *scoring* remain
  separately deferred, as does REQ-209's disambiguation UI.
- Given a player is typing a guess
- When autocomplete suggestions are shown
- Then suggestions are drawn from a broad player name index covering many
  thousands of professional footballers, never from only the narrow,
  incrementally-built attribute cache used for correctness-checking
  (see `architecture-document.md` ADR-0007)
- And a name appearing in autocomplete implies nothing about whether it is
  correct for the current cell — correctness is only ever determined after
  submission (REQ-203)

**Test level:** Unit (verify the autocomplete data source is distinct from
the correctness-check data source), Manual (spot-check that early/sparse
grids don't make guessing trivially easy)

**REQ-208 – Name normalization and matching**
> As a player, I want reasonable spelling/formatting variations of a
> player's name to be accepted, so I'm not penalized for not knowing exact
> diacritics or punctuation.

- **Status: Implemented (Tier 0, S-009 + S-065), 2026-07-20.** All
  acceptance criteria below are now built. `PlayerNameNormalizer.Normalize`
  (`XGArcade.Data`) lowercases, strips diacritics, strips punctuation
  (added in S-009 — this closes a real pre-existing gap left over from
  S-006, which stripped diacritics but not punctuation), and collapses
  whitespace; `Player.NormalizedFullName` is kept in lockstep with
  `FullName` via its setter and backfilled for pre-existing rows
  (`PlayerNormalizedFullNameBackfiller`). **As of S-065**,
  `GridGameModule.FindMatchAsync` tries three stages in order, each only
  reached if the previous produced no candidate fitting both of the cell's
  categories: exact `Player.NormalizedFullName` match (unchanged),
  `PlayerAlias.NormalizedAlias` exact match, then a bounded
  edit-distance/fuzzy pass (`NameEditDistance`, plain Levenshtein) scoped
  to players already known to satisfy at least one of the cell's two
  categories — never a full-table scan. The fuzzy tolerance scales with
  the guessed name's normalized length (0 for <=4 characters, 1 for 5-8,
  2 for >=9) rather than one fixed threshold, specifically to avoid
  colliding real short football nicknames (e.g. "Pele"/"Dele" is distance
  1 between two different real players) while still catching a genuine
  typo on a longer name. Stays entirely on the correctness-checking side
  (`PlayerAttribute`/`PlayerAlias`/`Player`, COMP-06) — no new read path
  into `PlayerNameIndex` (COMP-10), per ADR-0007's boundary rule.
- Given a submitted guess
- When it is compared against a candidate player's known name(s)
- Then comparison is done on a normalized form: lowercased, diacritics
  stripped (e.g. "Kaká" and "Kaka" are equivalent), punctuation and extra
  whitespace ignored
- And known aliases/stage names (e.g. a player commonly known by a single
  name different from their full legal name) are matched via a maintained
  alias list, not just the primary name field
- And minor typos are tolerated via a small edit-distance tolerance, applied
  only when no exact or alias match is found, and only when it resolves to
  a small, confident set of candidates (see REQ-209 if more than one remains)

**Test level:** Unit — comprehensive case coverage (diacritics, aliases,
typos, and confirming near-miss strings that should NOT match are rejected)

**REQ-209 – Disambiguating multiple players with a matching name**
> As a player, I want a fair resolution when my guess matches more than one
> real player, so the cell's categories — not luck — decide correctness.

- **Status: Partially implemented (Tier 0's simplified handling only,
  S-009, per `MVP-SCOPE.md`).** The "exactly one candidate satisfies both
  categories → accept automatically" branch is fully built and matches the
  acceptance criteria below exactly. The "no candidate satisfies both
  categories → incorrect" branch is also fully built. **Simplified,
  per `MVP-SCOPE.md`'s explicit Tier 0 scoping:** when more than one
  candidate satisfies both categories, Tier 0 does not show a
  disambiguation prompt at all — `GridGameModule.ScoreSubmissionAsync`
  auto-accepts the lowest-`Id` fitting candidate (the same deterministic
  pick REQ-204 already specifies for uniqueness grouping) and logs a
  warning (`ILogger.LogWarning`) so a real occurrence is visible and can
  trip `MVP-SCOPE.md`'s Tier 1 "build the disambiguation UI" trigger. No
  player-facing disambiguation prompt/picker UI exists.
- Given a normalized/alias/fuzzy-matched guess resolves to more than one
  distinct player record
- When those candidates are checked against the cell's row and column
  categories
- Then if exactly one candidate satisfies both categories, that candidate is
  accepted automatically — the categories themselves disambiguate, no
  extra step needed
- And if more than one candidate satisfies both categories, the player is
  shown a disambiguation prompt listing the distinguishing candidates
  (e.g. birth year, primary nationality/club) and must pick one before the
  guess is scored
- And if no candidate satisfies both categories, the guess is incorrect
  (REQ-203), regardless of how many same-named players exist elsewhere

**Test level:** Unit (all three branches: auto-resolved, disambiguation
required, no valid candidate), UI (disambiguation prompt)

**REQ-210 – Two guesses per cell, locked immediately on a correct answer**
> As a player, I want a clear, tight limit on how many times I can guess a
> cell, so the game stays a genuine test of knowledge rather than something
> solvable by trial and error against immediate feedback.

- **Status: Implemented (Tier 0, S-009).** `GuessSubmissionService`
  (`XGArcade.Core.Scoring`) checks the existing `Guess` row's
  `IsCorrect`/`AttemptCount` before calling the owning `IGameModule` at
  all — "checked before any name resolution work, not after" — and locks
  immediately on a correct answer even if only 1 of 2 attempts was used.
  The disambiguation-doesn't-consume-an-extra-attempt clause is currently
  inapplicable rather than violated: REQ-209's Tier 0 simplification never
  produces a disambiguation prompt to resolve as a separate step, so there
  is nothing for that clause to apply to yet.
- Given a cell where `allow_guess_change` is true for the round (REQ-202)
- When a player submits a guess for that cell
- Then they may submit at most 2 guesses total for that cell in that round
- And if a guess is correct, the cell locks immediately — no further
  guesses are accepted for it, even if only 1 of the 2 attempts was used
- And if both attempts are used without a correct answer, the cell locks
  as incorrect — the player sees this clearly, with `ScoringRules.MaxPointsPerCell`
  points guaranteed (the worst score, per ADR-0021's lowest-wins model)
  regardless of what round-close scoring later computes
- And resolving a disambiguation prompt (REQ-209) is part of the same
  attempt that triggered it, not a separate attempt — a player isn't
  penalized an extra try for a name that happened to be ambiguous
- And this limit applies independently of REQ-704's unrelated confirmation-
  resend cooldown, and independently of REQ-606's login/signup rate limits

**Test level:** Unit (all branches: correct on attempt 1 locks immediately,
correct on attempt 2, both attempts wrong, disambiguation doesn't consume
an extra attempt), API

**REQ-211 – Live verification of known-but-unverified players at guess time**
> As a player, I want a genuinely correct guess to be recognized as
> correct even if that specific player wasn't part of the original grid's
> sample data, so I'm never wrongly told I'm wrong.

- **Status: Partially implemented (Tier 0 simplified, S-011 follow-up,
  ADR-0018; extended to Club × Club by S-030).** `GridGameModule
  .ScoreSubmissionAsync` (`XGArcade.Games.XGGrid`) now falls back to a live
  Wikidata lookup (re-running the cell's own intersection query — country×
  club or, as of S-030, club×club, whichever pairing the cell actually is)
  whenever cached data doesn't already resolve a guess, then re-checks. Any
  other pairing (e.g. a future Trophy cell) is not covered by this fallback
  and falls through to fail-closed, same as before S-030. This closes
  the real gap ADR-0010 predicted and MVP-SCOPE.md's trigger condition
  confirmed in practice. What differs from the full criteria below: the
  trigger is "cached data didn't already answer this guess," not "guess
  matched a `PlayerNameIndex` candidate" — `PlayerNameIndex` (REQ-207) is
  still Tier 1 and not built, so there is no name-index pre-filter yet
  (ADR-0018 explains why Tier 0 doesn't need one for correctness). There is
  also still only one live source (Wikidata) — no API-Football fallback or
  `ExternalApiUsage` budget-gating exists yet, same as REQ-103's status.
  The rest of this requirement's acceptance criteria (the full
  `PlayerNameIndex` gate, the Wikidata/API-Football waterfall, budget
  fail-closed behavior) are recorded below as the full/long-term
  definition, not a claim of current behavior.
- **Status note (2026-07-20, supersedes ADR-0029's fallback-specific
  carve-out — a new ADR superseding ADR-0029 is pending, number TBD):**
  ADR-0029 (2026-07-19) deliberately kept this requirement's guess-time
  fallback lookup persisting `confidence="unverified"`, specifically so an
  admin could still spot-check this narrower, less-vetted path while a
  routine sync (REQ-103/REQ-110) persisted `"verified"` directly. The
  product owner has now decided all Wikidata-sourced data should be
  verified by default, including this path — the call this status note's
  "Partially implemented" bullet above describes
  (`GridGameModule`'s live-lookup fallback, `WikidataLookupOrigin
  .GuessTimeFallback`) now also persists `confidence="verified"`,
  immediately, in the same request, exactly the same as the `Sync` origin
  REQ-103/110 already use. See the superseded acceptance-criterion bullet
  below for the specific line this reverses.
- Given a submitted guess resolves to a specific candidate in
  `PlayerNameIndex` (REQ-207/208 — a real, known player)
- When `PlayerAttribute`/`PlayerOverride` has no record at all — neither
  confirming nor denying — for that player against the cell's category types
- Then the system performs a live lookup for that specific player's
  attributes, using the same Wikidata-first, API-Football-fallback
  waterfall as REQ-103 (ADR-0011)
- **Superseded 2026-07-20 (kept for history, no longer current behavior):**
  "the result is persisted immediately as unverified data, in the same
  request — never deferred to a later batch sync (ADR-0010)."
- And (2026-07-20) the result is persisted immediately as **verified**
  data, in the same request — never deferred to a later batch sync
  (ADR-0010); "immediately, in the same request" is unchanged from the
  superseded bullet above, only the persisted `confidence` value is
- And this live lookup only triggers when the name matched a real
  `PlayerNameIndex` candidate — a guess matching nothing there is
  incorrect without any live call
- And API-Football's daily budget (shared with REQ-103's grid-generation
  fallback calls, tracked via `ExternalApiUsage`) is only at risk of being
  consumed on the rarer path where Wikidata didn't resolve the lookup —
  if that budget is exhausted on that path, the guess is evaluated against
  existing cached data only (fails closed as incorrect, not blocked)

**Test level:** Unit (all branches: no `PlayerNameIndex` match → incorrect,
no live call; match with existing attribute data → no live call needed;
match with no attribute data and budget available → live call + persist;
match with no attribute data and budget exhausted → fails closed), API

**REQ-212 – Click/tap reveals the guessed player name on a locked, correct cell**
> As a player, I want to see which player I answered for a cell I've already
> solved, so I can confirm or recall my own answer without it being
> permanently on display.

- **Status: Implemented (Tier 0, S-041, 2026-07-14).** Replaces the small
  in-cell reveal toggle `CellState.tsx` used before this date (see REQ-204's
  2026-07-14 status note) — the toggle's target was a narrow sub-element
  inside the cell; this requirement makes the whole cell the interactive
  target, and narrows the trigger from tap-or-hover/focus to click/tap only,
  on every device.
- **Built as (`docs/backlog.md` S-041):** `GridCell.tsx` now owns a
  `revealed` boolean (`useState`, defaulting false) and renders a
  locked+correct cell (`isRevealable`) as a real, focusable `<button>`
  whose `onClick` toggles it and whose `aria-expanded` reflects it —
  replacing the old non-interactive `<div role="group">` that pattern used
  before this story, since `CellState.tsx` no longer owns a control of its
  own to avoid nesting inside. `CellState.tsx` takes `revealed` as a plain
  prop and no longer owns any toggle state itself. One real bug found via
  required manual browser verification, not just tests: `.cell-state__name`
  used `overflow: hidden`/`text-overflow: ellipsis`/`white-space: nowrap`,
  which gives a flex item an *automatic* minimum size of 0 once its
  `flex-shrink: 0` siblings (flag, club badge, checkmark) refuse to yield
  space — in a narrow revealed cell, the entire layout deficit landed on
  the name, silently shrinking it to zero width even though it was present
  and correct in the DOM. Fixed by wrapping normally instead
  (`overflow-wrap: anywhere`, matching `.cell-state__meta`'s existing
  pattern) so a long name drops to its own line rather than disappearing
  (`CellState.css`).
- **Status note (2026-07-19, `docs/backlog.md` S-047, direct user feedback
  + a real bug found during that story's own required real-browser
  verification):** on a correct cell that also has a photo (REQ-214), the
  badge dock is **no longer** part of what click/tap reveals — at a
  typical Tier-0 mobile cell width, the row/column badges, name, and
  checkmark did not fit together at all (an ordinary name like "Thierry
  Henry" rendered completely invisible, not just tightly cropped). This
  supersedes, for the photo case only, the "the guessed player's canonical
  name … and its badge dock … are revealed" line in the acceptance
  criteria below: on a photo cell, click/tap now reveals the name alone
  (clamped to a single ellipsis-truncated line — the full name remains in
  the DOM for assistive tech, only its painted box is bounded), with the
  badge dock staying hidden (`display: none`) whether revealed or not. The
  no-photo case is completely unaffected — click/tap still reveals both
  the name and the badge dock exactly as this requirement originally
  specifies, with no clamp on the name. See `design-document.md` §2's
  matching S-047 exception note and SCREEN-01a's S-047 status note for the
  full before/after detail.
- **Status note (2026-07-19, `docs/backlog.md` S-048, direct user feedback
  — "on click name + points only in an overlay"):** on a photo cell
  specifically, this requirement's click/tap toggle now also governs
  whether the points value is shown — before this story, REQ-204's
  points value was always visible at rest regardless of `revealed`, and
  this requirement's toggle only ever affected the name/badge dock. As of
  S-048, a photo cell shows *nothing* overlaid at rest (see REQ-204's
  matching 2026-07-19 status note) — clicking/tapping the cell now reveals
  the name **and** the points value together, still no checkmark icon (the
  checkmark is dropped from the photo overlay entirely, not merely moved
  behind the reveal toggle — see `design-document.md` §2's
  `accent-green-scrim` token note on why that color choice is now
  dormant), and still no badge dock (S-047's exception stands, unchanged).
  The no-photo case is completely unaffected: click/tap there still
  reveals only the name and badge dock exactly as this requirement's
  acceptance criteria state, and the points value there remains
  always-visible at rest as REQ-204 originally specifies, never gated by
  this toggle.
- Given a cell that is locked (REQ-210) and the player's own guess for it
  was correct — i.e. state 1 (correct, round still active) or state 4
  (correct, round closed)
- When the player clicks or taps anywhere on the cell
- Then the guessed player's canonical name (`ResolvedPlayerName`, REQ-303)
  and its badge dock (the row and column category glyphs) are revealed
- And clicking/tapping the cell again while revealed hides the name and
  badge dock again — a single toggle, not a one-way reveal
- And this click/tap is the only interaction that reveals or hides the
  name — there is no separate hover-only or focus-only peek distinct from
  it, and behavior is identical on desktop (mouse), touch, and keyboard
  (activating the cell via keyboard, e.g. Enter/Space when it holds focus,
  produces the same toggle a click/tap would); the cell exposes
  `aria-expanded` reflecting its current revealed/hidden state so a
  keyboard or screen-reader user has the same access as a mouse/touch user
- And a locked cell whose guess was incorrect (state 2/3) is never a click
  target for this interaction — it remains non-interactive, and continues
  to show no player name at all, ever, regardless of click/tap (unchanged
  from REQ-303/S-029)
- And an unlocked or unattempted cell is unaffected — this requirement only
  applies once a cell is both locked and correct

**Test level:** Unit/UI (click/tap reveals then hides on a locked+correct
cell; keyboard activation produces the same toggle; `aria-expanded`
reflects state; a locked+incorrect cell is not a click target and never
reveals a name)

**REQ-213 – Scoring and live-updates explainer**
> As a player, I want a general explanation of how scoring and live updates
> work, so I understand what a point value on a cell means without that
> explanation being repeated on every cell.

- **Status: Implemented (Tier 0, S-041, 2026-07-14).** Replaces the
  per-cell %-breakdown/round-end disclosure text REQ-204 carried before this
  date (see REQ-204's 2026-07-14 status note) — that explanatory content now
  lives in one general place instead of being repeated, cell by cell,
  across the grid.
- **Built as (`docs/backlog.md` S-041):** the header's `(ⓘ)` button
  (`GridScreen.tsx`, next to the round/timer indicator) opens
  `ScoringExplainer.tsx`, a modal (`role="dialog"`, `aria-modal="true"`)
  covering the three required content points verbatim (live estimate can
  change; locked/final value doesn't change after round close; golf-style,
  fewer-others-guessed-scores-better framing, no exact formula). Its open
  state (`explainerOpen`) is tracked independently of `GuessInput`'s
  `activeCell` state in `GridScreen.tsx`, so opening one never discards the
  other. A `code-reviewer` pass on this story's diff found the
  `design-document.md` SCREEN-06 entry, as first written, falsely claimed
  the explainer "returns focus to the entry point on close" as something
  `GuessInput` already did — neither modal actually did that at the time.
  Fixed by implementing real focus management in `ScoringExplainer.tsx`
  (moves focus to its close button on mount via `useEffect`, restores the
  previously-focused element on unmount) and correcting the doc to describe
  `GuessInput`'s actual, unchanged behavior instead of a false comparison —
  see `design-document.md` SCREEN-06's current wording. The same pass also
  gave the explainer's backdrop an explicit `z-index: 20` (above
  `GuessInput`'s `z-index: 10`) rather than relying on DOM order for correct
  stacking when both are open at once.
- **Content expanded (2026-07-14), requested directly by a player:** three
  more required content points added, alongside the original three (see
  acceptance criteria below for all six). Landed in the same iteration as
  a connected SCREEN-01a fix — see REQ-204's matching 2026-07-14 note —
  since a player asked "is wrong = max points, same as not guessing at
  all?" in the same message that reported the per-cell display bug.
- Given the grid screen (SCREEN-01) is displayed with an active round
- When the player activates the explainer entry point in the screen's
  header, next to the round/timer indicator (e.g. "Round #14 ⏱ 1d 4h")
- Then an explainer opens — its exact presentation (modal, expandable
  panel, or similar) is a `design-document.md` decision, not specified
  here — and can be dismissed, returning the player to the grid screen
  without discarding any in-progress state (e.g. a filled-but-not-yet-
  submitted guess)
- And the explainer's content states, at minimum:
  - what the live point estimate shown on a still-active correct cell
    means, and that it can still change before the round closes (REQ-204)
  - what the locked/final point value shown on a cell means once the round
    closes, and that it does not change after that (REQ-205)
  - in general terms, not the exact formula, that an answer fewer other
    players also guessed scores better, and that xG Arcade is scored like
    golf overall — lower is better (ADR-0021)
  - **(2026-07-14 addition)** the number of attempts allowed per cell
    (`MAX_ATTEMPTS_PER_CELL`, REQ-210)
  - **(2026-07-14 addition)** that a wrong guess (attempts exhausted) locks
    a cell at the maximum score, and that this is the *same* maximum score
    an unanswered cell locks at once the round closes — the two are the
    same rule (ADR-0021, S-028's unanswered-cell materialization), not two
    separate ones, and the explainer must connect them rather than only
    stating one
  - **(2026-07-14 addition)** the player-pool restriction: only male
    footballers born in 1939 or later are ever used as answers (REQ-112,
    ADR-0025) — stated plainly so a rejected-but-technically-correct name
    reads as an intentional scope boundary, not a bug
- And the explainer is reachable from the grid screen at any time an active
  round is shown — not gated behind having attempted any particular cell,
  and not a one-time first-visit-only prompt
- And the explainer's content is general to the scoring/live-update
  mechanic — it never includes cell-specific numbers, since it must remain
  valid regardless of which cells, or how many, the player has attempted

**Test level:** UI (explainer opens from the header entry point and closes
without losing in-progress state; contains text covering all six required
content points — presence checks against required concepts, not exact
wording)

**REQ-214 – Photo reveal on a locked, correct cell**
> As a player, I want to see the guessed player's photo, when one is
> available, alongside their name when I reveal a solved cell, so I can
> visually confirm my own answer, not just read it as text.

- **Status: Implemented (Tier 1, pulled forward by deliberate choice,
  2026-07-18 — see `MVP-SCOPE.md`, `docs/backlog.md` S-043/S-044).** The
  trigger for this pull-forward is not an observed pain point — it's a
  direct idea request, recorded plainly rather than invented as something
  else. The backend half (S-043) carries Wikidata's `P18` through
  `WikidataClient`'s existing intersection queries into a new
  `Player.PhotoUrl` column and exposes it, additive, alongside
  `ResolvedPlayerName` in both reveal responses (`POST .../guesses`'
  `SubmitGuessResponse.ResolvedPlayerPhotoUrl` and `GET /rounds/current`'s
  `CurrentRoundGuessResponse.ResolvedPlayerPhotoUrl`). The frontend half
  (S-044) landed in parallel and confirmed the field name matched exactly
  (camelCase JSON: `resolvedPlayerPhotoUrl`) — see S-044 for the full "built
  as" note, including the fixed-size avatar-slot approach that satisfies the
  no-layout-change/no-broken-image-icon constraints below.
- **Scope note:** this is a display-only addition to the correctness side
  of player data (`Player`/`PlayerAttribute`, COMP-06) — specifically,
  carrying Wikidata's `P18` (image) property through the cell-resolution
  query that REQ-101/102 already run and already cache, so a photo is
  available wherever `ResolvedPlayerName` (REQ-303) already is. It does
  not add a new query trigger and does not change REQ-211/ADR-0018's
  guess-time live-lookup behavior in any way. It is explicitly unrelated
  to `PlayerNameIndex`/autocomplete (REQ-207, COMP-10): that data source
  is for autocomplete and name matching only, per ADR-0007's boundary rule,
  and stays out of scope here exactly as it was for the S-032 `PhotoUrl`
  field that was built and then dropped from `PlayerNameIndex` — this
  requirement does not reintroduce that column or revisit that decision.
- **Backfill addendum (S-045, 2026-07-18):** `Player.PhotoUrl` is only ever
  set at the moment a `Player` row is first created
  (`WikidataLookupService.GetOrCreatePlayerAsync`) — a row created by an
  earlier `warm-player-cache` run, before this requirement's `P18` addition
  shipped, has `PhotoUrl` permanently `NULL` with no other code path that
  will ever revisit it, so this requirement's acceptance criteria ("a photo
  shows … whenever one is available") were silently unmet for every
  already-cached player. `PlayerPhotoBackfillService` (`XGArcade.DataSync`),
  run via the `backfill-player-photos` CLI verb, closes that gap: batched,
  idempotent, safe to re-run — see `implementation-document.md`'s CLI-verb
  section for the full shape. Not a new requirement — this is implementation
  detail supporting REQ-214's existing acceptance criteria for players that
  predate it, not a new user-facing behavior.
  Club crests (`ClubCrest`, Tier 2) are also out of scope.
- **Status note (2026-07-18): photo trigger decoupled from the click/tap
  reveal, requested directly by the user after seeing the click-gated
  version live.** Supersedes the click-gated presentation described in the
  acceptance criteria below (the version shipped same-day, PR #79, commit
  `2a8b40d`) — the photo now shows automatically, filling the cell, the
  moment a correct guess locks the cell, with no click/tap required. This
  is strictly a change to the photo's own trigger condition. **REQ-212
  itself is unchanged**: the guessed player's name (and badge dock) is
  still click/tap-gated exactly as REQ-212 defines, on the same cell, and
  that toggle now operates independently of the photo rather than
  revealing it — a photo, when available, is visible whether the name is
  currently shown or hidden. The layout-invariance constraint (cell
  footprint must not change) carries forward unchanged from the prior
  version — it previously guarded the revealed state only and now guards
  the at-rest state, since that's where the photo now appears. The
  no-photo case is unaffected by this note: it already fell back to
  today's checkmark+points-only display and continues to.
- **Status note (2026-07-19, `docs/backlog.md` S-047):** the "reveals the
  canonical name and badge dock (over the photo, when one is present)"
  line below is superseded for the photo case — see REQ-212's matching
  2026-07-19 status note for the full detail. On a photo cell, click/tap
  now reveals the name only (clamped to a single line); the badge dock
  stays hidden. This is a change to what REQ-212's toggle reveals on a
  photo cell, not a change to this requirement's own photo-trigger
  behavior (the photo itself still shows automatically, unaffected).
- Given a cell that is locked (REQ-210) and the player's own guess for it
  was correct — i.e. state 1 (correct, round still active) or state 4
  (correct, round closed)
- And the resolved player has a Wikidata photo available
- When the cell renders, regardless of whether the player has clicked/
  tapped it (REQ-212's reveal state)
- Then the photo displays automatically, filling the cell, at rest — no
  click/tap is required to show it, and clicking/tapping the cell (REQ-212)
  neither shows nor hides the photo, only the name and (as of S-048) the
  points value
- **Status note (2026-07-19, `docs/backlog.md` S-051, direct user choice,
  not a bug fix):** "filling the cell" above never specified whether the
  photo crops to eliminate empty space or scales down to stay fully
  visible with possible empty space on two sides — both are ways of
  "filling the cell" in the sense of occupying its whole footprint (the
  cell's own box, not necessarily every one of its pixels). The behavior
  as shipped through S-050 was crop-to-fill (`object-fit: cover`); asked
  directly which the player preferred after reporting photos looked
  "cut off," the user chose "Show full photo, allow empty space
  (letterbox)" over "Crop photo to fill the cell completely" — the whole
  photo is now always visible, never cropped, at the cost of a plain
  background strip on two opposite sides whenever the photo's aspect
  ratio doesn't match the cell's own. This narrows what "filling the
  cell" means going forward (the cell's footprint, not necessarily every
  pixel within it) without changing the footprint-invariance bullet below
  in substance — the cell's own width/height are still identical whether
  or not a photo is shown, orientation included, confirmed via
  real-browser measurement across both a portrait and a landscape test
  photo at mobile and desktop viewports.
- **Superseded 2026-07-19 (`docs/backlog.md` S-048, kept for history):**
  "the cell's existing checkmark and points value are overlaid on top of
  the photo, in the same position they occupy in the no-photo case … at
  rest." This was true as first shipped and through S-047's coverage
  tightening, but is no longer current: as of S-048 (direct user feedback
  — "at rest, only picture"), a photo cell overlays **nothing** at rest —
  no checkmark, no points, no scrim. The checkmark and points move behind
  REQ-212's click/tap toggle instead, and the checkmark is dropped
  entirely (not merely relocated) — see REQ-204's and REQ-212's own
  matching 2026-07-19 status notes, and `design-document.md` SCREEN-01a's
  S-048 status note, for the full before/after and the recorded trade-off
  (a photo cell no longer has an always-visible-without-clicking score
  signal, only an always-visible "this cell is done" signal via the photo
  itself). The contrast-floor testing requirement below is unaffected in
  substance — it now applies to the name/points shown on reveal rather
  than to an always-visible overlay, using the same already-verified
  `overlay-scrim`/`accent-gold`/`surface-card` pairings.
- And the cell's rendered width and height are identical whether or not a
  photo is shown — this is a testable layout constraint, not a visual
  preference: a photo filling the cell at rest must never change the
  cell's footprint compared to today's no-photo display, and must never
  push or resize neighboring cells in the grid
- **Status note (2026-07-19, `docs/backlog.md` S-050):** "filling the
  cell" above was, for the version shipped through S-049, only ever true
  up to a real, measured, symmetric gap between the photo and the cell's
  actual bordered edge — exactly `.grid-table__cell`'s own CSS `padding`
  value (4px below 960px, 12px at/above it) on every side, confirmed via
  `getBoundingClientRect` on a real Chromium render, not the literal
  bottom-only gap the direct user report described (measuring all four
  edges found it symmetric; most visually obvious, per the report, where
  two photo cells stack vertically). Root cause and fix are CSS-only
  (`frontend/src/grid/Grid.css`'s `.grid-table__cell`/`.grid-cell`) — see
  that story's backlog entry for the full mechanism and before/after
  numbers. The footprint-invariance bullet above is unaffected in
  substance and was specifically re-verified as part of this fix,
  including a scenario this requirement's acceptance criteria didn't
  previously call out explicitly: a photo that loads successfully and
  *then* fails is no longer able to resize the cell either (confirmed via
  a real, deliberately-broken photo URL) — the first fix attempted for
  this gap (tried and rejected during the same story) would have
  regressed exactly that case.
- And REQ-212's click/tap toggle still applies on top of this exactly as
  before — clicking/tapping the cell reveals the canonical name and badge
  dock (over the photo, when one is present), and clicking/tapping again
  hides them again; the photo's own visibility is unaffected either way
- Given the resolved player has no Wikidata photo available
- Then the cell falls back to exactly today's existing at-rest display —
  checkmark and points value only (`SCREEN-01a` state 1/state 4) — no
  broken-image icon, no visible error or loading state, and no difference
  in cell footprint from the case where a photo is shown
- And REQ-212's click/tap reveal of the name and badge dock still applies
  on top of this, exactly as before this note
- And a locked cell whose guess was incorrect is unaffected — no photo is
  ever shown for an incorrect guess, unchanged from the existing rule for
  names

**Test level:** Unit/UI (photo displays automatically at rest when
available, independent of the cell's click/tap-revealed state; checkmark/
points remain present and meet the contrast floor against a photo
background; REQ-212's name/badge-dock toggle still reveals and hides
independently of the photo; no photo available degrades to today's
checkmark+points-only at-rest display with no broken-image icon and no
visible error state; rendered cell width/height are identical across a
photo-shown case, a no-photo case, and a revealed-name-over-photo case —
regression test against the cell's own bounding box, not a visual
snapshot alone, given REQ-212's prior finding that a real layout bug was
missed by tests and only caught by required manual browser verification;
S-051 additionally requires manual verification with both a portrait and
a landscape test photo — jsdom cannot render actual letterboxing, so the
declared `object-fit` value is the extent of what's unit-testable, and the
"whole photo visible, no cropping" outcome itself can only be confirmed by
real-browser rendering)

---

### 4.3 Rounds

**REQ-301 – Configurable round frequency**
> As an admin, I want to configure how often new rounds are created (e.g.
> twice per week), so play frequency can be adjusted without a code change.

- **Status: Partially implemented (Tier 0, S-008; round-duration
  configurability added 2026-07-17, ADR-0027).** The "one round ahead"
  rule itself is fully built: `RoundGenerationService`
  (`XGArcade.Core.Rounds`) skips generation if an upcoming/not-yet-started
  round already exists for the `GameKey`, otherwise resolves the owning
  `IGameModule` (via the new `IGameModuleResolver`), generates its instance,
  and chains the new round's `StartTime` from the previous round's
  `EndTime` — exactly the acceptance criteria below. `generate-round.yml`'s
  cron (now daily, `0 6 * * *`) triggers this via the bearer-token-protected
  `POST /internal/generate-round` (`XGArcade.Api.Rounds.InternalRoundEndpoints`),
  registered in every environment since this is a legitimate scheduled job
  (CONT-05), not a test-data endpoint. "configured...so play frequency can
  be adjusted without a code change" is now also built, within a Tier 0
  scope: `RoundSchedulingOptions.RoundDuration`'s default is read from
  `RoundScheduling:RoundDurationHours` (`appsettings.json` ships `48`; the
  deployed Container App can override it via the
  `RoundScheduling__RoundDurationHours` env var, wired through
  `infra/bicep`, with no code change or redeploy), and
  `POST /internal/generate-round` additionally accepts an optional
  `roundDurationHours` query parameter for a one-off override of a single
  generation call only (validated `>= 24`, never mutates the shared
  `RoundSchedulingOptions` singleton), exposed via `generate-round.yml`'s
  `workflow_dispatch` input. The old requirement that `RoundDuration` and
  the cron cadence be hand-matched against each other is gone: the cron is
  now daily, giving a constant 24h max gap between firings, and
  `RoundGenerationService`'s existing idempotency check makes the daily
  firing a no-op until the current round actually ends — so any
  `RoundDuration >= 24h` (including the 48h default) is safe by
  construction rather than needing hand-verification every time either
  value changes. See ADR-0027 for the full reasoning, including why a
  cron cadence that fires exactly every N days was rejected. What's
  **still not built**, relative to this requirement's full long-term
  acceptance criteria below: an admin-facing configuration surface — "a
  cron expression configured in the system" still means editing
  `appsettings.json`/an env var (a config change, not a code change, but
  still not an in-app admin control) and, for the cron cadence itself,
  editing `generate-round.yml`. That remains Tier 1/2 scope
  (`MVP-SCOPE.md`). `GridSize`'s find-or-create-a-`GridTemplate`-by-size
  shortcut is the same Tier 0 gap already noted on REQ-102, reused via the
  new shared `GridTemplateResolver` helper. The rest of this requirement's
  acceptance criteria are recorded below as the full/long-term definition.
- Given a cron expression configured in the system
- When the scheduler runs
- Then a new Round and its associated GridInstance are created automatically
  according to the schedule, with `start_time` and `end_time` set per configuration
- And generation runs **one round ahead**: the job creates round N+1 while
  round N is still active, so a failed generation (REQ-101's abort path)
  leaves a full round-length window to notice and fix it before players
  see a gap — this matters most in Tier 0, where there is no automated
  failure alerting yet (REQ-902 is Tier 1) and a silent failure would
  otherwise mean a dead app until someone happens to check

**Test level:** Unit (cron parsing), API/Integration (job creates a correct Round)

**REQ-302 – Round lifecycle**
> As a player, I want to always know whether a round is open, closed, or
> upcoming, so I know if I can play.

- **Status: Implemented (Tier 0, S-008/S-009).** The status calculation
  itself is fully built and tested exactly as described below:
  `RoundStatusExtensions.GetStatus` (`XGArcade.Core.Rounds`) derives
  `Upcoming`/`Active`/`Closed` live from a `Round`'s `StartTime`/`EndTime`
  and the current time, with no separate stored status field. "Only
  `active` rounds accept new guesses" is now enforced too, as of S-009:
  `GuessSubmissionService` calls `GetStatus` and rejects with
  `RoundNotActive` (409) for any round that isn't currently `Active`.
- Given a Round's `start_time` and `end_time`
- When a player visits the platform
- Then the Round status (`upcoming` / `active` / `closed`) is calculated
  correctly based on the current time
- And only `active` rounds accept new guesses

**Test level:** Unit, API

**REQ-303 – Fetch the active round and grid for display**
> As a player, I want to open the app, select a game, and see that game's
> current round with my own progress on it, so I can play without already
> knowing a round id.

- **Status: Implemented (Tier 0, S-010; UX updated S-021).** Added as part
  of building the Grid UI (`docs/backlog.md` S-010): no read endpoint
  existed for a client to discover "the round I can currently play" before
  this — `GET /rounds/current` (`XGArcade.Api.Rounds.RoundEndpoints`,
  `[RequireAuthorization]`)
  resolves the caller's local `User` from the bearer token, finds the
  currently `Active` (REQ-302) round for the xG Grid `GameKey` via the new
  `IRoundRepository.GetActiveByGameKeyAsync`, and returns its cells (row/col
  category type and value) joined with the caller's own `Guess` rows for
  that round (`IGuessRepository.GetByRoundAndUserAsync`) — never another
  player's. A cell the player hasn't attempted carries no guess object at
  all, distinguishing "not attempted" from "attempted and pending." The
  guess object includes `SubmittedName` (closing a gap `ui-implementer`
  flagged while building S-010's UI, `docs/design-document.md` §7): without
  it, a cell the player answered before the current browser session had no
  way to redisplay what they guessed. Reading `GridInstance`/`GridCell`
  content is done directly via `IGridInstanceRepository`, bypassing
  `IGameModule` — `architecture-reviewer` confirmed this is a genuine (if
  narrow) exception to ADR-0003's boundary rule 2, not covered by the
  existing `GridTemplateResolver` precedent; recorded explicitly in the new
  ADR-0016 rather than left as an undocumented shortcut.
- **S-029 addition:** `SubmittedName` is unchanged (still the raw as-typed
  text), but the guess object now also carries `ResolvedPlayerName` — the
  canonical, properly-cased `Player.FullName` for a correct guess, resolved
  via a new bulk `IPlayerStoreRepository.GetPlayersByIdsAsync` (also added to
  `POST .../guesses`' own response, via `GuessSubmissionService` calling
  `IPlayerStoreRepository.GetPlayerByIdAsync` directly, so a name is
  available immediately on submission, not only on the next `GET
  /rounds/current`). `ResolvedPlayerName` is always null for an incorrect
  guess — a player-feedback pass found the raw as-typed guess unhelpful to
  display for a wrong answer (and inconsistent casing distracting for a
  right one), so the frontend now shows the canonical name for a correct
  guess and no name at all for an incorrect one (`CellState.tsx`), only the
  ✕ icon and attempt count. Separately, the frontend's header nav no longer
  has separate "Games"/"Grid" links — the "xG Arcade" title itself now
  routes back to the game-selection landing screen (S-021), which was the
  only other place a player could reach the grid from anyway; this reduced
  the header to "Leaderboard" + "Log out" so it stops wrapping onto a second
  line on a narrow phone. No endpoint change, client-side routing only, same
  as S-021's own note above — see the new acceptance criterion below.
- Given a logged-in player
- When they request the current round
- Then the system returns the currently active round for the game (if any),
  including its grid cells and, for each cell, the player's own guess state
  if they've attempted it (correct/incorrect, attempts used, whether the
  cell is locked, and the name they submitted — so the UI can still show
  what was guessed after a page reload, not only immediately after submission)
- And if no round is currently active, a clear "no active round" response is
  returned rather than a generic error
- And this endpoint never reveals another player's guesses — only the
  requesting player's own
- And an upcoming (not-yet-started) round scheduled one round ahead
  (REQ-301) is never returned as if it were playable now
- **(S-021)** And, in the frontend, the player only reaches the screen that
  calls this endpoint after selecting a game from a game-selection landing
  screen shown immediately after login/signup — a client-side routing
  change only (no "list games" endpoint exists or is needed while Tier 0
  has exactly one game, `GameKey="xg-grid"`); this endpoint's own contract
  is unchanged
- **(S-029)** And, for a correct guess, the response also includes the
  canonical, properly-cased player name (not just the raw text the player
  originally typed) — the frontend shows this instead of the as-typed guess;
  for an incorrect guess, no name is shown at all in the UI, only that it
  was wrong and how many attempts remain
- **(S-029)** And, in the frontend, the header nav no longer exposes
  separate "Games"/"Grid" links duplicating this screen's entry point — the
  "xG Arcade" title itself is the (client-side) route back to the
  game-selection landing screen (S-021), leaving only "Leaderboard" and
  "Log out" in the header at every viewport width; this endpoint's own
  contract is unchanged

**Test level:** API, E2E (`tests/e2e/play-grid.spec.ts`'s REQ-303-tagged
case covers the game-selection step added in S-021)

---

### 4.4 Leagues

**REQ-401 – Global League (default)**
> As a player, I want to automatically be part of a global leaderboard, so I
> can compare myself to all users without extra steps.

- **Status: Implemented (Tier 0, S-011).** `AuthController.Signup`
  (`XGArcade.Api.Auth`) calls `ILeagueRepository.GetOrCreateGlobalLeagueAsync`
  (idempotent get-or-create, guarded by a filtered unique index on
  `League.Type = 'global'` plus a race-recovery catch for two concurrent
  first-ever signups) followed by `AddMembershipAsync`, right after the
  local `User` row is created — this is COMP-02 (Core.Leagues)'s first real
  code. Two backfillers (`UserDisplayNameBackfiller`,
  `LeagueMembershipBackfiller`, both run from `dotnet run --
  migrate-and-seed`) cover rows that predate this feature.
- **Status note (2026-07-20):** automatic membership (below) is unchanged
  and is not the same guarantee as automatic *ranked visibility* — as of
  this date, REQ-404's ranked leaderboard excludes a member who has never
  submitted a single guess (see REQ-404's own new acceptance criterion).
  This REQ still governs membership only; it does not claim every member
  is shown in the ranked list.
- Given a new user registers
- Then the user is automatically added to `League(type="global")`
- And this requires no action from the user

**Test level:** Unit, API

**REQ-402 – Create a custom league**
*(Status: Implemented (S-063), 2026-07-20 — pulled forward ahead of
`MVP-SCOPE.md`'s original Tier 1 placement; see that file's own updated
note.)* `POST /leagues` (`LeagueEndpoints`, `Api.Leagues`) →
`LeagueService.CreateCustomLeagueAsync` (`Core.Leagues`) creates a
`League(Type="custom")` with a unique 6-character `InviteCode` (887M-symbol
alphabet, visually-ambiguous characters excluded) and enrolls the creator
as its first member in the same call. Uniqueness: an in-app pre-check plus
a DB-level unique index (`IX_Leagues_InviteCode`) as the real race-safety
net, same pattern as `User.NormalizedDisplayName`'s uniqueness handling.
**Not built, tracked separately:** REQ-404's full per-custom-league
leaderboard (this story only lists a member's own custom leagues by
name/code, no leaderboard rendering) and the per-user league caps
mentioned in this document (25 created / 100 joined) — neither was
requested for this story.
> As a player, I want to create my own league and invite friends, so we can
> compete in a smaller group.

- Given a logged-in player
- When the player creates a league with a name
- Then a `League(type="custom")` is created with a unique `invite_code`
- And the creator is automatically added as a member

**Test level:** Unit, API

**REQ-403 – Join a league via code**
*(Status: Implemented (S-063), 2026-07-20.)* `POST /leagues/join`
(`LeagueEndpoints`) → `LeagueService.JoinByInviteCodeAsync` — the code is
trimmed and upper-cased before lookup (codes are only ever generated
uppercase, so a lowercase-typed code still resolves). An unrecognized code
is a 404 with a specific detail message and creates no membership.
Re-joining a league the caller already belongs to is treated as an
idempotent success, not an error — this REQ doesn't specify that case, and
a documented product-shape choice was made rather than leaving it
undefined.
> As a player, I want to join a friend league via a code, so I can compete
> with specific people.

- Given a valid `invite_code`
- When a player enters the code
- Then the player is added as a `LeagueMembership`
- And an invalid code returns a clear error without creating a membership

**Test level:** Unit, API

**REQ-404 – Leaderboard per league**
> As a player, I want to see the leaderboard for any league I'm a member of,
> so I can track my ranking.

- **Status: Partially implemented (Tier 0, S-011; sort direction corrected
  S-028/ADR-0021; paginated S-034).** `GET
  /leagues/global/leaderboard` (`XGArcade.Api.Leagues.LeaderboardEndpoints`)
  → `ILeaderboardService`/`LeaderboardService` (`XGArcade.Core.Leagues`)
  implements exactly this ranking (members' `SUM(FinalPoints ?? 0)`,
  **sorted ascending** — ADR-0021: xG Arcade is scored like golf, lowest
  total wins, so rank #1 is the lowest total, not the highest — ties broken
  by display name) for the global league only — custom leagues (REQ-402/403)
  don't exist yet, so there is exactly one leaderboard to read today;
  SCREEN-03's frontend (`LeaderboardScreen.tsx`) shows only the Global list,
  with a "Load more" control and a pinned "you" footer for when the
  requesting user's row is off the currently-loaded page(s), no tab
  switcher.
  **Pagination (S-034):** the response is now bounded via `cursor`/
  `pageSize` — see REQ-607's own status note for the shape. This closes
  the gap previously noted here.
- **Status note (2026-07-19, drafted — REQ-406):** the `SUM(FinalPoints ??
  0)` formula described above is, per REQ-206's own status note,
  deliberately locked-only today — a round still in progress contributes
  nothing to this total until it closes. **REQ-406** now specifies the
  revisit: this leaderboard's total additionally includes a live,
  recomputed-on-every-read contribution from the currently active round
  (correctly-guessed cells' current `LivePoints`, REQ-204, plus
  locked-incorrect cells' `MaxPointsPerCell`), on top of the unchanged
  `SUM(FinalPoints ?? 0)` over closed rounds. See REQ-406 for the full
  acceptance criteria — this note only cross-references it so the
  contradiction between "only sums `Guess.FinalPoints`" above and the new
  behavior isn't silently left standing.
- **Status note (2026-07-20 — new acceptance criterion, Status: Implemented,
  Tier 0, S-056):** `LeaderboardService.GetGlobalLeaderboardAsync`
  previously included every league member regardless of guess history,
  defaulting an absent total to `0` — under ADR-0021's lowest-wins model,
  `0` is the *best* possible score, so a member who had never submitted a
  single guess ranked #1 ahead of everyone who had actually played. The
  product owner confirmed this was wrong: such a member should not be
  ranked at all, not ranked first. Built exactly as specified below — a new
  `IGuessRepository.GetUserIdsWithAnyGuessAsync` (`GuessRepository`) is
  queried alongside the existing locked-only
  `GetTotalFinalPointsByUserIdsAsync`, kept as a separate call specifically
  so a member active only in the currently active (unlocked) round is not
  mistaken for never-played. See the bullet below.
- **Status note (2026-07-20, superseded by REQ-409):** the
  `SUM(FinalPoints ?? 0)` ranking formula described below no longer
  reflects production behavior — `GetGlobalLeaderboardAsync` now ranks by
  REQ-409's median-per-round score (>= 5 qualifying rounds), not the raw
  sum. This REQ's own text is kept, not rewritten in place, per this
  document's ID-stability rule; see REQ-409 for the current, actual
  behavior and full acceptance criteria.
- Given a player is a member of at least one league
- When the player opens a league's leaderboard
- Then the ranking is based on the same underlying score data (no separate
  score calculation per league), filtered by league membership
- And a member for whom no `Guess` row has ever existed — in any round,
  locked or still active, correct or incorrect — is excluded entirely from
  the ranked list, not shown ranked with a default total of `0`; this
  applies to the all-time ranking specifically (REQ-401/404's own scope) —
  REQ-406/407's active-round contribution and REQ-408's per-round totals
  already have their own, narrower "zero guesses in this round"/"zero
  guesses in this specific round" exclusions that are unaffected by this
  bullet
- And the list is correctly sorted ascending by total score — lowest wins
  (ADR-0021)

**Test level:** Unit, API, UI (a league member with zero guesses ever does
not appear in the ranked list at all; a member with at least one guess,
locked or still-live, appears ranked normally even if their computed total
happens to be 0)

**REQ-405 – Leaderboard time-window resolutions** *(Status: Implemented
(Tier 0, S-027), 2026-07-20.)*
- **Status note (S-027):** built as drafted below, plus the resolved design
  questions. New `GET /leagues/global/leaderboard/window/{resolution}`
  route (`XGArcade.Api.Leagues.LeaderboardEndpoints`), `{resolution}` parsed
  case-insensitively into a new `LeaderboardWindowResolution` enum
  (`Round`/`Week`/`Month`/`Year`) — anything else is a 400 ("Invalid
  resolution"). Backed by a new
  `LeaderboardService.GetWindowedLeaderboardAsync`: `Round` reuses the exact
  REQ-408 single-round path (`IRoundRepository.GetClosedByGameKeyAsync(gameKey,
  0, 1)` + the existing `IGuessRepository.GetTotalFinalPointsByRoundIdAsync`),
  always resolved to the single most-recently-closed round, never a
  caller-chosen one. `Week`/`Month`/`Year` compute a calendar-aligned,
  half-open `[start, end)` UTC window (ISO week Monday-to-Monday, calendar
  month from the 1st, calendar year from Jan 1st), fetch that window's closed
  round ids via a new `IRoundRepository.GetClosedIdsWithinWindowAsync`, and
  sum `FinalPoints` via a new `IGuessRepository.GetTotalFinalPointsByRoundIdsAsync`
  (the existing single-round method now delegates to this plural one with a
  one-element collection, rather than keeping two independent query
  implementations). Every scope is locked-only by construction — an active
  round (`ClosedAt == null`) is never even a candidate row, so its guesses
  can never contribute to any window, matching REQ-401/404's existing rule.
  A member with zero guesses in the selected window is simply absent from
  the ranked list (same "must have at least one row here to be ranked at
  all" pattern as every other scope in this file), not shown with a
  default-0 total. **Indexing plan (per this REQ's own acceptance
  criterion):** no new migration was added. The existing
  `Round(GameKey, EndTime)` composite index (added for REQ-408) already
  covers the `(gameKey, EndTime range)` filter `GetClosedIdsWithinWindowAsync`
  needs, and `Guess`'s existing unique index on `(RoundId, UserId, CellId)`
  already has `RoundId` as its leading column, so a `RoundId IN (...)` filter
  is already index-covered too — both are documented inline as code comments
  on the new repository methods rather than re-derived. Frontend (SCREEN-03,
  same session, follow-up commit): a 4th "Time Windows" scope on
  `LeaderboardScreen.tsx` with round/week/month/year sub-tabs, same
  fetch-on-transition pattern as the `live`/`past` scopes, rows always
  non-provisional (locked totals only). `design-document.md` SCREEN-03
  updated accordingly.
> As a player, I want to see the leaderboard filtered to the current round,
> week, month, or year — not only the all-time total — so I can compare
> recent performance, not just who has played longest.

- Given a player opens the leaderboard
- When the player selects a resolution (round / week / month / year — all-time
  remains the REQ-401/404 default)
- Then the ranking sums `FinalPoints` (same locked-only rule as REQ-401/404 —
  this REQ does not change what counts, only the time window) for guesses
  whose `Round.EndTime` falls within the selected window, sorted ascending
  (ADR-0021: lowest wins, same direction as REQ-401/404's all-time total)
- And "round" specifically means the single most recently *closed* round for
  the game (Tier 0 has no past-round browsing UI at all yet — REQ-206's
  status note already flags this gap; this REQ does not resolve it, it only
  needs the *most recent* closed round, not an arbitrary one)
- And week/month/year windows are **calendar-aligned** (ISO week, calendar
  month starting the 1st, calendar year), not rolling (last 7/30/365 days)
- And a window boundary is always evaluated in **UTC**, matching every other
  timestamp in this system
- And a round whose `EndTime` is null (still active, unlocked) never
  contributes to any window — the same locked-only rule REQ-401/404's
  all-time total already follows, now stated explicitly here rather than
  left to be inferred from their silence

**Design questions this REQ previously left open — resolved 2026-07-12:**
- Calendar-aligned vs. rolling windows → **calendar-aligned**, decided above
- Timezone for boundary evaluation → **UTC**, decided above
- Whether an unlocked round ever contributes → **no**, decided above
- Performance: REQ-607's pagination is now implemented (S-034), but this
  REQ still adds four more query shapes (round/week/month/year windows) on
  top of the existing all-time one — **not resolved as a product
  decision, still an implementation-time requirement**: S-027's acceptance
  criteria requires a REQ-607-aligned indexing plan as part of implementing
  this REQ, not just "add a `WHERE` clause"

**Test level:** Unit, API, UI

**REQ-406 – Leaderboard totals include live points from the active round**
*(Status: Implemented (Tier 0, S-053), 2026-07-19 — this is the revisit
REQ-206's status note flagged since S-029.)*
> As a player, I want the leaderboard to reflect what I've done in the
> round that's happening right now, not only my finished rounds, so I can
> see where I actually stand instead of a total that ignores whatever I'm
> currently playing.

- **Status note (S-053):** built exactly as drafted below, plus one shared
  computation reused by REQ-407. `GET /leagues/global/leaderboard`
  (`XGArcade.Api.Leagues.LeaderboardEndpoints`, unchanged route) resolves
  the currently active round (`IRoundRepository.GetActiveByGameKeyAsync`,
  same REQ-303 pattern `RoundEndpoints` already uses) and passes it into
  `LeaderboardService.GetGlobalLeaderboardAsync`, which now takes a
  nullable `Round? activeRound` parameter. The three-case per-cell formula
  (correct → `LivePoints`; locked-incorrect → `MaxPointsPerCell`;
  unattempted → nothing) lives in one place, a new
  `ILiveRoundContributionService`/`LiveRoundContributionService`
  (`XGArcade.Core.Scoring`), reused verbatim by REQ-407 below — never two
  independently-written formulas. Cells are resolved only through
  `IGameModuleResolver`/`IGameModule.GetCellIdsAsync`, never a direct
  `GridInstance`/`GridCell` reach-in (ADR-0003), confirmed by
  `architecture-reviewer`'s quality-gate pass. No caching anywhere in this
  path (ADR-0031) — verified in the same review. A member with zero
  guesses in the active round is unaffected, exactly as specified.
- **Relationship to existing behavior:** today, `LeaderboardService` sums
  only `Guess.FinalPoints` (REQ-401/404), which is `null` until a round is
  locked at close (REQ-205/ADR-0022) — REQ-206's status note already
  documents this as deliberate, not a bug, "revisit once/if a past-round-
  detail view exists." This REQ is that revisit for the *shared, all-time*
  leaderboard specifically (REQ-401/404); REQ-407 is the companion REQ for
  a leaderboard scoped to *only* the active round.
- Given a player is a member of a league (global, REQ-401, or custom,
  REQ-402) and a game the league tracks has a currently active round
  (REQ-302)
- When that league's leaderboard (REQ-404) is requested
- Then each member's total is the existing `SUM(FinalPoints ?? 0)` over
  every closed round (unchanged), **plus** a live contribution from the
  currently active round only, computed per cell exactly as REQ-407
  defines it: a correctly-guessed cell contributes its current
  `LivePoints` (REQ-204); a locked-incorrect cell (both attempts used,
  REQ-210) contributes `ScoringRules.MaxPointsPerCell`; a cell that
  member has not yet attempted in the active round contributes nothing to
  the total — not `0`, since `0` already means "best possible score"
  under ADR-0021's golf model, and not `MaxPointsPerCell` either, since
  that penalty is only ever applied at round close (REQ-206/ADR-0021's
  `MaterializeUnansweredCellsAsync`, which does not run against an active
  round)
- And this combined total is recomputed on every request — no stored or
  cached snapshot of the live component, the same "always live, never
  persisted until close" rule REQ-204's `LivePoints` and REQ-206's client-
  side running total already follow
- And the sort order is unchanged: ascending, lowest combined total first
  (ADR-0021), same tie-break as REQ-404 (display name)
- And a league member with zero guesses in the currently active round is
  unaffected by this REQ — their total is exactly what REQ-401/404 already
  compute today (locked rounds only)
- **Status note (2026-07-20 — narrows, does not supersede, the bullet
  above it and the "unattempted cell contributes nothing" clause earlier
  in this REQ; Status: Implemented, Tier 0, S-056):** the product owner
  has confirmed a live estimate that never credits an untouched cell reads
  as unfairly low the moment a player has genuinely started a grid — a
  freshly-initiated grid's live total should start near the theoretical
  max and count down as guesses resolve, not sit near zero until every
  cell is attempted. This changes the "not yet attempted" case
  specifically for a player who has made **at least one** guess anywhere
  in that round's grid (a "participant," the same definition
  `ScoreLockingService.MaterializeUnansweredCellsAsync`/ADR-0021 already
  uses) — it does **not** change the bullet immediately above this note: a
  member with **zero** guesses anywhere in the active round is still
  entirely unaffected by this REQ, exactly as that bullet already states
  verbatim; that bullet is correct and stays as-is. Built exactly as
  described: `ILiveRoundContributionService`/`LiveRoundContributionService`
  (`XGArcade.Core.Scoring`) now tracks each participant's per-cell
  attempted set and adds `MaxPointsPerCell` for every round cell outside
  it, leaving a cell with one of two attempts used untouched.
- Given a player is a member of a league and a game the league tracks has
  a currently active round, and that player has made at least one guess —
  any attempt count, correct or incorrect — somewhere in that round's grid
  (i.e. they are a participant in the round, per the definition above)
- When that league's leaderboard is requested
- Then, in addition to the correctly-guessed and locked-incorrect
  contributions already defined above, every cell in that round's grid the
  player has made **zero** guesses on at all contributes
  `ScoringRules.MaxPointsPerCell` to their live total — the reversal is
  specifically for a cell with no guess row at all; a cell where the
  player has used one of their two attempts and still has one remaining
  (REQ-210) is a separate, genuinely unresolved state and is unaffected by
  this clause — it continues to contribute nothing, exactly as the
  original "not yet attempted" bullet already specifies
- And this only applies once the player is a participant in that specific
  round — a player with zero guesses anywhere in the active round
  contributes nothing at all from it, unchanged (see the preserved bullet
  above)
- And this REQ does not change REQ-405's round/week/month/year
  time-window leaderboard, which remains explicitly locked-only by its own
  resolved design question ("a round whose `EndTime` is null … never
  contributes to any window") — REQ-405 itself is not modified by this REQ

**Test level:** Unit (combined total sums a closed round's locked points
plus the active round's live contribution correctly; a never-attempted
cell in the active round contributes nothing for a non-participant, not
`0` and not `MaxPointsPerCell`; for a participant, a cell with zero
guesses contributes `MaxPointsPerCell` (2026-07-20) while a cell with one
of two attempts used and still unresolved continues to contribute nothing;
recomputing after a cell's `LivePoints` changes — e.g. another player
submits a matching guess — produces a different total on the next read
without any explicit invalidation step), API (global/per-league
leaderboard endpoint reflects the live contribution and updates across two
successive requests as underlying guesses change)

**REQ-407 – Leaderboard scoped to the currently active round (live)**
*(Status: Implemented (Tier 0, S-053), 2026-07-19.)*
> As a player, I want a leaderboard scoped to just the round being played
> right now, updating live as guesses come in, so I can see how I compare
> to others on this specific round, not only my all-time or last-closed
> total.

- **Status note (S-053):** new `GET /leagues/global/leaderboard/active-round`
  route (`cursor`/`pageSize`, same shape as every other leaderboard route
  here), participant-only, backed by `LeaderboardService
  .GetActiveRoundLeaderboardAsync` calling the same
  `ILiveRoundContributionService` REQ-406 uses. Returns a 404 ("No active
  round") exactly mirroring `RoundEndpoints`' REQ-303 "no active round"
  response when none is active, per this REQ's own acceptance criterion.
  Frontend: `LeaderboardScreen.tsx` (SCREEN-03) gained a three-way scope
  selector — "All-time" / "Current Round" / "Previous Rounds" (REQ-408;
  renamed from "This round (live)"/"Past rounds" on 2026-07-20, S-056 —
  purely cosmetic, no REQ specifies exact tab wording) —
  as an additional selector alongside (not replacing) the not-yet-built
  custom-league tabs, exactly the placement this REQ specifies. The live
  scope renders every row with the same "~N pts estimated" wording
  `GridScreen.tsx`/`CellState.tsx` already established for a single cell's
  live point value (REQ-204/S-018), satisfying "presented as visibly
  provisional" without a new token/color/icon (no `design-document.md` §2
  change needed). **One clarification on "recomputed fresh on each
  request," found and corrected during this story's own quality gate:**
  the frontend does not poll this route on an interval the way the
  all-time scope's 15s poll does — `ADR-0031` explicitly flags this read as
  materially more expensive than the all-time one, so the frontend instead
  fetches once per genuine *entry* into the "Current Round" tab
  (switching to it fresh, including re-entering after visiting a different
  scope) rather than continuously in the background. Each such fetch still
  recomputes fully fresh server-side, satisfying this REQ's actual
  acceptance criterion ("every rank and total returned is computed fresh on
  each request") — the criterion governs what a request returns, not how
  often the frontend chooses to issue one. An earlier draft of this
  behavior had a real bug (a `useRef` "fetch once ever" latch that never
  reset, so re-entering the tab after leaving it showed indefinitely stale
  data with no refresh) — caught by `quality-architect`'s pre-merge review
  and fixed before merge; regression tests now cover the leave-and-return
  case explicitly.
- **Relationship to REQ-405:** REQ-405's "round" resolution is, by its own
  explicit, already-resolved design decision, the single most recently
  *closed* round only — no live component, no browsing of arbitrary past
  rounds. This REQ is a different concept: a live, in-progress round's own
  leaderboard. REQ-405 is not modified, weakened, or merged by this REQ.
- **Relationship to REQ-406:** REQ-406 folds this same live, per-round
  contribution into the shared all-time leaderboard's total. This REQ is
  the same contribution exposed as its own standalone, round-scoped view —
  the two share one underlying computation, not two independently-written
  formulas.
- **Status note (2026-07-20 — carries REQ-406's matching change over,
  since both consume the same `ILiveRoundContributionService` computation;
  Status: Implemented, Tier 0, S-056):** REQ-406 was revised the same
  day to credit `ScoringRules.MaxPointsPerCell` for a cell with **zero**
  guesses, specifically for a player who has made at least one guess
  somewhere in that round's grid. Every player who appears on *this*
  leaderboard is, by this REQ's own definition, already such a participant
  (zero-guess players never appear here at all — see the bullet below) —
  so this change applies to every row shown here, not a narrow subset of
  them. See the superseded parenthetical in the formula below.
- **UX placement (resolved, not left open):** this leaderboard is reached
  from the same leaderboard screen (SCREEN-03) REQ-401/404/405 already
  use, as an additional selectable scope alongside REQ-405's existing
  round/week/month/year resolution options — e.g. "Current Round" —
  not a separate top-level screen. This keeps a single leaderboard
  surface with one consistent list/pagination/"you"-row pattern
  (REQ-607) rather than duplicating that UI for a second, parallel screen.
- Given a game has a currently active round (REQ-302)
- When a player requests that round's leaderboard
- Then every round *participant* — a player with at least one `Guess` row
  in this specific round, the same participant definition ADR-0021's
  `MaterializeUnansweredCellsAsync` already uses — has a provisional total
  computed as: for each of the round's cells, a correctly-guessed cell
  contributes its current `LivePoints` (REQ-204, itself recomputed live
  and free to change as more players answer, per ADR-0020); a
  locked-incorrect cell (both attempts used, REQ-210) contributes
  `ScoringRules.MaxPointsPerCell`;
  **superseded 2026-07-20 (kept for history, no longer current behavior):**
  "a cell that participant has not yet attempted contributes nothing to
  their total (explicitly not `0` — see REQ-406's identical resolution of
  this — and not `MaxPointsPerCell`, since that penalty only ever applies
  at round close)." As of 2026-07-20, a cell the participant has made
  **zero** guesses on contributes `ScoringRules.MaxPointsPerCell`, same as
  a locked-incorrect cell — see REQ-406's matching 2026-07-20 status note
  for the full rationale (a freshly-initiated grid's live estimate should
  start near the theoretical max, not near zero). A cell where the
  participant has used one of their two attempts and still has one
  remaining (REQ-210) is unaffected by this change — it remains genuinely
  unresolved and contributes nothing, exactly as originally specified
- And a player who is not a participant in this round (zero guesses) does
  not appear on this leaderboard at all
- And ranking sorts ascending — lowest provisional total first (ADR-0021)
  — with the same tie-break REQ-404 already uses (display name)
- And every rank and total returned is computed fresh on each request —
  there is no snapshot, cache, or "freeze" of a rank: if a participant's
  guess, or another participant's guess on a shared cell, changes the
  underlying data between two requests (e.g. a second attempt flips a
  cell from incorrect to correct, or another player's new guess changes a
  cell's uniqueness and therefore its `LivePoints`), the next request
  reflects the new value immediately — a rank shown at one moment
  legitimately differing from the next request's rank is expected
  behavior, not a bug, the same way REQ-204's live point estimate is
  already understood to be able to change before a cell locks
- And this leaderboard is presented as visibly provisional (mirroring
  REQ-204/213's existing "estimated"/"can still change before the round
  closes" framing) — a player must not be able to mistake a live rank
  shown here for a locked, final one
- And requesting this leaderboard when no round is currently active
  returns a clear "no active round" response, mirroring REQ-303's existing
  pattern for the same situation — not a generic error
- And once this round closes (REQ-205), it is no longer reachable via this
  REQ — its final leaderboard is reached only via REQ-408 from that point on

**Test level:** Unit (provisional-total computation per the three cell
cases above, updated 2026-07-20 so a zero-guess cell contributes
`MaxPointsPerCell` rather than nothing, while a one-of-two-attempts-used
unresolved cell still contributes nothing; ranking and tie-break match
REQ-404's rules; recompute-on-read
produces a different rank after an underlying guess changes, with no
caching layer to invalidate), API (endpoint returns a clear "no active
round" response when none exists; two successive requests reflect an
intervening guess change), UI (leaderboard is visibly marked provisional;
reachable from SCREEN-03 as an additional scope option)

**REQ-408 – Browsing a past (closed) round's leaderboard**
*(Status: Implemented (Tier 0, S-054), 2026-07-19.)*
> As a player, I want to open any individual past round and see its final
> leaderboard, not only the current all-time total or the most recent
> round, so I can look back at how a specific round played out.

- **Status note (S-054):** required adding a new `Round.ClosedAt` (nullable
  `DateTime`) column, via a real EF Core migration (`AddRoundClosedAt`) —
  this executes the exact follow-up ADR-0022's own "Follow-up" section
  anticipated ("if a past-round-detail screen is ever built... revisit
  adding an explicit `Round.ClosedAt` column then"); no new ADR was needed,
  ADR-0022 already reasoned through it. `RoundCloseService.CloseRoundAsync`
  sets it once, first-close-wins, same idempotent shape as its existing
  `EndTime` pull-forward. **Correctness detail found and fixed during this
  story's own quality gate:** `ClosedAt` must only ever be persisted
  *after* `LockRoundScoresAsync` completes, never before or concurrently —
  an earlier version of this change set it first, which could let a reader
  see a round as "closed"/browsable via this REQ while some guesses still
  had `FinalPoints == null`, understating totals as if final. Reordered so
  a throw during locking leaves `ClosedAt` null and a later retry
  resumes/redoes locking before ever closing. New routes: `GET
  /leagues/global/leaderboard/closed-rounds` (paginated round list,
  `cursor`/`pageSize` matching REQ-607's exact shape/defaults, most
  recently closed first) and `GET
  /leagues/global/leaderboard/closed-rounds/{roundId}` (that round's
  locked, never-recomputed leaderboard — `IGuessRepository
  .GetTotalFinalPointsByRoundIdAsync`, REQ-206's own formula filtered to
  one round). Not-found (404) and not-closed-yet (409) are distinct
  responses, exactly as specified. Frontend: SCREEN-03's "Previous Rounds"
  scope shows the round list (labelled by close time, no fabricated round
  numbering since none exists in the data), drilling into one round's
  leaderboard rendered with plain, non-provisional point text (contrast
  REQ-407's "~N pts estimated").
- **Relationship to REQ-405:** REQ-405's "round" resolution only ever
  exposes the single most-recently-closed round, folded into the same
  shape as its week/month/year windows. This REQ is a different concept:
  every closed round, individually selectable and browsable by id, as its
  own standalone view — not limited to the most recent one. REQ-405 is
  not modified by this REQ.
- **Relationship to REQ-206:** a closed round's total here is exactly
  REQ-206's own `SUM(final_points)` definition, per participant, applied
  per round — this REQ is a new way to *view* that number (individually,
  by round, browsable), not a new scoring formula.
- **UX placement (resolved, not left open):** reached from the same
  leaderboard screen (SCREEN-03) as REQ-401/404/405/407, via a "past
  rounds" scope that first shows the round-selection list below, then that
  round's leaderboard — not a separate top-level screen, consistent with
  REQ-407's placement decision.
- Given a game with one or more closed rounds (REQ-302)
- When a player requests the list of browsable past rounds for that game
- Then the system returns only closed rounds (never the active or
  upcoming one — the active round is reachable only via REQ-407), most
  recently closed first
- And this list is paginated the same way REQ-607 already paginates
  leaderboard membership — `cursor`/`pageSize` query parameters on the
  round list itself, with the same default/maximum `pageSize` REQ-607
  already established (default 50, max 100), so the platform has one
  consistent pagination shape rather than a second, differently-shaped one
  for round browsing specifically
- Given a specific closed round's id
- When a player requests that round's leaderboard
- Then each participant's total is `SUM(final_points)` for that round only
  (REQ-206's own definition, unchanged) — a permanently locked value,
  never recomputed live, ranked ascending (ADR-0021) with REQ-404's
  existing tie-break
- And requesting a round id that does not exist returns a clear "not
  found" response
- And requesting a round id that exists but has not yet closed (still
  `active` or `upcoming`, REQ-302) returns a clear, distinct "not closed
  yet" response — a not-yet-closed round is never silently served through
  this endpoint as if it were a completed one; it is only ever reachable
  through REQ-407 while active

**Test level:** Unit (round-list query returns only closed rounds, most
recent first; a specific round's total matches REQ-206's own locked
formula exactly), API (round-list pagination matches REQ-607's cursor/
pageSize shape; not-found vs. not-closed-yet are distinct, correctly-coded
responses), UI (round-selection list, then that round's leaderboard, on
SCREEN-03)

**REQ-409 – Median, participation-gated score for the all-time leaderboard**
*(Status: Implemented (Tier 0, S-060), 2026-07-20 — decided and built the
same day. `LeaderboardService.GetGlobalLeaderboardAsync` ranks by the
median of each player's per-round `SUM(FinalPoints)` totals via a new
`IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync` (joins `Guesses`
to `Rounds`, filters `ClosedAt != null`), filtered to members with >= 5
qualifying rounds; ties broken by display name as decided. The REQ-406
live-round fold was removed from this endpoint entirely, not left dormant
— folding a still-changing round into a median has no resolved meaning
(see this REQ's own "no live-round component" bullet); `GetActiveRoundLeaderboardAsync`
(REQ-407) is untouched. The now-dead `GetTotalFinalPointsByUserIdsAsync`/
`GetUserIdsWithAnyGuessAsync` repository methods were removed (no other
callers). See REQ-404's own added status note for what it now describes as
superseded interim behavior.)*
> As a player, I want the all-time leaderboard to rank players by how
> consistently they perform per round, not by a raw cumulative total that
> only ever grows the more rounds someone plays, and only once they've
> played enough rounds for that comparison to be meaningful, so a player
> with a long, consistent history isn't ranked behind someone who has
> only played a small, lucky handful of rounds.

- **Context:** REQ-401/404's all-time leaderboard ranks by
  `SUM(FinalPoints ?? 0)` ascending (ADR-0021: lowest total wins). Under a
  pure sum, every closed round a player plays adds strictly more to their
  total — there is no way a round reduces it — so a player who has played
  50 rounds necessarily carries more accumulated total than one who has
  played 2, independent of actual per-round performance. The sum measures
  volume as much as it measures skill; this REQ replaces it with a measure
  that doesn't.
- **Product owner's decision (2026-07-20):** the all-time leaderboard
  ranks players by their **median per-round score**, not the sum, and a
  player must have played **at least 5 rounds** before they qualify to
  appear on the ranked list at all.
- **Per-round score used for the median:** for each qualifying round (see
  below), the same per-round total REQ-408 already defines and computes
  for its closed-round leaderboard — `SUM(FinalPoints)` for that player,
  that round only. This REQ introduces no new per-round metric; it only
  changes how those existing per-round totals are combined into a single
  all-time ranking number.
- **"Played a round" / qualifying-round definition:** a round counts
  towards both the 5-round minimum and the median itself if and only if it
  is **closed** (`Round.ClosedAt` is set, REQ-408) **and** the player has
  at least one `Guess` row in it — the same "at least one guess in this
  specific round" participant definition REQ-406/407/408 and ADR-0021's
  `MaterializeUnansweredCellsAsync` already use. This is a **different**
  check from the existing `IGuessRepository.GetUserIdsWithAnyGuessAsync`
  REQ-404 already uses for its zero-guess-ever exclusion — that method
  answers a yes/no question ("has this user ever submitted any guess, in
  any round at all, closed or still active") and does not count rounds.
  REQ-409 needs a per-round, closed-rounds-only count, so it requires a
  new query (the exact method name/shape is an implementation detail, not
  part of this REQ), not a reuse of that existing boolean method. An
  active (unlocked) round is never a qualifying round, matching
  REQ-401/404/405's existing locked-only rule for all-time computations.
- **Median definition:** the standard median of the qualifying rounds'
  per-round totals — the middle value once those totals are sorted
  ascending, or the arithmetic mean of the two middle values when the
  qualifying-round count is even. The median is computed over **every**
  qualifying round the player has ever played, not only their 5 most
  recent — the 5-round minimum is a qualification floor, not a rolling
  window.
- **Scope: this replaces, rather than adds to, REQ-401/404's existing
  all-time ranking.** The product owner's own framing — making "the
  all-time leaderboard" fairer — describes a correction to the existing
  ranking, not a new, separate lens meant to coexist with the old one.
  Contrast REQ-406/407/408: each of those answers a genuinely different
  question (live in-progress standing, one specific round, browsing past
  rounds) that a player might reasonably still want the old total for
  alongside it. REQ-409 answers the exact same question REQ-401/404's
  "All-time" scope already answers ("where do I rank overall?"), just with
  a fairer formula — there is no reason to keep the old, PO-identified-as-
  unfair sum as a second, coexisting tab. There remains exactly one
  "All-time" scope on the leaderboard screen (SCREEN-03); once this REQ is
  implemented, that scope's ranking is the median described here, and the
  raw-sum formula REQ-404 currently describes is retired for ranking
  purposes. Per this document's ID-stability rule, REQ-404's own text and
  status notes are not rewritten in place to reflect this — see REQ-404's
  own newly added status note, which cross-references this REQ instead of
  silently going stale.
- **No live-round component.** Unlike REQ-406's sum-based total, this
  median ranking does **not** fold in a live contribution from the
  currently active round. Precedent: REQ-405's round/week/month/year
  windows already remain locked-only and are explicitly unaffected by
  REQ-406 ("this REQ does not change REQ-405's... time-window leaderboard,
  which remains explicitly locked-only") — REQ-409 follows that same
  precedent rather than inventing a new one. Folding a live, still-
  changing round into a median (which round would count, and what
  per-round figure to use for a round still in progress) has no existing
  analogue in this document and is not resolved by this REQ; a live-
  updating version of this median, if ever wanted, is a separate future
  requirement.
- Given a player has fewer than 5 qualifying rounds (per the definition
  above)
- Then that player does not appear on the all-time ranked list at all —
  the same "absent, not ranked with a default value" exclusion pattern
  REQ-404's 2026-07-20 zero-guess exclusion already established, extended
  here from "zero qualifying rounds" to "fewer than 5 qualifying rounds"
- Given a player has 5 or more qualifying rounds
- When the all-time leaderboard is requested
- Then that player's rank is based on the median of their per-round
  `SUM(FinalPoints)` totals across every qualifying round they have ever
  played, sorted **ascending** — the lowest median wins (ADR-0021, same
  direction as every other ranking in this document)
- And ties (equal median) are broken by display name, ordinal
  case-insensitive comparison — the same tie-break rule used by every
  other leaderboard ranking in this document (REQ-404/405/406/407/408)
- And the currently active (unlocked) round never contributes to the
  median or to the qualifying-round count, regardless of how many guesses
  the player has made in it — matching REQ-401/404/405's existing
  locked-only rule
- And a player's median is recomputed from the full, current set of their
  qualifying rounds on every leaderboard read (no stored, precomputed
  median) — consistent with every other ranking in this document being
  computed from source rows on read, not maintained as a running/cached
  value

**Test level:** Unit (median computed correctly for an odd and an even
qualifying-round count; a player with exactly 4 qualifying rounds is
excluded while a player with exactly 5 is included and ranked; a round
still active never counts toward the 5-round minimum or the median
regardless of guesses made in it; sort order and tie-break match every
other leaderboard ranking in this document), API (all-time leaderboard
endpoint returns the median-based ranking; a below-threshold member is
absent from the response, not present with a placeholder value)

---

### 4.5 Data management and overrides

**REQ-501 – Manual override always wins**
> As an admin, I want to manually correct incorrect player data and be
> confident the correction is not overwritten on the next sync.

- **Status: Implemented (Tier 0, S-012), API only.** The override-precedence
  merge logic (`HasEffectiveAttributeAsync`, COMP-06/ADR-0015) predates this
  story (built alongside guess submission, S-009) — this story's addition is
  the admin-facing way to actually create/update/delete a `PlayerOverride`
  over HTTP: `POST/GET/PUT/DELETE /admin/player-overrides[/{id}]`
  (`XGArcade.Api.Admin.AdminEndpoints`), all behind the "Admin" authorization
  policy (`Admin__UserIds`, see `architecture-document.md` §7 and
  `implementation-document.md` §4). One override per `(PlayerId, Field)` —
  `POST` 409s if one already exists, matching ADR-0015's "replaces the
  entire attribute type" semantics; use `PUT` to change an existing
  override's value/reason instead. Covered end-to-end by
  `REQ501_CreatePlayerOverride_FlipsCellCorrectness_ForSubsequentGuess`,
  which submits a real guess, creates an override via the API, then
  resubmits and asserts the same cell flips from incorrect to correct. No
  admin UI/page exists (SCREEN-04 not built) — API only.
- Given a `PlayerOverride` record exists for a player field
- When a sync runs and updates `PlayerData` for the same field
- Then the effective data (used by the game) continues to use the override
  value, not the newly synced value
- And the sync must not delete or modify the `PlayerOverride` table

**Test level:** Unit (merge logic), Integration (full sync cycle with an existing override), API (S-012: override CRUD and the correctness-flip end-to-end path)

**REQ-502 – Data source traceability**
> As an admin, I want to see where each data point came from, so I can judge
> its reliability.

- **Status: Partially implemented (Tier 0, S-012; UI added S-026).**
  `source` and `confidence` are visible via `GET /admin/player-data/unverified`
  (now rendered by `AdminScreen.tsx`, meeting the "UI (admin)" test level
  below), but only for rows with `Confidence == "unverified"` — there is
  still no admin endpoint or view over verified `PlayerData`, so "any
  player data point" (below) is not yet true; only the unverified subset is
  browsable. **S-052/ADR-0029:** that subset is now meaningfully smaller —
  a routine Wikidata sync persists `Confidence = "verified"` directly
  (`WikidataLookupOrigin.Sync`), so it never enters this list at all;
  only REQ-211's guess-time fallback still writes `"unverified"`. This
  narrows what's browsable further, it doesn't add the missing
  verified-data view.
- **Status note (2026-07-20, supersedes "only REQ-211's guess-time fallback
  still writes `"unverified"`" above; kept for history, not deleted):** that
  line is no longer current — see REQ-211's own 2026-07-20 status note
  (reverses ADR-0029's fallback-specific carve-out; a new ADR superseding
  ADR-0029 is pending). As of that decision, no code path persists
  `Confidence = "unverified"` anymore, so the "unverified" subset this
  status note describes as "browsable" is now empty by construction going
  forward, not merely smaller — see REQ-503's matching 2026-07-20 status
  note for what that means for the review queue itself. The still-missing
  verified-data-view gap this note already flags is unchanged.
- Given any player data point
- Then `source` (e.g. `wikidata`, `api_football`, `live_lookup`, `manual_override`)
  and `confidence` (`verified` / `unverified`) are always visible in the admin view

**Test level:** API, UI (admin)

**REQ-503 – Admin review of unverified data**
> As an admin, I want to quickly review and approve/correct auto-fetched
> data, so the cache is quality-assured over time.

- **Status: Partially implemented (Tier 0, S-012; UI added S-026).** Only
  the "review list" half is built: `GET /admin/player-data/unverified`
  (`XGArcade.Api.Admin.AdminEndpoints`, rendered by `AdminScreen.tsx` as of
  S-026) returns every unverified `PlayerData` row with
  `Source`/`Confidence`/`PlayerFullName`. The "correct" action exists only
  indirectly, as a separate call to `POST /admin/player-overrides` (by
  `PlayerId`/`Field`, not by the `PlayerData` row's own id) — there is no
  "approve → verified" action and no "remove the data point" action; a
  `PlayerData` row's `Confidence` cannot currently be flipped to `verified`,
  nor can a row be deleted, via any endpoint. "The action is logged with
  `admin_id` and a timestamp" is satisfied for the override-creation path by
  `PlayerOverride.LockedByAdminId`/`LockedAt` on the override row itself
  (no separate audit-log table) — there is no equivalent log for
  approve/remove since those actions don't exist yet.
- **S-052/ADR-0029 status note — this REQ's premise revised:** S-026 gave
  this endpoint its first real UI caller, which surfaced that the review
  list had reached 52,782 rows: every `PlayerData` row ever synced from
  Wikidata since S-006 landed here, because nothing had ever made
  `Confidence` conditional on anything. That doesn't match this REQ's own
  framing ("auto-fetched data" implies something worth spot-checking, not
  every routine sync) — ADR-0029 narrows what "auto-fetched" means here: a
  routine sync (grid-generation cache-miss or cache-warming) is now trusted
  as ground truth and persists `Confidence = "verified"` directly, never
  entering this list. Only REQ-211's guess-time fallback (a narrower,
  guess-triggered re-check) still writes `"unverified"` and lands in this
  queue — which is what "quickly review" and "quality-assured over time"
  below should have described from the start, once the sheer sync volume
  Tier 0 has since accumulated made it obvious "every sync" and "worth
  reviewing" aren't the same thing. The pre-existing 52,782-row backlog was
  bulk-cleared to `verified` via a one-time CLI verb
  (`verify-wikidata-player-data`), since no row records which of the two
  paths originally created it. The still-missing "approve"/"remove"
  actions above are unaffected by this change.
- **Status note (2026-07-20, supersedes the S-052/ADR-0029 note above
  where it describes REQ-211's guess-time fallback as still landing in
  this queue; kept for history, not deleted):** per REQ-211's own
  2026-07-20 status note (reverses ADR-0029's fallback-specific carve-out
  — a new ADR superseding ADR-0029 is pending), the guess-time fallback
  path now also persists `Confidence = "verified"` immediately. **This
  REQ's review queue (`GET /admin/player-data/unverified`) is therefore
  empty by construction going forward** — no code path writes
  `"unverified"` anymore. The queue and its endpoint are not being
  removed: ADR-0029's own follow-up note already earmarked this exact
  channel for a future player-suggestion/correction feature (still
  unbuilt, out of scope here) — when that exists, it becomes the queue's
  sole source, exactly as ADR-0029 originally anticipated. The
  still-missing "approve"/"remove" actions this REQ's status note flags
  are addressed below — see the 2026-07-20 extension for "approve,"
  including bulk/select-all.
- **Status note (2026-07-20, "remove" built):** `POST
  /admin/player-data/remove` (`AdminEndpoints`, Admin policy) closes the
  last gap — bulk-capable from the start like "approve," hard-deletes the
  `PlayerData` row (nothing in this codebase holds a foreign key to a
  specific `PlayerData` row id, so a real delete is safe, matching this
  REQ's own "remove," not "hide," wording), and does not require the row
  still be unverified (removal is a general corrective action, not tied to
  the review queue's current state — a row another admin already approved
  can still be removed). No new `RemovedByAdminId`/`RemovedAt` columns:
  once a row is deleted there's nothing left to attach them to, so "logged
  with admin_id and a timestamp" is satisfied via a structured `ILogger`
  line at removal time instead, matching this codebase's established
  preference against a general-purpose audit-log table.
  `AdminScreen.tsx` gained a "Remove selected" action alongside "Approve
  selected," same bulk-selection UI. **This REQ's acceptance criteria are
  now fully met** — approve, correct (via the pre-existing
  `PlayerOverride` path), and remove are all built.
- Given data with `confidence = "unverified"`
- When an admin opens the review view
- Then the admin can approve (→ `verified`), correct (creates a `PlayerOverride`),
  or remove the data point
- And the action is logged with `admin_id` and a timestamp

**Extended (2026-07-20, Status: Implemented, Tier 0, S-057) — the
"approve" action, including bulk/select-all, and confirming no reason is
required:**
- Given one or more `PlayerData` rows with `confidence = "unverified"` are
  visible in the review view
- When an admin selects exactly one row and approves it
- Then that row's `confidence` is set to `verified`, and the action is
  logged with `admin_id` and a timestamp — the same "action is logged"
  rule this REQ already states above, now made explicit for this specific
  action rather than only the pre-existing override-creation path
- And no `reason` field is required or accepted for this action — unlike
  `PlayerOverride`'s "correct" action (`POST /admin/player-overrides`,
  REQ-501), which continues to require a reason; approve is a separate,
  simpler action, and this extension does not change the override
  endpoints' existing reason requirement
- Given multiple `PlayerData` rows with `confidence = "unverified"` are
  visible in the review view
- When an admin multi-selects more than one row — including via a
  "select all" control that selects every row currently loaded in the
  view — and approves the whole selection in one action
- Then every selected row's `confidence` is set to `verified` as part of
  that one action, each logged individually with the same `admin_id` and
  timestamp (one admin action producing one audit entry per row, not a
  single ambiguous batch entry that can't be traced back to individual
  rows)
- And a bulk approve that partially fails (e.g. a row was deleted, or its
  confidence already changed, by another admin between selection and
  submission) reports which rows succeeded and which failed, rather than
  silently succeeding or failing the entire batch as one all-or-nothing unit
- And no `reason` field is required or accepted for the bulk form of this
  action either — same rule as the single-row case above
- **Out of scope for this extension:** bulk/multi-select "remove the data
  point." REQ-503's existing "remove" action (still unbuilt) remains
  single-row, scoped however a future story defines it — this extension
  covers "approve" only.
- **Built as (S-057):** built exactly as specified. `POST
  /admin/player-data/approve` (`XGArcade.Api.Admin.AdminEndpoints`, Admin
  policy) takes a list of `PlayerData` ids (a single id is just the N=1
  case, no separate single-row endpoint); `IPlayerStoreRepository
  .ApprovePlayerDataAsync`/`PlayerStoreRepository` evaluates each id
  independently in one `SaveChangesAsync` call and returns a per-id
  outcome (`NotFound`/`NotUnverified`/success), never an all-or-nothing
  batch result. Audit fields (`PlayerData.ApprovedByAdminId`/`ApprovedAt`,
  new columns via the `AddPlayerDataApproval` migration) mirror
  `PlayerOverride.LockedByAdminId`/`LockedAt`'s existing shape rather than
  a separate audit-log table — satisfying "the action is logged with
  `admin_id` and a timestamp" the same way the override path already does.
  `AdminScreen.tsx` (SCREEN-04) adds the checkbox/"select all"/"Approve
  selected" UI, no `reason` field, and a per-row results list after
  submit.

**Test level:** API, UI (single approve; bulk approve including
select-all; no `reason` field required or accepted, for either form;
partial-failure reporting on a bulk approve; unaffected: `PlayerOverride`'s
existing reason requirement, and "remove" staying single-row/out of scope)

**REQ-504 – Admin UI page** *(Status: Implemented, Tier 0, S-026)*
> As an admin, I want an actual page (not just API calls) to perform admin
> actions, so I don't need to script HTTP requests to correct data, manage
> rounds, or manage users.

- **Built as (S-026):** `frontend/src/admin/AdminScreen.tsx` (SCREEN-04),
  reachable only from a new "Admin" header nav link (`App.tsx`) rendered
  only when `GET /auth/me`'s `MeResponse.IsAdmin` is `true` — a new field
  computed server-side by `AuthController.Me` via the same `Admin:UserIds`
  check `AdminAuthorizationHandler` itself uses (extracted to a shared
  static `IsAdminUserId` helper so the two can never disagree). Three
  sections: the REQ-501/502/503 unverified-data review/override-CRUD flow
  (always rendered — no Production restriction, matching this REQ's own
  acceptance criteria), REQ-505's round controls, and REQ-506's user
  deletion. The latter two sections are entirely absent from the DOM (not
  merely disabled) in Production — detected by the frontend via a 404 from
  REQ-505's `GET /admin/rounds/{gameKey}/active` probe endpoint, since that
  whole endpoint group is unregistered there (ADR-0006). A non-admin who
  reaches the page directly still gets a defense-in-depth "access denied"
  message from the page itself (its own 403 from the unverified-data
  fetch), independent of the nav-link hiding. Covered by
  `AdminScreen.test.tsx` (12 tests) and 2 new `App.test.tsx` cases (nav-link
  gating on `isAdmin`).
- **Status note (2026-07-19, entry point relocated per REQ-712/REQ-713):**
  the "Built as (S-026)" note above describes the screen as reachable from
  a standalone top-level "Admin" header nav link — that top-level link is
  superseded by REQ-713's "Settings" menu entry, which shows an
  admin-only link to this same, otherwise-unchanged `AdminScreen` only
  when the logged-in user is an admin. Nothing about `AdminScreen` itself,
  its authorization checks, or its Production-only section-hiding changes
  here — only how a player navigates to it. The "not linked from the
  normal player nav" and "no visible entry point" acceptance criteria
  below are unaffected by this relocation — if anything, REQ-713 restates
  them for the new entry point.
- Given the S-012 admin API (REQ-501/502/503) and REQ-505/506's new endpoints
  (this REQ adds no endpoints of its own — it is the UI surface over all of
  them) already require the existing "Admin" authorization policy
  (`Admin__UserIds`)
- When a user whose id is in `Admin__UserIds` logs in
- Then they can reach a protected admin screen (reached via REQ-713's
  "Settings" menu entry, not a standalone top-level nav link — see the
  status note above) exposing: the REQ-503 unverified-data review list and
  override CRUD (REQ-501/502/503), the REQ-505 round controls, and the
  REQ-506 user-management action
- And a non-admin user gets no visible entry point to it and a 403 from
  every underlying endpoint if they reach it directly
- And in `ASPNETCORE_ENVIRONMENT == Production`, the REQ-505/506 sections are
  not merely non-functional but not rendered at all (the page must not show
  dead buttons for endpoints ADR-0006 says don't exist in prod) — the
  REQ-501/502/503 override-review sections, which have no such
  Production restriction, remain visible

**Test level:** UI

**REQ-505 – Admin round control (non-Production only)** *(Status: Implemented,
Tier 0, S-026)*
> As an admin testing the game, I want to end the active round or adjust its
> schedule on demand, so I don't have to wait for real time to pass to test
> round-close behavior outside of the existing E2E harness.

- **Built as (S-026):** `GET/POST /admin/rounds/{gameKey}/active|close` and
  `PUT /admin/rounds/{gameKey}/end-time`
  (`XGArcade.Api.Admin.AdminManagementEndpoints`), all non-Production-only
  (fail-closed per ADR-0006 — the whole route group is never registered
  when `ASPNETCORE_ENVIRONMENT == Production`, checked before any route is
  mapped, never guarded only by the "Admin" policy) and additionally behind
  that "Admin" authorization policy. `POST .../close` reuses
  `IRoundCloseService.CloseRoundAsync` (REQ-205) directly — no second,
  independently-written close implementation. `PUT .../end-time` enforces
  the constraint below (400 Problem Details, titled "Invalid end time", if
  violated). **Deliberate deviation from the criteria as originally
  drafted:** `GET .../active` always returns `200 { hasActiveRound, round }`
  — including `hasActiveRound: false, round: null` when no round is active
  — rather than a not-found-style response for "no active round." This is
  not an oversight: it doubles as the frontend's only reliable way (REQ-504)
  to distinguish "this environment has the feature but no round is active
  right now" (a genuine `200`) from "this environment doesn't have the
  feature at all" (a genuine `404` from ASP.NET routing itself, since
  Production never registers the route group). Covered by
  `AdminManagementEndpointTests.cs` (22 tests total across REQ-505/506,
  including the Production-absence 404 case and the non-admin 403 case for
  every endpoint).
- **Relationship to REQ-806:** `POST
  /internal/test-data/force-close-round/{roundId}` already exists for
  automated E2E tests (REQ-806) but requires the round id and the
  `INTERNAL_JOB_TOKEN` bearer, not admin login — this REQ is the
  human-facing, admin-authenticated equivalent for manual testing, plus a
  new capability REQ-806 doesn't cover: adjusting a round's schedule rather
  than only closing it immediately.
- Given an admin is authenticated and `ASPNETCORE_ENVIRONMENT != Production`
- When the admin ends the currently active round for a game
- Then round-close (REQ-205) runs immediately for that round, exactly as it
  would at its real `end_time`
- Given an admin is authenticated and `ASPNETCORE_ENVIRONMENT != Production`
- When the admin sets a new `end_time` for the active round (must remain
  after `start_time` and after the current time, i.e. this cannot be used to
  retroactively close a round that already ended — REQ-205's lock behavior
  handles that path)
- Then the round's `end_time` is updated and reflected on the next `GET
  /rounds/current` read
- And in `ASPNETCORE_ENVIRONMENT == Production`, no endpoint backing either
  action is registered at all — same fail-closed pattern REQ-806/ADR-0006
  already established for `XGArcade.Testing`, checked in `Program.cs`
  before routing, never guarded only by an attribute

**Test level:** API, UI

**REQ-506 – Admin user deletion (non-Production only)** *(Status: Implemented,
Tier 0, S-026)*
> As an admin testing the game, I want to delete a test user's account, so I
> can clean up seeded/test accounts without touching the database directly.

- **Built as (S-026):** `DELETE /admin/users?email=`
  (`XGArcade.Api.Admin.AdminManagementEndpoints`), non-Production-only (same
  fail-closed gating as REQ-505) and behind the "Admin" authorization
  policy. Resolves the admin-supplied email to a local `User.Id` via new
  `IUserRepository.GetByEmailAsync` (case-insensitive, matching how
  Supabase Auth itself treats email), then calls the exact same
  `IAccountDeletionService.DeleteAccountAsync` REQ-710's self-service
  deletion uses — no second, independently-written deletion path, per this
  story's own explicit watch-out. Returns `404` if no user matches the
  email, `204` on success, and a `500` Problem Details response (logged with
  the target user id) if the underlying deletion fails. Covered by
  `AdminManagementEndpointTests.cs` and 2 new `UserRepositoryTests.cs` cases
  (case-insensitive email lookup).
- Given an admin is authenticated and `ASPNETCORE_ENVIRONMENT != Production`
- When the admin deletes a specified user
- Then the same anonymization behavior REQ-710 defines for self-deletion
  applies (the `User` row and credentials are removed, `Guess` rows are
  anonymized rather than deleted, per-user leaderboard/uniqueness history
  stays accurate) — this REQ does not define a second, different deletion
  behavior, only a second, admin-initiated way to trigger REQ-710's existing
  one
- And in `ASPNETCORE_ENVIRONMENT == Production`, no endpoint backing this
  action is registered at all, same fail-closed pattern as REQ-505

**Test level:** API, UI

---

### 4.7 Account creation and email confirmation

**REQ-701 – Create account with email and password**
> As a person, I want to create an account with my email and a password, so
> I can play and have my scores tracked.

- **Status: Implemented (Tier 0, S-004/S-011/S-016/S-017/S-062).** All
  acceptance criteria are now built. The
  16+ checkbox clause below is built and enforced server-side (`POST
  /auth/signup` rejects the request with 400 before ever calling Supabase
  Auth if the checkbox is false) — see ADR-0013 (backend-mediated
  signup/login) and `MVP-SCOPE.md`. As of S-011, the DisplayName clause
  below is also built and enforced server-side (`AuthController.Signup`
  rejects with 400 if `DisplayName` is empty or over 30 characters, before
  Supabase Auth is ever called) and client-side (`AuthScreen.tsx` blocks
  submission with "Choose a display name." without calling the API at
  all). As of S-016, the confirm-password clause below is also built and
  enforced the same way: server-side (`AuthController.Signup` rejects with
  400, "Passwords do not match", if `ConfirmPassword != Password`, checked
  before the DisplayName/AgeConfirmed checks and before Supabase Auth is
  ever called) and client-side (`AuthScreen.tsx` blocks submission with
  "Passwords do not match." without calling the API at all). As of S-017,
  the display-name-uniqueness clause below is also built: case-insensitive
  only (spaces/punctuation/formatting stay exactly as entered — a
  deliberate decision against reshaping this into a username-style field),
  enforced both as a pre-check (`AuthController.Signup` calls
  `IUserRepository.DisplayNameExistsAsync` before Supabase Auth is ever
  called, returning 409 "Display name already in use") and as a DB-level
  unique index (`User.NormalizedDisplayName`, `IX_Users_NormalizedDisplayName`)
  that a race between two concurrent signups falls back to
  (`UserRepository.AddAsync` catches the constraint violation and throws
  `DisplayNameAlreadyInUseException`, which the controller maps to the same
  409 rather than letting it surface as a raw 500). The password-policy
  clause (§5's default: minimum 8 characters, no forced complexity) is now
  enforced server-side (`AuthController.Signup` rejects under-8-character
  passwords with 400, checked first among the free local checks) and
  client-side (`AuthScreen.tsx`). **As of S-062**, the account-enumeration-safe
  error message is also built: every Supabase signup-rejection reason
  returns the identical generic body ("Check your email to confirm your
  account, or reset your password if you already have one.") rather than
  Supabase's own wording — deliberately applied to every rejection reason,
  not narrowed to the already-registered case, since a differently-worded
  message only for that one case would itself leak which case occurred;
  Supabase's real error is logged server-side, never returned to the
  client. REQ-606's signup/login rate limiting (10 requests/minute per IP,
  no queueing, ASP.NET Core's built-in `RateLimiting` middleware, 429 on
  exceeding) was built in the same change — see REQ-606's own status note.
  The rest
  of this requirement's acceptance criteria are recorded below as the
  full/long-term definition, not a claim of current behavior.
- Given a person provides an email address and a password meeting the
  platform's password policy
- And they confirm the password by re-entering it in a second field, which
  must match exactly — a mismatch blocks signup with a clear error
  ("Passwords do not match") before Supabase Auth is ever called
- And a display name between 1 and 30 characters — this is the only
  identity a leaderboard (REQ-401/404) ever shows another player; the
  account's email address is never shown to other players
- And the display name must be unique, case-insensitively, across all
  accounts — spaces and other formatting are not otherwise restricted or
  reshaped; attempting to sign up with a display name already in use (in
  any casing) is rejected with a clear, specific error before an account is
  created, not a generic failure, and does not affect the existing account
  using that name
- And they have checked a required confirmation "I am at least 16 years
  old" — self-declared, no age verification performed, but signup cannot
  proceed without it checked
- When they submit account creation
- Then an account is created in an unconfirmed state
- And attempting to register with an email address that already has an
  account returns a clear error, without the error text itself confirming
  or denying whether an account exists for that address (avoids account
  enumeration)

**Test level:** Unit, API

**REQ-702 – Unconfirmed accounts cannot play**
> As the platform, I want to prevent unconfirmed accounts from taking
> actions tied to a real identity, so scores and leagues stay trustworthy.

- Given an account that has not completed email confirmation
- When that account attempts to submit a guess, create a league, or join a
  league
- Then the action is blocked with a message explaining that email
  confirmation is required, plus a way to resend the confirmation email
- And browsing public content (viewing an active grid, public leaderboards)
  is not blocked by this rule

**Test level:** Unit, API

**REQ-703 – Confirmation email content and methods**
> As a person, I want to confirm my email either by tapping a button or by
> entering a code, so I can use whichever is more convenient.

- Given an account has just been created
- Then a confirmation email is sent to the provided address containing
  both a one-tap confirmation link and a numeric code the person can enter
  manually
- And confirming via either the link or the code marks the account confirmed
- And confirming via one method invalidates the other for that same
  confirmation request (using the code after already clicking the link
  returns a clear "already confirmed" message, not an error)

**Test level:** Unit, API, UI

**REQ-704 – Resend confirmation email**
> As a person who didn't receive or lost their confirmation email, I want
> to request a new one, so I'm not stuck unable to confirm my account.

- Given an unconfirmed account
- When the person requests the confirmation email be resent
- Then a new confirmation email is sent, respecting a minimum cooldown
  (default 60 seconds) between resend requests to prevent abuse
- And requesting a resend before the cooldown elapses returns a clear
  message stating how long to wait, not a generic error

**Test level:** Unit, API

**REQ-705 – Confirmation expiry**
> As the platform, I want confirmation links/codes to expire, so a stale,
> possibly-leaked confirmation credential can't be used indefinitely.

- Given a confirmation link or code has been issued
- Then it expires after a configurable period (default 24 hours)
- And attempting to confirm with an expired link or code returns a clear
  error that offers to resend a new one (REQ-704), rather than a generic failure

**Test level:** Unit, API

**REQ-706 – Round-result notification email (deferred to Phase 2)**
> As a player, I want to optionally receive an email when a round I played
> closes, summarizing my final score, so I don't have to remember to check back.

- **Status: Deferred.** Not required for the MVP; recorded now for planning
  purposes so the account/notification data model accounts for it from the
  start (see `implementation-document.md`).
- Given a new account is created
- Then `NotificationPreference.RoundResultsOptIn` defaults to `true`
  (opted-in by default)
- Given a round they participated in closes and scores are locked (REQ-205)
- When the person is opted in
- Then they receive an email summarizing their final score and per-cell
  results for that round
- And a person who has opted out receives no such email
- And every notification email includes a working unsubscribe/opt-out action,
  and acting on it takes effect immediately (no "still receives the next one" gap)

**Test level:** Not yet applicable (deferred) — acceptance criteria recorded
for future implementation, not for current test coverage

**Compliance note:** opt-in-by-default is fine for a transactional
notification directly tied to something the person actively did (played a
round they signed up to play) — this is generally treated as "service
communication" rather than marketing consent. If this ever expands to
include promotional content (new features, re-engagement nudges) rather
than pure round results, that's a materially different consent question
under GDPR (the platform's primary user base is in the EU) and should get
its own explicit opt-in separate from this one, not be folded into it.

---

### 4.8 Non-functional requirements

**REQ-601 – Testability**
- All business logic (scoring, grid generation, override merging) must be
  isolated in testable units with no dependency on a database or network (unit-testable)
- All API endpoints must have automated API tests (happy path plus at least
  one error scenario per endpoint)
- Critical user flows (guess, view results, create league) must be covered
  by automated UI tests

**REQ-602 – Cost envelope**
- The system must be runnable within free tiers for hosting, database, and
  scheduling during the MVP phase (see implementation document for concrete choices)

**REQ-603 – Data consistency under concurrent guesses**
- Uniqueness calculation must handle concurrent guesses correctly (no race
  conditions producing an incorrect percentage)

**REQ-604 – Performance**
- Page loads showing the live uniqueness percentage must respond within a
  reasonable time (< 1s for typical cell volumes) even with a few thousand
  guesses per cell

**REQ-605 – Cache growth boundaries**
- The local data cache must remain proportional to actual usage (only
  storing data for combinations that have actually been requested by a
  generated grid), never requiring bulk/speculative data imports

**REQ-606 – Security baseline**
- **Status note (2026-07-20, S-062): the rate-limiting bullet below is now
  implemented**, scoped exactly as written — signup/login only, not every
  endpoint. `[EnableRateLimiting("auth-signup"/"auth-login")]` on
  `AuthController`'s `Signup`/`Login` actions, two named fixed-window
  policies registered in `Program.cs` (ASP.NET Core's built-in
  `Microsoft.AspNetCore.RateLimiting`, no new package): 10 requests/minute
  per client IP, `QueueLimit = 0` (no queueing — over-limit requests are
  rejected immediately, not delayed), 429 with a `{title, detail}` body the
  existing frontend error path already renders without special-casing.
  Every other REQ-606 bullet was already satisfied before this change.
- All traffic between frontend, backend, and database must use HTTPS/TLS;
  no plaintext transport anywhere
- Password credentials are never stored or logged by the platform's own
  code — they are handled entirely by the auth provider (see
  `architecture-document.md` ADR-0004)
- Admin-only actions (data review/approval, template/schedule configuration)
  must be rejected with an authorization error if attempted by a
  non-admin account, verified by an automated test per admin endpoint
- All user-supplied input (guesses, league names, admin corrections) is
  validated server-side regardless of client-side validation
- Dependency vulnerabilities are checked automatically in CI (both backend
  and frontend package manifests) and a failing check blocks merge for
  known-high/critical severity issues
- Sign-up and login endpoints apply rate limiting per IP/account to reduce
  brute-force and account-enumeration risk (see REQ-701's enumeration note)
- Cross-origin requests are restricted via CORS to the known frontend
  origin(s) only — never a wildcard — matching `architecture-document.md`
  §3's security middleware pipeline, which already described this as part
  of what "realizes REQ-606" before this bullet made it explicit here

**REQ-607 – Performance baseline**

- **Status: Implemented (Tier 0, S-034, 2026-07-17).** The pagination
  clause immediately below, previously a real, unmet gap (flagged by an
  architecture-reviewer pass during S-011 and deliberately left unfixed at
  the time), is now closed: `GET /leagues/global/leaderboard`
  (`XGArcade.Api.Leagues.LeaderboardEndpoints` →
  `ILeaderboardService`/`LeaderboardService`, `XGArcade.Core.Leagues`)
  takes optional `cursor`/`pageSize` query params (default `pageSize` 50,
  max 100, `cursor` defaults to 0, negative `cursor` or an out-of-range
  `pageSize` → 400) and returns a bounded page — `Rows` (each with an
  explicit, global 1-based `Rank`, not a page-local index),
  `RequestingUserRow` (always populated, even when the caller's own rank
  falls outside the current page), `NextCursor`, and `HasMore`. Matches
  `implementation-document.md` §6's cursor-shaped contract; the underlying
  implementation still composes the full member list in memory and slices
  it there rather than doing DB-level `ORDER BY`/`LIMIT` — an explicit,
  already-documented MVP-scale tradeoff, not a new gap (see that section's
  "Built as (S-034)" note). SCREEN-03's frontend (`LeaderboardScreen.tsx`)
  consumes this via a "Load more" button and a pinned "you" footer. The
  other two bullets below are unaffected by this note.
- Leaderboard queries (REQ-404) must be paginated; the API must never
  return an entire league's membership in one unbounded response
- Guess correctness/uniqueness lookups (REQ-203, REQ-204) must use indexed
  queries — no full-table scans on the `Guess` table for a single cell's
  calculation
- The system must support REQ-604's response-time target at a minimum of
  10x current expected load, not just current traffic, so a moderate
  growth in players doesn't require an emergency fix

### 4.9 Testability and environment management

**REQ-801 – Test-data endpoints are dev-only**
> As a developer, I want a way to create and reset test data safely, so
> automated and manual testing never touches or risks production data.

- Given a dev environment
- Then a test-data management API is available for creating and resetting data
- Given a production environment
- Then that same API does not exist (returns 404), not merely "access denied"

**Test level:** API (must be tested in both environment configurations)

**REQ-802 – Reset to a known baseline**
> As a developer, I want to reset dev data to a known baseline before a
> test run, so tests are repeatable and don't interfere with each other.

- Given a dev environment
- When a reset is triggered via the test-data API
- Then all rounds, guesses, leagues, and synthetic users created by tests
  are removed and a defined baseline dataset is (re)created
- And this operation is safe to run repeatedly without manual cleanup

**Test level:** API, and used as setup/teardown by the E2E test suite itself

**REQ-803 – Create synthetic test scenarios**
> As a developer, I want to create specific test scenarios (a round at a
> given stage, pre-existing guesses with known uniqueness, a synthetic user
> in a given league), so I can test specific behaviors deterministically.

- Given a dev environment
- When a test scenario is requested via the test-data API (e.g. "a round
  with N cells already guessed, M seconds from closing")
- Then the described data is created deterministically, without requiring
  the caller to know internal ID generation details
- And created synthetic users are clearly distinguishable from any synced
  real-looking data (e.g. a reserved email domain or naming convention)

**Test level:** API, used by E2E tests as setup

**REQ-804 – Sync of game/reference data between prod and dev (fallback direction: prod → dev)**
> As a developer, I want dev to be able to catch up with game data changed
> directly in production, so dev doesn't go stale relative to prod when
> that happens — while never exposing real user accounts.

- Given a sync is triggered (manual only, never scheduled)
- Then only game/reference data (footballer/club/trophy data, grid
  templates — the explicit allowlist in `lib/game-data-tables.sh`) is
  copied from production into dev
- And user accounts, leagues, guesses, rounds, notification preferences,
  and all auth-provider tables are never included in this sync, regardless
  of direction — this is a categorical exclusion (results and customer
  data are never eligible), not just an incidental omission
- And the sync never writes to production — this direction is one-way,
  and is the fallback path, not the recommended workflow (see REQ-805)

**Test level:** Integration (verify excluded tables are genuinely never
touched by the sync script), Manual (verify sync output before first
production use)

**REQ-805 – Promotion of game/reference data from dev to prod (recommended direction)**
> As a developer, I want to build and curate game data safely in dev and
> then ship it to prod, so dev is where experimentation happens and prod
> only receives verified results.

- Given game/reference data has been built up or corrected in dev
- When a promotion is triggered (manual only, never scheduled)
- Then only the same game/reference-data allowlist as REQ-804 is copied
  from dev into production
- And user accounts, leagues, guesses, rounds, notification preferences,
  and all auth-provider tables are never included, regardless of direction
  — the same categorical exclusion as REQ-804, enforced by the same shared
  allowlist file so the two directions can't drift apart
- And this action requires a more explicit confirmation than REQ-804's
  sync, since it writes to what real users may be actively playing against
- And this is the recommended day-to-day workflow — REQ-804's direction
  exists only for the "prod changed directly" fallback case

**Test level:** Integration (verify the same excluded-table guarantees as
REQ-804), Manual (verify promotion output before first production use)

---

**REQ-806 – Minimal round-closure control for automated testing (Tier 0)**
> As a developer, I want to deterministically close a round during
> automated tests, so scoring/leaderboard behavior (REQ-205/206) can be
> tested without waiting for real time to pass.

- Given `ASPNETCORE_ENVIRONMENT` is not `Production`
- When a test calls `POST /internal/test-data/force-close-round/{roundId}`
- Then the round-close job's normal logic (REQ-205) runs immediately for
  that round, exactly as it would at its real `end_time`
- And this endpoint is never registered when `ASPNETCORE_ENVIRONMENT ==
  Production` — enforced in startup configuration, same discipline as REQ-801
- And test users and guesses are created via the **real** signup/guess
  endpoints, not a separate seeding API — `@test.invalid` addresses
  (REQ-803's convention) distinguish them without needing dedicated
  creation endpoints, since Tier 0 has no email-confirmation friction blocking it

This is deliberately narrower than REQ-801-804's full vision (a
persistent, remotely-deployed, admin-visible dev environment with a
complete reset/scenario API) — this is the one piece Tier 0 can't work
without, scoped to the local/ephemeral stack `ci.yml` already runs E2E
against. REQ-801-804 remain the Tier 1 target once a real dev environment exists.

**Test level:** Integration (endpoint absent when Production), E2E (full
flow: signup → guess → force-close → verify locked score)

---

**REQ-807 – Minimal guessable-round seeding for automated testing (Tier 0)**
> As a developer, I want to deterministically create a round with a known,
> guessable cell during automated tests, so UI/E2E behavior (REQ-201/203/210/303)
> can be tested without depending on Wikidata's live, timing-variable query
> service being reachable at all from the test environment.

- **Status: Implemented (Tier 0, S-010).** Added for the same reason
  REQ-806 exists: unlike guesses/users (created via the real signup/guess
  endpoints, per REQ-806's own convention), a real playable round's grid
  content genuinely cannot be created deterministically without either a
  live Wikidata call (network-dependent, observed taking 9-27s per query,
  ADR-0011's addendum) or direct database access — and Playwright, running
  against a separately-started API process, has neither. `POST
  /internal/test-data/seed-guessable-round`
  (`XGArcade.Api.Rounds.InternalRoundEndpoints`) creates a `GridInstance`
  with one cell and a `Player` whose `PlayerAttribute` rows satisfy it,
  entirely through each owning component's normal repository write paths
  (`IGridInstanceRepository`/`IPlayerStoreRepository`/`IRoundRepository` —
  ADR-0006 boundary rule 4), never a raw table write. **Extended, not
  replaced, in S-011:** the endpoint now also seeds a second valid player
  ("Robert Pires") satisfying the same cell, so two different players can
  each submit a different correct answer — needed for a meaningful REQ-204
  live-uniqueness test (a single valid answer can only ever show "0%
  unique"). The response gained `AlternateCorrectPlayerName` alongside the
  existing `CorrectPlayerName`; the acceptance criteria below are otherwise
  unchanged.
- Given `ASPNETCORE_ENVIRONMENT` is not `Production`
- When a test calls `POST /internal/test-data/seed-guessable-round`
- Then an active Round and a single-cell `GridInstance` are created, together
  with one `Player` whose `PlayerAttribute` rows satisfy that cell's row and
  column categories
- And the response returns the created round id, cell id, and the exact
  correct player name, so a test can deterministically submit both a correct
  and an incorrect guess
- And this endpoint is never registered when `ASPNETCORE_ENVIRONMENT ==
  Production`, enforced in startup configuration, same discipline as
  REQ-801/REQ-806
- And test users are still created via the real signup endpoint (REQ-806's
  existing convention) — only grid/round content is seeded this way

**Test level:** Integration (endpoint absent when Production), used as E2E
setup by S-010's Playwright suite

---

### 4.10 Account and data rights

**REQ-710 – Account deletion** *(Status: Implemented, Tier 0, S-025/S-039)*
> As a user, I want to permanently delete my account, so I control my own
> data (this is a legal right under GDPR for EU users, and good practice regardless).

- Given a logged-in user requests account deletion
- When the deletion is confirmed (a confirmation step is required — this
  is irreversible)
- Then the user's `User` record, credentials (via the auth provider), and
  `NotificationPreference` are permanently deleted
- And the user's past `Guess` records are anonymized (the link to the
  deleted user is severed) rather than deleted outright — this preserves
  the accuracy of other players' historical uniqueness scores and
  leaderboard standings, which depend on the total count of past guesses,
  while still removing the personal data (the connection between a person
  and their guesses)
- And the user can no longer log in, and their email becomes available for
  a new account to register with

**Built as (S-025):** `DELETE /auth/account` (`AuthController.DeleteAccount`),
`[Authorize]`-protected. The "confirmation step" is the caller re-submitting
their current password, re-verified against Supabase Auth
(`ISupabaseAuthClient.SignInWithPasswordAsync`, the same call `Login` uses)
before anything is touched — a 401 on a wrong password, not a bare
confirmation flag a client could set without the user re-affirming intent.
The actual anonymize/delete logic is `IAccountDeletionService`/
`AccountDeletionService` (`XGArcade.Core.Auth`), built as reusable service
logic — identified by local `User.Id`, not a JWT/password — specifically so
`docs/backlog.md` S-026's admin-triggered deletion can call the identical
path rather than a second implementation. Order: anonymize `Guess` rows
(`IGuessRepository.AnonymizeByUserIdAsync`) → remove `LeagueMembership` rows
(`ILeagueRepository.RemoveMembershipsByUserIdAsync`) → delete the local
`User` row → delete the Supabase Auth identity last
(`ISupabaseAuthClient.DeleteUserAsync`, ADR-0026 — requires a new
`Supabase:ServiceRoleKey` secret, since the anon key Supabase Auth calls
otherwise use can't call the Admin API). **`NotificationPreference` is a
no-op**, not an oversight: no such table exists yet in Tier 0 (Resend/
notification preferences are Tier 1, `MVP-SCOPE.md`) — nothing to delete
until it's built. **Acknowledged gap:** the Supabase Auth deletion call is
not part of the same transaction as the local writes (it's a separate,
non-transactional HTTP call, matching `implementation-document.md` §6.8's
documented flow) — if it fails, local account data is already gone but the
credential/email is not; surfaced to the caller as a `500` rather than
swallowed, but no retry/saga exists yet (see ADR-0026's consequences).
**Gap identified and closed same-day (`docs/backlog.md` S-039, 2026-07-14):**
a scoping pass right after S-025 merged found that no frontend code called
this endpoint — S-025's own acceptance criteria was backend-only, so there
was no way for a real player to reach this flow from the app itself, and no
account/settings screen existed in `design-document.md` either. S-039
closed that gap, scoped narrowly to the delete-account flow only (no
general profile/settings page) — see "Built as (S-039)" below for what was
actually built.

**Built as (S-039):** the frontend UI this REQ's Given/When/Then always
implied but S-025 didn't build. A "Delete account" header link (the only
entry point — no general profile/settings page exists in Tier 0) opens
`DeleteAccountScreen` (SCREEN-05, `docs/design-document.md` §3): an
explicit irreversibility warning, then the current-password field that is
this REQ's confirmation step, re-verified server-side exactly as
`AuthController.DeleteAccount` already enforced — no bare confirmation
checkbox added on top of it. A wrong password shows an inline error and
deletes nothing; any other 401 (an expired/invalid JWT) signs the user out
the same way every other authenticated screen already does. On success the
user is signed out and returned to the login/landing screen, since no
account remains to show anything else on.

**Status note (2026-07-19, entry point relocated per REQ-712/REQ-713):**
the standalone top-level "Delete account" header link described above is
superseded by REQ-713's "Settings" menu entry, which now hosts this same,
otherwise-unchanged `DeleteAccountScreen` flow. The S-039 note's "no
general profile/settings page exists in Tier 0" aside is also now
outdated — REQ-713 introduces exactly such a screen, scoped narrowly to
delete-account (unchanged) plus an admin-only link (REQ-504); it is still
not a general profile/settings page in the broader sense (no other
account fields live there). Nothing about the deletion flow itself — the
password confirmation step, the anonymization behavior, or its tests —
changes here, only how a player navigates to it.

**Test level:** Unit (anonymization logic specifically — verify no
reversible link remains), API, UI (`frontend/src/auth/DeleteAccountScreen.test.tsx`,
`frontend/tests/unit/App.test.tsx`)

**REQ-711 – Data export**
> As a user, I want to export my data, so I have a copy and can verify what
> the platform holds about me (GDPR data portability).

- Given a logged-in user requests a data export
- Then they receive a machine-readable (e.g. JSON) export containing their
  account info, guess history, league memberships, and notification
  preferences
- And the export is provided within a reasonable timeframe (a synchronous
  API response is acceptable at this scale; no background job needed
  unless export size becomes a real problem)

**Test level:** API

**REQ-712 – Header navigation collapses behind a menu toggle on mobile**
> As a player using the app on a narrow viewport, I want the header
> navigation collapsed behind a single toggle control, so the header never
> overflows or wraps onto a second line no matter how many nav entries
> exist.

- **Context:** the header nav overflowed on mobile once before (fixed in
  S-029 by trimming duplicate items) and has regressed since REQ-504 and
  REQ-710 each added their own top-level link. REQ-713 addresses the
  regression's cause (too many top-level links) by consolidating two of
  them into one menu entry; this requirement addresses the layout symptom
  directly, so the header is robust to future growth in nav entries too,
  not just the current count.
- Given the viewport width is below the header's designated mobile
  breakpoint (a single breakpoint value defined once in
  `design-document.md`'s token system — which specific value, and whether
  it reuses an existing token such as SCREEN-01's 960px grid breakpoint or
  defines its own, is a design-document detail, not fixed by this
  requirement)
- When the header renders
- Then no nav entry (including "Leaderboard," "Settings" per REQ-713, and
  "Log out") is rendered as a visible top-level item in the header row —
  all of them are reachable only after activating a single toggle control
- And the toggle control is a real, focusable, keyboard-operable element
  (reachable via Tab, activated via Enter/Space) exposing `aria-expanded`
  reflecting its open/closed state, matching the accessible-disclosure
  pattern already established for REQ-204's reveal toggles
- And activating the toggle reveals the full nav item list; the list can be
  dismissed by activating the toggle again
- Given the viewport width is at or above the mobile breakpoint
- When the header renders
- Then every nav entry remains visible as a horizontal row exactly as
  today, and no toggle control is rendered at all — this is a mobile-only
  layout change, not a change to desktop's existing pattern
- And regardless of viewport width, the header nav row itself never wraps
  onto a second line or causes horizontal overflow, for any nav entry count
  up to what currently exists ("Leaderboard," "Settings," "Log out")

**Test level:** UI (component test: toggle hidden/absent above the
breakpoint, present and functional below it; `aria-expanded` reflects
open/closed state), E2E (Playwright, real viewport widths on both sides of
the breakpoint — nav never wraps or overflows at either)

**REQ-713 – "Settings" screen consolidates the delete-account and admin
entry points**
> As a player, I want a single "Settings" menu entry that gives me access to
> account management (and, if I'm an admin, admin tools), so the header
> doesn't need a separate top-level link per action.

- **Label choice:** "Settings," not "Profile" — chosen to match the
  header's existing plain, functional-noun copy voice ("Leaderboard,"
  "Admin," "Log out") rather than introduce a more personal/identity-toned
  word, and because "Profile" would misdescribe a screen whose contents
  (account deletion, an admin link) aren't profile information. This
  replaces the standalone "Delete account" and "Admin" top-level links
  described in REQ-710's and REQ-504's own "Built as" notes — see the
  status notes added to each.
- Given a logged-in user opens the header nav menu (REQ-712)
- Then it contains exactly one entry, labeled "Settings," in place of the
  previously separate "Delete account" and, for admins, "Admin" top-level
  links
- When the user selects "Settings"
- Then a new screen is shown containing the existing delete-account flow —
  REQ-710's behavior, acceptance criteria, and confirmation step, unchanged
- And, only when the logged-in user is an admin (the same check REQ-504
  already uses), the screen also shows a link that navigates to the
  existing, unchanged `AdminScreen` (REQ-504) — a link to that screen, not
  admin controls embedded inline on the Settings screen itself
- Given a non-admin user opens the Settings screen
- Then no admin link, admin-referencing text, or any other trace of an
  admin entry point appears anywhere on the screen or in the nav menu —
  the same "no visible entry point for a non-admin" guarantee REQ-504
  already makes for its own screen, now also true of this one
- And a non-admin who reaches the `AdminScreen` route directly (bypassing
  the UI) still gets REQ-504's existing defense-in-depth 403/access-denied
  behavior, unchanged by this requirement

**Test level:** UI (component test: non-admin sees the delete-account flow
only and no admin link, in the Settings screen and in the nav menu; admin
sees both, and the admin link navigates to `AdminScreen`; the delete-account
flow within Settings still passes REQ-710's existing tests unmodified)

---

**REQ-714 – Edit display name from Settings** *(Status: Implemented, Tier 0,
S-058, 2026-07-20)*
> As a player, I want to change my display name from the Settings screen,
> so I can update how I appear on the leaderboard without creating a new
> account.

- **Status note (S-058):** built exactly as drafted below. `PUT
  /auth/display-name` (`AuthController.UpdateDisplayName`) reuses REQ-701's
  exact 1-30 character bound and `IUserRepository.DisplayNameExistsAsync`
  uniqueness check, now with an `excludeUserId` parameter so a no-op
  resubmission of the caller's own current name — including a pure-casing
  change — is never treated as a conflict against itself; a losing race
  against another caller's concurrent signup/edit falls back to the same
  `DisplayNameAlreadyInUseException` → 409 path `Signup` already uses.
  `frontend/src/settings/SettingsScreen.tsx` hosts the edit form, and
  `App.tsx` updates the in-memory `currentUser.displayName` on success so
  every other screen reflects it immediately without a re-fetch. Covered by
  `UserRepositoryTests.cs`, `AuthEndpointTests.cs` (including an explicit
  exact-30-character boundary test), and `SettingsScreen.test.tsx`.
- **Context:** `frontend/src/settings/SettingsScreen.tsx` today only hosts
  the delete-account flow (REQ-710) plus, admin-only, a link to
  `AdminScreen` (REQ-504/713) — there is no way to change `User.DisplayName`
  after signup. `User.DisplayName`'s setter already keeps
  `NormalizedDisplayName` in lockstep (`User.NormalizeCase`), and
  `UserRepository.DisplayNameExistsAsync` plus the DB-level unique index on
  `NormalizedDisplayName` (`IX_Users_NormalizedDisplayName`,
  `UserRepository.AddAsync`'s race-fallback) are the exact mechanism
  REQ-701 already uses to enforce case-insensitive uniqueness at signup —
  this REQ reuses that same mechanism for an edit, not a new one.
  Confirmed by reading `Guess.cs` and `LeaderboardService.cs`: neither
  `Guess` rows nor any leaderboard computation (REQ-401/404/406/407/408)
  denormalizes `DisplayName` onto another table — every read resolves it
  live via `User.Id` (`IUserRepository.GetByIdsAsync`/`GetByIdAsync`) — so
  a name change needs no backfill of historical `Guess`/leaderboard data
  to take effect everywhere that name is shown.
- Given a logged-in user opens the Settings screen (REQ-713)
- When they submit a new display name between 1 and 30 characters (the
  same length bound REQ-701 already enforces at signup)
- Then the account's `DisplayName` is updated, and the new name is what
  every subsequent read of that account's identity shows — on leaderboards
  (REQ-401/404/406/407/408), and anywhere else the account's canonical name
  is resolved via `User.Id` (e.g. REQ-212's guess-reveal name) — with no
  backfill of past `Guess` or leaderboard rows required or performed, since
  none of them store a copy of the name
- And the new name is checked for uniqueness case-insensitively across all
  accounts, using the same mechanism REQ-701 already establishes at
  signup — a name already in use by a different account (in any casing) is
  rejected with a clear, specific conflict error, not a generic failure,
  and the account's existing display name is left unchanged
- And submitting the account's own current display name unchanged
  (including a resubmission that differs only in casing from what's
  already stored) is never treated as a conflict against itself — the
  uniqueness check must exclude the account's own existing row
- And a display name outside the 1–30 character bound is rejected with a
  clear error, before any database write, the same way REQ-701 already
  validates it at signup

**Test level:** Unit (uniqueness check excludes the account's own row;
length validation), API, UI (Settings screen edit form; conflict error
shown inline, not a generic failure)

**REQ-715 – Persistent login (remember-me) via refresh token** *(Status:
Implemented, Tier 0, S-058, 2026-07-20)*
> As a player, I want to stay logged in across sessions without re-entering
> my password every time, so I don't have to sign back in every time I
> return to the app while my session is still valid.

- **Status note (S-058):** built exactly as drafted below, plus one
  deliberate omission called out at implementation time: no explicit
  server-side refresh-token revocation call on logout — REQ-715's own
  acceptance criteria below only require clearing the frontend's stored
  copy, which `App.tsx`'s `handleLogout` does (alongside the access
  token); account deletion (REQ-710) already invalidates any outstanding
  refresh token as a side effect of deleting the underlying Supabase
  identity, so no separate revoke call was added there either. `POST
  /auth/refresh` (`AuthController.Refresh`, `ISupabaseAuthClient
  .RefreshTokenAsync`) is unauthenticated by design (the caller's access
  token may itself be missing/expired) and mediates through Supabase Auth
  the same way `/auth/login`/`/auth/signup` already do (ADR-0013), sharing
  `SupabaseAuthClient`'s request plumbing rather than a parallel
  implementation; `LocalE2EAuth` implements the same contract
  deterministically for the local E2E stack. Storage location
  (`localStorage`, alongside the access token) is ADR-0033's own decision,
  not repeated here. Covered by `AuthEndpointTests.cs` and
  `frontend/src/App.test.tsx`.
- **Context:** `frontend/src/App.tsx` now stores both the Supabase access
  token and the refresh token in `localStorage`; the backend's `POST
  /auth/login` response (`AuthController.Login`, `LoginResponse
  .RefreshToken`) already carried a refresh token — Supabase Auth returns
  one on every successful token exchange — but `AuthScreen.tsx` previously
  destructured only `accessToken`, discarding it, and no refresh flow
  existed anywhere in the frontend or backend. Per ADR-0013, the frontend
  never calls Supabase Auth directly — any refresh mechanism must be
  mediated through the backend, the same way `POST /auth/login`/`POST
  /auth/signup` already are, not a direct frontend-to-Supabase call.
- Given a person logs in successfully (`POST /auth/login`)
- Then the frontend stores the returned `RefreshToken` (already present in
  `LoginResponse`, previously discarded before this REQ was built), not
  only the access token, so it survives a page reload or a new browser
  session
- Given the frontend's stored access token is missing or expired, or a
  request to the backend receives a 401 that is not itself a
  wrong-password/wrong-credential response (e.g. not REQ-710's "Incorrect
  password" case)
- When the frontend has a stored refresh token
- Then it calls a new backend-mediated refresh endpoint — mediated through
  Supabase Auth exactly as `POST /auth/login`/`POST /auth/signup` already
  are (ADR-0013); the frontend never calls Supabase directly for this —
  which exchanges the stored refresh token for a new access token (and, if
  Supabase's own token rotation returns one, a new refresh token) without
  requiring the person to re-enter credentials
- And this renewal happens silently — the person is not shown a login
  prompt or otherwise interrupted, as long as the stored refresh token is
  still valid
- And a refresh attempt with an invalid, expired, or revoked refresh token
  fails clearly and signs the person out to the existing login screen — it
  never silently retries indefinitely and never leaves the app in a stuck,
  ambiguous authenticated-but-broken state
- And logging out, or account deletion (REQ-710), clears the stored
  refresh token, not only the access token — a stale refresh token must
  never outlive an explicit logout

**Test level:** Unit (refresh-endpoint request/response shape; expired/
invalid/revoked refresh token handling), API (refresh endpoint mediates
through Supabase Auth per ADR-0013 — the frontend layer of this is
verified never to call Supabase directly), UI/E2E (reloading the app with
a valid stored refresh token but a missing/expired access token stays
logged in without showing a login prompt; an invalid stored refresh token
returns to the login screen; logging out clears the stored refresh token)

**REQ-716 – Selectable color themes / dark mode** *(Status: Implemented
(S-064), 2026-07-20 — design pass and implementation both completed the
same day. A System/Light/Dark radio group on `SettingsScreen.tsx`
(`frontend/src/lib/theme.ts`'s `useThemePreference`), persisted in
`localStorage`, applied as a `data-theme` attribute on `<html>` via
`main.tsx`'s `applyStoredThemePreference()` before the React tree mounts
(no flash of the wrong theme). Every dark-theme token value in
`frontend/src/index.css`'s `:root[data-theme='dark']` block is copied
verbatim from `docs/design-document.md` §2's contrast-verified table (see
that section for the derivation; ADR-0034 for the mechanism/persistence
decision). Verified visually via a real Chromium screenshot (light/dark
side by side, both legible) in addition to the automated suite.
**Flagged, not silently passed over:** the login/signup submit button's
text color reuses `--color-surface-card` as its foreground (a
component-level token-reuse pattern, not one of the tokens the design
pass's audit table enumerated) — in dark theme this computes to a
measured 4.64:1 contrast against the green button background, clearing
the 4.5:1 AA floor but narrowly, and by coincidence rather than by
deliberate derivation. Worth a closer look if this pattern repeats
elsewhere or the token values ever shift.)*
> As a player, I want to choose a different color theme (e.g. dark mode)
> for the app, so I can use it comfortably in different lighting
> conditions or to match my own preference.

- **Context:** raised as part of a broader Settings-page expansion
  request.
- **Status note (2026-07-20 design pass):** every question this REQ
  previously left open (below) is now decided. `docs/design-document.md`
  §2 gained a full dark-theme token table — every existing color token
  that carries real information (`text-primary`, `text-muted`,
  `surface-card`/`surface-sunken`/`bg-base`, the `accent-green`/
  `accent-gold`/`accent-red` text/icon pairings) has a contrast-verified
  dark counterpart; the photo-overlay set (`overlay-scrim`,
  `accent-green-scrim`, and the `accent-gold`/`surface-card` foreground
  pairing used on it) needs no theme-specific value at all, since it's
  calibrated against a photo's own worst-case brightness, not the app's
  chrome. Layout, spacing, typography, and animation tokens are
  unaffected — this is a colors-only change.
- `docs/backlog.md` already flagged this as deserving its own design
  session rather than a quick story — this status note **is** that
  session's outcome, not a shortcut around it.

**Scope of "theme" (resolved):** three states, not a plain on/off toggle
— **System** (follows `prefers-color-scheme`, the default for anyone who
has never touched the setting), **Light**, **Dark** (either pins the
theme regardless of the OS setting). Not multiple named/branded themes —
REQ-716's own request text asks for "a different color theme (e.g. dark
mode)," singular, and `docs/design-document.md` §1's brand direction (real
football imagery, a quiet neutral shell) doesn't call for more than a
light/dark pair.

**Mechanism (resolved):** an explicit toggle on `SettingsScreen.tsx`
(SCREEN-08), not an automatic-`prefers-color-scheme`-only approach with no
in-app control — see `docs/design-document.md` §2's Dark theme subsection
for the full reasoning (short version: the request explicitly asks to
*choose*, not just to have the OS setting respected). The choice persists
in `localStorage` (a new key, device-local, no `User`-level/account-synced
row and no new backend endpoint — same reasoning ADR-0033 already used for
refresh-token storage: match the existing device-local pattern rather than
add new server-side surface for something this low-stakes at Tier 0).

- Given a player has never set a theme preference before
- When the app loads
- Then the UI renders using the OS-level `prefers-color-scheme` result
  (light or dark), re-evaluated live if the OS setting changes mid-session
  while "System" is selected
- Given a player opens Settings and selects "Light" or "Dark" explicitly
- When that choice is made
- Then the chosen theme applies immediately (no reload required), persists
  across reloads and new sessions via `localStorage`, and no longer
  follows the OS setting even if it changes
- Given a player has previously chosen "Light" or "Dark" explicitly
- When they select "System" again
- Then the app reverts to following `prefers-color-scheme` live, and the
  explicit pin is cleared from `localStorage`
- Given any of the four load-bearing correctness/state signals this app
  already never renders as color-only (REQ-204's points/attempt text,
  REQ-210's attempt count, the correct/incorrect icon-plus-text pairing)
- When the dark theme is active
- Then those signals remain text-paired, not color-only, in the dark
  theme exactly as they already are in light theme — this REQ changes
  color values only, never removes an existing text pairing
- Given every text/icon-on-background pairing `docs/design-document.md`
  §2 has previously verified for the light theme (body text, muted text,
  the three accent-*-text correctness colors)
- Then each has an independently-computed WCAG contrast ratio for its dark
  counterpart, documented in §2's Dark theme subsection — not assumed to
  carry over from the light-theme derivation

**Design questions this REQ previously left open — resolved 2026-07-20:**
- Scope of "theme" → **System/Light/Dark**, decided above
- Per-theme token values and re-verified contrast ratios → done, see
  `docs/design-document.md` §2's Dark theme subsection
- Persistence mechanism → **`localStorage`**, device-local, decided above
- Whether to also consider `prefers-color-scheme` → **yes, as the
  "System" default**, decided above

**Test level:** Unit/UI (Vitest) once built — the theme resolution logic
(System resolves to the live OS preference; Light/Dark pin regardless of
OS preference; the explicit choice persists across a simulated reload via
`localStorage`); visual/contrast verification is a manual/design-review
check against the ratios already computed in `docs/design-document.md`
§2, not an automated test. E2E: not required to gate merge (Playwright
only runs in CI per this repo's convention), but should get a smoke check
that switching the toggle actually changes rendered colors, once built.

---

### 4.11 Operational resilience

**REQ-901 – Database backups**
> As the platform operator, I want the production database backed up
> independently of the hosting provider's own guarantees, so a data-loss
> event doesn't mean permanent loss.

- Given production is hosted on a plan with no included automated backups
  (true of Supabase's free tier — see `infra/README.md`)
- Then an independent, scheduled backup process exports the production
  database on a recurring basis (daily) and stores it somewhere separate
  from the primary database
- And a documented restore procedure exists and has been tested at least
  once manually before being relied upon

**Test level:** Manual (restore drill), Integration (backup job itself
runs and produces a non-empty, valid export)

**REQ-902 – Failure alerting for scheduled jobs**
> As the platform operator, I want to know when an automated job fails, so
> a silent failure (a round that never gets generated, data that stops
> syncing) doesn't go unnoticed until a player reports it.

- Given a scheduled job (round generation, data sync, backups) fails
- Then the failure is surfaced to the operator without requiring them to
  actively check — at minimum via the CI/CD platform's own failure
  notifications, enabled and confirmed working, not just assumed to be on
  by default

**Test level:** Manual (deliberately break a job once and confirm a
notification arrives)

## 5. Decisions made as sensible technical defaults

The following were open questions in earlier drafts. They're implementation
details where a competent default is more useful than waiting on input, so
they're resolved here rather than left open. Revisit only if experience
shows the default is wrong.

- **Password policy (REQ-701):** minimum 8 characters, no forced
  complexity rules (no mandatory mixed-case/symbols) — this follows current
  NIST 800-63B guidance, which found forced-complexity rules push people
  toward predictable patterns rather than stronger passwords. Check new
  passwords against a breached-password list (e.g. via the HaveIBeenPwned
  range API) instead of arbitrary complexity requirements.
- **`allow_guess_change`:** already modeled as a per-`Round` field, not
  global (see `implementation-document.md` §5) — resolved by the existing
  data model, not a separate decision needed.
- **Synthetic test user naming (REQ-803):** reserved email domain
  `@test.invalid` (a domain reserved by RFC 2606 for exactly this kind of
  use, guaranteed never to be a real registrable domain) — e.g.
  `player1@test.invalid`. Immediately and permanently distinguishable from
  any real or synced account.
- **Max leagues/memberships per user:** default cap of 25 custom leagues
  created and 100 leagues joined per user, as a spam/abuse guard — generous
  for any real usage pattern, configurable if it turns out to be wrong.
- **Rate limiting thresholds (REQ-606):** 5 failed login attempts per
  15 minutes per account, 10 signup attempts per hour per IP, 1 confirmation
  resend per 60 seconds (REQ-704) — standard, conservative starting points;
  tune based on real abuse patterns once live.
- **Display name change frequency (REQ-714):** no cooldown or rate limit —
  an edit is treated like any other account-profile write, gated only by
  the same uniqueness check REQ-701 already enforces at signup. Revisit
  only if real abuse (e.g. rapid churn to impersonate another player on the
  leaderboard) is actually observed.
- **Refresh token lifetime/expiry (REQ-715):** governed entirely by
  Supabase Auth's own project-level session settings, not overridden by
  application code — consistent with ADR-0004/0013's boundary that the
  auth provider owns credential/session lifecycle, not `XGArcade.Core`.
  "Expired, invalid, or revoked" in REQ-715's acceptance criteria means
  whatever Supabase Auth itself reports at refresh time.

## 6. Product decisions (resolved 2026-07-05)

- **Round-result notifications default to opted-in** with easy unsubscribe
  (REQ-706). Treated as a service communication tied to active play, not
  marketing — see the compliance note under REQ-706 for the line that
  shouldn't be crossed without a separate, explicit opt-in.
- **V1 category types are Country, Club, and Trophy** (REQ-108). Position
  and era are explicitly out of scope for v1, not just deferred silently —
  revisit once Country/Club/Trophy has been played enough to know if more
  variety is actually needed.
- **Club badges in v1 are placeholder initial-chips only** (name/initials
  on a colored circle, as already in the mockups) — not real crest imagery.
  Real crest sourcing via API-Football (ADR-0008's `ClubCrest`
  caching approach) is **deferred to Phase 2**, same pattern as REQ-706's
  notification deferral: the data model and caching approach are already
  designed, but v1 ships without the actual integration to keep initial
  scope smaller. When built, this is a genuinely low-risk addition: API-Football's
  own documentation confirms logo/crest calls don't count against the
  request quota at all, and the universe of distinct clubs that ever
  appear as a category value across all grids is naturally small and
  largely static (a few hundred well-known clubs, not thousands) compared
  to the much larger space of individual player attribute lookups —
  fetched once per club, cached forever, essentially never revisited.
  Revisit the deferral itself once the core game loop is proven.

## 7. Open questions (remaining)

REQ-405's leaderboard time-window questions (the previous entry in this
section) were resolved 2026-07-12: calendar-aligned windows, UTC, locked
rounds only. See REQ-405's own status note and `docs/backlog.md` S-027.

REQ-409's participation-adjusted all-time score question was resolved
2026-07-20: the all-time leaderboard's ranking becomes a median of each
player's per-round `SUM(FinalPoints)` totals (locked rounds only, no live
component), gated by a minimum of 5 qualifying rounds to appear ranked at
all, replacing (not sitting alongside) the existing raw-sum ranking, with
the same display-name tie-break every other leaderboard ranking in this
document already uses. See REQ-409's own text for the full decision and
REQ-404's added status note for the interim state — implementation is a
separately queued story, not yet built.

REQ-716's selectable-color-themes/dark-mode question was resolved
2026-07-20: a System/Light/Dark toggle on `SettingsScreen.tsx`, persisted
in `localStorage`, with a fully token-valued and contrast-verified dark
theme in `docs/design-document.md` §2. See REQ-716's own status note and
that document's Dark theme subsection — implementation not yet queued in
`docs/backlog.md`.

No open questions remain from 2026-07-20 as of this pass.

Both items from the terms-of-service/privacy-policy drafting were
resolved 2026-07-06:

- **Minimum age:** 16, enforced via a self-declared checkbox at signup
  ("I am at least 16 years old") — no age verification performed, but
  signup cannot proceed unchecked. See REQ-701.
- **Governing law / entity:** Swedish law; operated as a personal project
  (not under SyVe or a separate registered entity) unless that changes
  later. See `docs/legal/terms-of-service-draft.md`.
