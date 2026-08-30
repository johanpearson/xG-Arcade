---
doc_id: requirements-document
title: Requirements Document
version: "2.24"
status: draft
last_updated: 2026-08-30
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

# Requirements Document – xG Arcade

Version 0.75 · 2026-07-20

> **Naming note:** "xG Arcade" is the overall product name (users, leagues,
> rounds, scoring — everything shared across games). **xG Grid** is the
> name of the first game built on the xG Arcade, not the platform itself.

> **Repository-split note (2026-08-11, S-106+S-107, ADR-0067):** any
> `IPlayerStoreRepository.<Method>` reference below dated before 2026-08-11
> refers to a method that has since moved to one of eight new sibling
> repositories (`IPlayerRepository`/`IPlayerDataRepository`/
> `IPlayerAttributeRepository`/`IPlayerAliasRepository`/
> `IPlayerOverrideRepository`/`IPlayerBackfillRepository`/
> `IPlayerCareerStintRepository`/`IPlayerDataQualityRepository`) —
> `IPlayerStoreRepository`/`PlayerStoreRepository.cs` no longer exist, now
> that both halves of the split have landed. See `architecture-document.md`'s
> COMP-06 entry and ADR-0067 for the full mapping. This was a pure refactor
> (no behavior change, no REQ changed), so historical dated entries below
> were not individually rewritten.

> **`GridGameModule`-split note (2026-08-11, S-119, ADR-0068):** any
> `GridGameModule.<Method>` reference below dated before 2026-08-11 may
> refer to logic that has since moved onto one of three new classes —
> `GridGenerationService` (`GenerateInstanceAsync`'s full pipeline,
> including the former `PickHeadersAsync`/`GetMatchCountAsync`),
> `GridNameMatcher` (`FindMatchAsync`'s three-stage matching,
> `AcceptMatchAsync`, disambiguation-candidate construction, and
> `ResolveWrongGuessPlayerAsync`), and `GridLiveLookupDispatcher`
> (live-lookup dispatch — `LookupMatchesAsync`, renamed from
> `LookupLiveMatchesAsync` — and REQ-211's guess-time fallback,
> `TryRefreshCellAsync`, renamed from `RefreshCellFromLiveLookupAsync`).
> Unlike ADR-0067's repository split, `GridGameModule.cs` was not deleted
> — it must keep implementing `IGameModule` directly for its real external
> callers — so `GridGameModule.ScoreSubmissionAsync`/`GetCellIdsAsync`/
> `GetMaxAttemptsForCellAsync`/`GetCellCategoryTypesAsync`/
> `ResolveWrongGuessPlayerAsync` references remain accurate (still defined
> there, now as thin delegation), but any reference to
> `GridGameModule.GenerateInstanceAsync`'s internal pairing/header-picking/
> cell-construction logic, `GridGameModule.FindMatchAsync`, or
> `GridGameModule`'s live-lookup dispatch now refers to logic on one of the
> three new classes above. Likewise, a "Test level" pointer below naming
> `GridGameModuleTests.cs` for generation/matching/live-lookup coverage may
> now live in `GridGenerationServiceTests.cs`/`GridNameMatcherTests.cs`/
> `GridLiveLookupDispatcherTests.cs` instead (moved 1:1, same test bodies/
> assertions) — a slimmed `GridGameModuleTests.cs` remains for the
> adapter's own orchestration tests. See `architecture-document.md`'s
> COMP-05 entry and ADR-0068 for the full mapping. This was a pure refactor
> (no behavior change, no REQ changed), so historical dated entries below
> were not individually rewritten.

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
- **Status note (2026-08-29, ADR-0089):** "no row category may be
  identical to a column category" below is now enforced as a per-
  `(CategoryType, Name)` equality check across all headers, not an
  axis-level "only filter by name if both axes share one type" branch —
  now that ADR-0089 lets each header pick its own category type
  independently, a Club-typed row header and a Club-typed column header
  must never collide even though the two axes are no longer uniformly
  typed. Candidates of a different `CategoryType` still never collide by
  name, same assumption as before. No change to the acceptance criterion's
  actual meaning, only to how it's implemented now that headers aren't
  grouped by axis.
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
  **Load-bearing caveat, updated (2026-08-09, ADR-0061):** `ReferenceDataSeeder`
  now seeds three trophies (Ballon d'Or, FIFA World Cup, UEFA Champions
  League), so `trophyCount(3)` clears `Size` for the default `GridSize = 3` —
  Country × Trophy and Club × Trophy are REACHABLE and selectable in
  production now, not just mechanically wired up. Trophy × Trophy still
  needs `trophyCount >= Size × 2 = 6`, so it remains structurally infeasible
  until the trophy pool grows further. See REQ-108's own status note for
  that requirement's full detail.
- **Status note (2026-08-29, ADR-0089) — supersedes the `SelectPairing`
  description above; kept for history, not deleted.** `SelectPairing`/
  `PoolFor` are removed entirely. There is no longer one pairing type
  chosen for the whole grid instance — each row and column header now
  picks its own category type (Country/Club/Trophy) independently,
  `GridGenerationService.GenerateInstanceAsync` drawing every header from
  one shuffled, combined pool of all three reference tables concatenated
  together (each candidate tagged with its own `CategoryType`), rather
  than a per-type pool selected by a once-per-instance pairing choice. A
  header's odds of being a given type are therefore naturally proportional
  to how much reference data that type actually has (today: 45 countries,
  21 clubs, 3 trophies), not an even 3-way split or a fixed feasibility
  table keyed off `Size`. A single grid can now mix category types freely
  across both axes (e.g. a Country row next to a Trophy column) — the
  acceptance criteria below never actually required axis-wide homogeneity,
  only the removed implementation happened to impose it. The
  Country×Country ban itself is now checked per individual (row header,
  column candidate) pair, inside `PickHeadersAsync`'s per-row loop, before
  that row's match-count query — replacing the old check that ran once per
  call against a globally-fixed pairing. This fixes the recurring "Ran out
  of candidates before completing the grid" generation failure
  (`docs/backlog.md` S-036) at its structural root — see ADR-0089 for the
  full reasoning and rejected alternatives. `MinValidAnswers` is unchanged
  (stays 5); this was an explicit, separately-considered-and-rejected
  option, not touched by this change.
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

- **Status: Implemented (Tier 0), full acceptance criteria now met.**
  Shipped in two stages: **S-031 (2026-07-20)** built individual awards only
  (Ballon d'Or), deliberately narrower than the acceptance criteria below;
  **S-095 (2026-08-09, ADR-0061)** shipped the previously-deferred
  team-competition remainder (FIFA World Cup, UEFA Champions League),
  completing this REQ's full v1 category-type definition.
  `TrophyDefinition` gained a `(Name)` unique index (S-031); `ReferenceDataSeeder`
  now seeds three trophies: **Ballon d'Or**, an individual award resolvable
  via Wikidata's `P166` ("award received") — the same simple query shape as
  the existing Country/Club intersection query
  (`WikidataClient.QueryTrophyCountryIntersectionAsync`/
  `QueryTrophyClubIntersectionAsync`, `IWikidataLookupService.
  LookupAndPersistTrophyCountryAsync`/`LookupAndPersistTrophyClubAsync`) —
  and **FIFA World Cup**/**UEFA Champions League**, team competitions
  resolvable only via a three-way join (ADR-0061): a player's `P1344`
  ("participant of") a tournament edition, the edition's `P3450`
  ("sports season of league or competition") linking it back to the
  competition series, and the edition's `P1346` ("winner") matched against
  the target country (via `P1532`, "country for sport," on the winner's
  national-team item — the same property REQ-114/ADR-0035 already
  established) or club. `IWikidataClient` gained four new intersection
  query methods for this (`QueryTeamTrophyCountryIntersectionAsync`,
  `QueryTeamTrophyNationalTeamIntersectionAsync`,
  `QueryTeamTrophyClubIntersectionAsync`, and
  `QueryTrophyNationalTeamIntersectionAsync` for the individual-award path);
  `WikidataLookupService.LookupAndPersistTrophyCountryAsync`/
  `LookupAndPersistTrophyClubAsync` dispatch on `TrophyDefinition
  .IsTeamTrophy` (and, for Country, also on `CountryDefinition
  .UsesCountryForSportProperty` — this also resolves ADR-0035's own
  outstanding follow-up note, see that ADR). `GridGameModule` treats Trophy
  as a third category type throughout generation, guess-scoring, and
  REQ-211's guess-time live-lookup fallback (Trophy × Trophy still has no
  dedicated live-lookup method — see REQ-107's own status note; it remains
  structurally infeasible, not merely unhandled).
  **Three caveats, all load-bearing for what actually ships:**
  (1) **No longer structurally dormant in production** —
  `ReferenceDataSeeder` now seeds three trophies, and `trophyCount(3)`
  clears `Size` for the default `GridSize = 3` (`GridGameModule
  .SelectPairing`), so Country × Trophy and Club × Trophy are REACHABLE and
  selectable in production for the first time, not just mechanically wired
  up (Trophy × Trophy still needs `trophyCount >= Size × 2 = 6` and stays
  infeasible). (2) **Ballon d'Or's QID (`Q166177`) was not independently
  verified against a live Wikidata page this session** — this sandbox
  cannot reach wikidata.org (same limitation `ReferenceDataSeeder`'s own doc
  comment already documents for S-036/S-037's guessed club QIDs, 4 of which
  turned out wrong) — a human must check it against the live page before
  this is relied on in a real deployment. (3) **The two new QIDs (World Cup
  `Q19317`, Champions League `Q18756`) are likewise training-knowledge
  guesses, not independently verified this session** — same caveat, same
  required human check, before real reliance; see ADR-0061's own
  "Consequences" section for what happens if `P3450` turns out not to be
  the property actually used to link editions to series for either
  competition (the query simply returns no matches, absorbed by REQ-101's
  retry logic, not an error).
  **Status note (2026-08-29, ADR-0089) on caveat (1) above:** `GridGameModule
  .SelectPairing` and its `trophyCount >= Size` / `trophyCount >= Size × 2`
  feasibility thresholds no longer exist — see REQ-107's own 2026-08-29
  status note for the mechanism that replaced them. Caveat (1)'s
  qualitative conclusion still holds and is, if anything, strengthened:
  Trophy headers are reachable in production and now also appear
  proportionally to the trophy pool's actual size (3 trophies today) rather
  than only becoming reachable once a hardcoded threshold was crossed, with
  no equivalent of the old Trophy×Trophy `Size × 2` ceiling blocking that
  pairing outright.
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
  triggered manually via `warm-grid-cache.yml` — **not** an HTTP
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
  signal yet), not a correctness bug. **Superseded by the "Extended
  (2026-07-28) — persisted confirmed-low signal" criterion below**: this
  is no longer accepted as a permanent gap, only as the state prior to
  that criterion being implemented. Left here, marked superseded rather
  than deleted, so the run of REQ-110's history stays legible.
- **Extended (2026-07-28) — technical-failure visibility in the run
  summary.** Three consecutive `warm-grid-cache.yml` runs
  (2026-07-26/27) produced byte-identical summaries ("2064 pairs checked,
  1214 queried live, 850 already valid") with zero net cache expansion.
  Most of that is the accepted "below-threshold, re-queried every run" gap
  above and is not changing. But `WikidataClient`'s sync-path intersection
  queries (used only by this cache-warming path, `throwOnTimeout: false`)
  silently swallow real technical failures — WDQS timeouts, HTTP errors,
  and JSON parse errors all return an empty match list, logged only as a
  per-pair warning — so a pair that is "confirmed genuinely below
  `MinValidAnswers`" and a pair the run simply failed to get a clean
  answer for are recorded identically in `CacheWarmingResult`. One run
  alone (2026-07-27 19:29) had 133 such swallowed failures out of 1214
  live queries (11%), invisible in the summary. Given a cache-warming run
  where at least one live-queried pair's Wikidata lookup ends in a
  technical failure (timeout, HTTP error, or parse error) rather than a
  successful response (with or without matches), when the run completes,
  then the final summary reports a count of how many live-queried pairs
  hit a technical failure, distinct from `PairsQueriedLive`, and lists the
  specific failing pairs (by category-value name or QID pair) so an
  operator can tell "genuinely below `MinValidAnswers`" apart from "failed
  to get a clean answer, worth re-running." This changes only
  `PlayerCacheWarmingService`'s own result/summary and does **not**
  change: (a) the accepted gap above — a below-threshold pair, technical
  failure or not, is still re-queried every run, there is still no
  persisted "checked, genuinely low" signal; (b) `WikidataClient`'s
  fail-open/swallow-and-return-empty contract for any other caller —
  round generation's own REQ-103 path and REQ-211's guess-time fallback
  (ADR-0046) must keep failing open exactly as today, this is
  observability for the cache-warming path only.
- **Extended (2026-07-28) — cache-warming-specific timeout and same-run
  retry.** Follow-up to the technical-failure-visibility extension above:
  of the 133 pairs that showed up as technical failures in the
  2026-07-27 19:29 run, a real portion are recoverable — a WDQS query
  timing out at round-generation's 15s budget (`_queryTimeout`, shared
  today because cache warming calls `WikidataLookupOrigin.Sync`, the same
  origin round generation uses) even though nobody is waiting
  synchronously for a cache-warming run the way REQ-101/103's own player
  is waiting for a grid. ADR-0046 already widened the timeout for a
  different caller in the same query-shape class (28s for REQ-211's
  guess-time fallback, justified by ADR-0011's documented 9-27s worst
  case) — this is the same fix for a second caller, not a new pattern.
  Given a live Wikidata query issued by the cache-warming path (REQ-110),
  when that query would otherwise time out at round-generation's 15s
  budget, then it uses a longer, cache-warming-specific timeout instead of
  that 15s budget. And when a pair's live lookup hits a technical failure
  (timeout, HTTP error, or parse error), it is retried at least once
  within the same cache-warming run before being counted as a technical
  failure in the run summary — a transient WDQS 502 or a momentary
  timeout may well succeed on a same-run retry a few seconds later. This
  is explicitly a **third, cache-warming-only timeout tier**: it must not
  change round generation's own 15s budget (REQ-103) or the guess-time
  fallback's 28s (ADR-0046) — those two remain exactly as documented. The
  specific timeout value and retry mechanics (backoff, count) are
  implementation details for `backend-implementer` to pick and justify,
  not specified here.
- **Extended (2026-07-28) — persisted confirmed-low signal.** Direct
  follow-up to the same diagnosis: the bulk of stuck pairs (1207 of 1214
  live-queried pairs in one measured run) are not failures at all — they
  are pairs Wikidata answered successfully, genuinely below
  `MinValidAnswers`, re-queried on every single run for zero possible
  benefit because nothing distinguishes "confirmed checked, genuinely
  low, as of this reference-data/query-shape state" from "never checked."
  This was an accepted gap when cache warming ran occasionally; it now
  runs roughly daily and burns real CI minutes re-querying the same
  ~1200 confirmed-low pairs every time. Given a pair queried live by the
  cache-warming path that returns a real (possibly zero-match) answer
  below `MinValidAnswers`, when that happens, then the system persists
  enough information to recognize, on a future cache-warming run, that
  this specific pair was already checked against the current
  reference-data/query-shape state and confirmed low — so it is not
  re-queried again unless the reference data or query shape has changed
  since. This directly supersedes this REQ's own earlier "known, accepted
  gap" criterion above (marked superseded there, not deleted). It must
  preserve every existing recovery-ordering rule that currently relies on
  "warming after a data/query-shape change re-checks everything from
  scratch": REQ-111's stale-QID cleanup (both the named and `--all-clubs`
  modes), the 2026-07-17 truthy-`wdt:P54` incident's "clean before warm"
  ordering (NOTES.md), and REQ-112/S-038's `purge-player-pool` flow.
  Whatever mechanism persists the confirmed-low signal must be something
  those existing purge/clean tools already touch, or must be extended to
  touch, when they run — a "purge and re-warm" cycle must still mean a
  real, full re-check of every affected pair, never a warm run that
  trusts confirmed-low markers left over from before the purge. The exact
  persistence mechanism (new table, new column, reuse of an existing
  one) is an implementation detail for `backend-implementer`, not
  specified here — but this invariant is not.
- **Extended (2026-08-01) — same-run retry removed; persistent
  cross-run technical-failure tracking added (ADR-0052).** Diagnosed from
  CI logs (`warm-grid-cache` run #15, 2026-07-28 through 08-01): every
  attempt to run the job after the 2026-07-28 same-run-retry extension
  above got cancelled at the workflow's 90-minute ceiling, never once
  completing. Root cause was two-fold: (1) the same-run retry itself made
  every technical failure cost up to 2x the cache-warming timeout instead
  of 1x, and (2) a technical failure was never persisted anywhere, so the
  exact same pairs got retried, at that now-doubled cost, from scratch, on
  every single run. A specific, confirmed cause of many of those failures
  was also found and fixed alongside this: `WikidataClient.BuildClubClubIntersectionQuery`'s
  plain join on two independent P54 statement-path patterns could produce a
  combinatorial row explosion (one real case returned 250,000+ WDQS
  binding rows) for two clubs with a large, historically-overlapping
  squad — no timeout, however long, reliably finishes that query, so
  retrying it (same-run or cross-run) was pure waste. This criterion
  **supersedes the "same-run retry" half** of the 2026-07-28
  "cache-warming-specific timeout and same-run retry" criterion above
  (marked superseded there, not deleted; the cache-warming-specific
  timeout ITSELF is unaffected and stays). Given a pair's live lookup ends
  in a technical failure (timeout, HTTP error, or parse error), when that
  happens, then it is attempted exactly once this run (no same-run retry)
  and the system persists that this run failed for this pair. Given a pair
  has technical failures persisted for at least 2 consecutive runs, when a
  later cache-warming run reaches that pair, then it is skipped without
  issuing any live query, and counted separately in the run summary from
  both `PairsQueriedLive`'s technical-failure subset and
  `PairsSkippedConfirmedLow`. Given a pair with a persisted failure record
  gets a real (possibly zero-match) answer on some later run, when that
  happens, then the persisted failure record is cleared, so a pair that
  recovers (a query-shape fix, a resolved WDQS outage) is not permanently
  starved. This must preserve REQ-110's own "persisted confirmed-low
  signal" invariant above: the same purge/clean tools (REQ-111's
  `clean-stale-club-attributes`, REQ-112/S-038's `purge-player-pool`) that
  already clear a stale confirmed-low marker must also clear a stale
  persistent-failure marker, for the same "purge and re-warm forces a
  real, full re-check" reason. The exact threshold (how many consecutive
  run failures before skipping) is an implementation detail for
  `backend-implementer` to pick and justify, not specified here.
- **Status note (2026-08-01, live-incident follow-up to ADR-0052).** The
  first real `warm-grid-cache.yml` runs under the extension above
  produced exactly the intended effect — 125 Club x Club pairs correctly
  identified as structural, persistent technical failures (a combinatorial
  WDQS row-explosion query shape) and stopped from being retried — but
  exposed a missing recovery path: there was no tool to clear a
  `PairLookupFailure` marker without also being `clean-stale-club-attributes`'s
  much broader club-name scope (every pair touching a named club on either
  side). Since the 125 stuck pairs collectively touched all 32 seeded
  clubs, using that tool to clear them would have wiped roughly 850 other
  pairs' worth of perfectly good cached `PlayerAttribute`/`PlayerData`
  data along with them. Added `PairLookupFailureCleaner`
  (`XGArcade.Data.Seeding`) and its `clear-pair-lookup-failures` CLI verb —
  pair-scoped, not club-name-scoped: it reads `PairLookupFailure` directly
  for every row at or above `PersistentFailureThreshold` and removes only
  those rows, touching no other table. This is a narrower sibling to
  REQ-111's `clean-stale-club-attributes`/`purge-player-pool`, not a
  replacement for either — the "purge and re-warm forces a real, full
  re-check" invariant those two already satisfy for a QID/query-shape
  correction is unaffected; this tool instead exists for the case where
  the failure marker itself is the only thing that needs clearing.
- **Status note (2026-08-02, ADR-0055): a second, independent proactive
  mechanism, `PlayerCareerPrefetchService` (`dotnet run --
  prefetch-player-careers`), also serves this REQ's intent.** Unlike
  `PlayerCacheWarmingService` above (which fills `PlayerAttribute`/
  `PlayerData` for existing Country×Club/Club×Club pairs), this sweeps
  every seeded `CountryDefinition` row's full eligible player pool
  directly (`IWikidataClient.QueryPlayerPoolByNationalityAsync`) and
  writes `PlayerCareerStint` rows for players xG Grid's own query history
  has never touched — the real fix for xG Path's target-pool bottleneck
  (REQ-1201). Its own workflow, `prefetch-player-careers.yml`, is
  `workflow_dispatch`-only (not on the weekly cron the rest of this REQ's
  jobs moved to) — see ADR-0055's Consequences section for why.
- **Status note (2026-08-17, ADR-0069): `PlayerCareerPrefetchService`
  widened to also sweep every seeded `ClubDefinition` row's full eligible
  player pool** (`IWikidataClient.QueryPlayerPoolByClubAsync`, P54's full
  statement path, not the truthy `wdt:P54` shortcut — see ADR-0069 for
  why that distinction is load-bearing here), in addition to the
  country sweep the note above describes — both sweeps run in the same
  `prefetch-player-careers` invocation, not two separate verbs. This
  closes the gap where a player from an unseeded country who played for a
  seeded club was invisible to both `warm-grid-cache`'s pairwise sweep
  and this service's own prior nationality-only sweep. `PlayerCareerPrefetchResult`
  now reports `ClubsProcessed`/`ClubsFailed` alongside the existing
  `CountriesProcessed`/`CountriesFailed`; `PlayersTouched`/`StintsAdded`
  remain combined totals across both sweeps (a player found via either
  sweep is written through the same batch-persist path).
- **Extended (2026-08-18): `PlayerCareerPrefetchService`'s two sweeps now
  also write `PlayerAttribute` rows, not just `Player`/`PlayerCareerStint`.**
  Every player returned by `QueryPlayerPoolByNationalityAsync`/
  `QueryPlayerPoolByClubAsync` satisfies that pool's own nationality/club
  value by construction of the query's own WHERE clause, so persisting
  `PlayerAttribute { AttributeType = "nationality" | "club", AttributeValue =
  <that pool's value> }` for every pooled player needs no separate Wikidata
  read-back. This is what lets `PlayerCacheWarmingService`'s existing
  `CountPlayersWithBothAttributesAsync` pre-check (a pure local SQL join,
  unchanged) become the *complete* answer for a Country×Club pair once both
  sides have been swept by `prefetch-player-careers`, rather than a partial
  one that still falls through to a live pairwise SPARQL intersection query
  — the mechanism that was timing out at a 100% failure rate on large,
  historically-overlapping club combinations (see the 2026-08-01 ADR-0052
  status note above for the same underlying row-explosion problem from the
  other direction). `PlayerCacheWarmingService` itself is unchanged; its
  skip-logic simply starts being correct more often. Deduped per
  country/club against what's already stored (one batched
  `GetPlayerAttributesAsync` call, not one per player), same discipline as
  `WikidataLookupService.PersistMatchesAsync`. `PlayerCareerPrefetchResult`
  gains `AttributesAdded`, a combined total across both sweeps, alongside
  the existing `PlayersTouched`/`StintsAdded`.
- **Extended (2026-08-18) — confirmed-low without a live query for a
  fully-swept pair (ADR-0078).** Direct follow-up to the extension
  immediately above: once `PlayerCareerPrefetchService`'s pool sweep has
  written every `PlayerAttribute` a specific country's or club's *complete*
  Wikidata pool could ever produce, a Country×Club or Club×Club pair where
  **both** sides have been fully swept has a local `PlayerAttribute` count
  (`CountPlayersWithBothAttributesAsync`) that is no longer a partial cache
  hint — it is the true, final count, since nothing a live query could
  return is missing from it. `PlayerCacheWarmingService.WarmAsync` still
  issued a live Wikidata query for exactly this case, which is pure waste
  (and, per the incident that motivated ADR-0077, the query shape most
  likely to be slow or fail). `CountryDefinition` and `ClubDefinition` each
  gain a nullable `PlayerPoolSweptAt` (`DateTime?`) so `WarmAsync` can check
  "this reference value's pool is fully and currently swept" directly
  instead of re-deriving it live.
- Given `PlayerCareerPrefetchService` completes a specific country's or
  club's pool sweep successfully in a run (the existing
  `countriesProcessed++`/`clubsProcessed++` success path)
- When that sweep completes
- Then that `CountryDefinition`/`ClubDefinition` row's `PlayerPoolSweptAt`
  is set to the current UTC time
- And a country/club skipped this run for having no `WikidataQid`, or one
  whose sweep this run ends in a caught `WikidataQueryException`, does
  **not** have its `PlayerPoolSweptAt` set or changed — an incomplete pool
  must never be marked as fully swept
- Given a Country×Club pair (or a Club×Club pair) whose local
  `PlayerAttribute` count is below `MinValidAnswers`, where **both** sides
  of the pair already have a non-null `PlayerPoolSweptAt`
- When `PlayerCacheWarmingService.WarmAsync` reaches that pair
- Then no live Wikidata query is issued for it — the system calls
  `RecordConfirmedLowAsync` directly, using the existing local count as the
  confirmed match count, and the pair is skipped on every subsequent run
  the same way an already-confirmed-low pair is today
- Given a pair below `MinValidAnswers` where only one side, or neither
  side, has a non-null `PlayerPoolSweptAt`
- When `WarmAsync` reaches that pair
- Then the existing live-query fallback behavior is unchanged — partial
  sweep coverage on either side is never treated as "final," only both
  sides swept is
- Given `StaleClubAttributeCleaner` (REQ-111) purges a club's
  `PlayerAttribute`/`PlayerData` rows, in either its named or `--all-clubs`
  mode
- When that cleanup runs
- Then the affected `ClubDefinition` row(s)' `PlayerPoolSweptAt` is reset to
  `null` alongside the data it already clears — a stale "fully swept"
  marker left in place after the underlying data was wiped would
  permanently and wrongly suppress the very re-check that cleanup exists to
  force, the same incident class ADR-0050/ADR-0052 already document once
  in this codebase
- Given the `purge-player-pool` CLI verb (REQ-112/S-038) runs
- When it deletes every `Player` row and its cascading
  `PlayerAttribute`/`PlayerData`/etc.
- Then it also resets `PlayerPoolSweptAt` to `null` on every
  `CountryDefinition` and `ClubDefinition` row — today it deletes `Player`
  data but does not touch either reference table at all, and this extension
  requires that it starts to, for the same full-reset-scope reason
- This does not widen the skip condition to a single swept side: a pair
  where only one side is swept still falls through to a live query, because
  a partial pool on either side leaves the true match count unknown, not
  merely "probably low"
- **Status note (2026-08-25, S-186 — Supabase free-tier egress incident,
  ADR-0088): `PlayerCareerPrefetchService` itself now also checks
  `PlayerPoolSweptAt` before re-sweeping a country/club, not only
  `PlayerCacheWarmingService`.** Root cause of a production Supabase
  free-tier egress overage (6.40GB/5GB, 128%, current billing cycle): a
  burst of 9 manual re-dispatches of `prefetch-player-careers.yml` in ~36
  hours (2026-08-17/18) each unconditionally re-swept every seeded
  country's and club's full player pool from scratch — a live Wikidata
  query per row plus a full `GetPlayerAttributesAsync`/
  `GetCareerStintsByPlayerIdsAsync` dedup read-back against Supabase
  Postgres — with no skip mechanism at all, unlike every sibling bulk
  Wikidata job. Given a country/club whose player pool was already fully
  swept in a prior `prefetch-player-careers` run (`PlayerPoolSweptAt`
  non-null), when the job is re-dispatched, then that country/club is
  skipped entirely — no live Wikidata query and no Supabase read-back —
  and counted in a new `CountriesSkipped`/`ClubsSkipped` total in the run
  summary. Given a country/club whose pool has never been swept
  (`PlayerPoolSweptAt` still null) or whose `PlayerPoolSweptAt` was reset
  to null by REQ-111's cleaner or `purge-player-pool` (REQ-112/S-038),
  when the job reaches it, then it is still queried normally — this
  extension does not change the existing invalidation contract in any
  way, only adds a new reader of the same signal. No staleness window:
  "ever successfully swept" is sufficient, matching this data's own low
  volatility (a Wikidata career history rarely changes retroactively) and
  ADR-0078's own precedent for the sibling `warm-grid-cache` job. See
  ADR-0088 for the full decision, alternatives considered, and how this is
  explicitly distinguished from ADR-0078's own (pairwise, different
  service) skip rule.
- **Status note (2026-08-29, S-187, ADR-0090): rotating, bounded re-sweep of
  already-swept pools added, closing the gap ADR-0088's skip-forever default
  left behind — a player transferring into an already-swept country's/club's
  pool now eventually gets noticed again.** `PrefetchAsync` gains an optional
  `maxEntitiesToResweep` parameter; `null` (unchanged for
  `prefetch-player-careers.yml`'s own `workflow_dispatch` trigger) preserves
  ADR-0088's exact unbounded-skip behavior. A non-null N — passed only by the
  new weekly `resweep-player-careers.yml` cron (Sunday 05:15 UTC, default 2)
  — additionally re-sweeps up to N already-swept entities, chosen as the
  oldest `PlayerPoolSweptAt` values, on top of every never-swept entity
  (still always swept, uncapped, never competing for the N budget).
  `SplitResweepBudget` divides N across the country and club sweeps
  (ceiling half to countries — 49 seeded vs ~15 clubs — so N=2 splits into 1
  country + 1 club). A row selected for resweep is treated identically to a
  never-swept row for the rest of `SweepAsync`. `prefetch-player-careers`'s
  CLI verb now accepts this as an optional second argument, switching from
  S-112's "exact-match, extra tokens silently fall through" shape to a
  "prefix-match, validate and throw" shape for this one verb. The two
  workflows use separate `concurrency` groups (each `${{ github.workflow }}`-scoped)
  and can therefore run concurrently against the same data if a manual
  dispatch of the unbounded job lands during the weekly bounded cron — an
  accepted, low-probability overlap, not a guaranteed mutual exclusion. See
  ADR-0090 for the full decision, its explicit reconciliation with ADR-0088
  (the worst-case cost delta is at most N pool fetches + N dedup read-backs
  per week, bounded and small, versus the unbounded manual path's full
  ~64-entity sweep), and the accepted staleness-window trade-off (freshness
  within roughly a season — ~49 weeks for countries, ~15 for clubs at
  N=2/week — not real-time).
- **Status note (2026-08-29, S-188, ADR-0092): a third, orthogonal
  freshness mechanism — a targeted, date-filtered per-club sweep for
  faster-than-the-rotation checks around a transfer-window deadline.**
  New `IRecentTransferSweepService`/`RecentTransferSweepService` and CLI
  verb `sweep-recent-transfers [lookbackDays]` (default 30) run two new
  server-side date-filtered SPARQL queries per seeded `ClubDefinition`
  (`pq:P580`/`pq:P582` qualifier `FILTER`s) and reconcile any arrival/
  departure through ADR-0091's existing `CareerStintReconciler` — reused,
  not reimplemented. `workflow_dispatch`-only for now, deliberately no
  cron (see ADR-0092 for the full reasoning: an unproven query shape plus
  an inherently event-driven need). This mechanism neither reads nor
  writes `PlayerPoolSweptAt` and does not compete with ADR-0088's
  skip-forever default or ADR-0090's rotation — it is a narrower,
  operator-triggered supplement. **Caveat on the REQ-110 tag itself:**
  this piece's actual write surface is `PlayerCareerStint` only — it
  never writes `PlayerAttribute`, the subject REQ-110 is literally about
  — so tagging it REQ-110 (matching S-186/S-187's own precedent) is an
  accepted, non-blocking stretch, not a clean fit; see ADR-0092's "The
  REQ-110 tag" section for the full reasoning and a suggested follow-up.
  A freshly-transferred player therefore becomes visible in xG Path's
  career timeline sooner than ADR-0090's rotation would surface it, but
  does **not** become a valid xG Grid guess answer any sooner — a
  deliberate, stated scope boundary (ADR-0092's "Grid-vs-Path freshness
  asymmetry" section), not an oversight. **This scope boundary is closed
  as of the status note below (S-189, ADR-0093).**
- **Status note (2026-08-29, S-189, ADR-0093): the Grid-vs-Path freshness
  asymmetry above is closed — `RecentTransferSweepService` now also writes
  `PlayerAttribute`.** Unlike S-188's own REQ-110 tag immediately above
  (an accepted stretch, since that piece wrote only `PlayerCareerStint`,
  never `PlayerAttribute`), this extension is a clean fit for REQ-110's
  actual subject: a genuinely new club arrival now also gets a
  `PlayerAttribute`+`PlayerData` row for `(player, "club", clubName)`,
  making it a valid xG Grid guess answer immediately rather than waiting
  for ADR-0090's rotation. A precise trace (see ADR-0093) found ADR-0092's
  original caution against this overstated: `ConfirmedLowMatchPair` is
  never consulted on any live-correctness path (only by
  `PlayerCacheWarmingService`'s own maintenance heuristic, after the local
  cached count is already checked first), and `PairLookupFailure`, while
  also consulted at guess time by `GridLiveLookupDispatcher`
  (REQ-211's live-lookup fallback), only ever costs latency when stale —
  never correctness (a live-lookup failure always fails closed as
  "unknown," per ADR-0046, consuming no attempt). The write is paired with
  a targeted `IPlayerDataQualityRepository.ClearMatchPairAsync` call that
  clears any now-stale `ConfirmedLowMatchPair`/`PairLookupFailure` row for
  the new club against the arriving player's other existing attributes,
  checking both possible stored orderings. Departures still never write or
  remove a `PlayerAttribute` row — Grid's "ever played for this club"
  answer semantics are unchanged. See ADR-0093 for the full trace and
  reasoning, including the exact `PairLookupFailure`/
  `GridLiveLookupDispatcher` nuance summarized above.

**Test level:** Unit (`PlayerCacheWarmingServiceTests.cs` — every pair
gets checked exactly once per run; an already-valid pair is skipped; a
below-threshold pair not yet confirmed-low is re-queried, not skipped; a
simulated `WikidataClient` technical failure — timeout, HTTP error, or
parse error — on a live-queried pair is counted separately from a
successful zero-match response and the failing pair is listed in the run
result, while REQ-103/REQ-211's own callers are unaffected by the change;
the cache-warming query timeout is distinct from and longer than
round-generation's own 15s budget, verified by a test that would fail if
the two timeouts were collapsed back into one; a failing pair makes
exactly one live call per run, never two (2026-08-01: proves the same-run
retry is actually gone); a pair is not skipped after a single prior run's
failure but is skipped without a live query after 2 consecutive prior
runs' failures, and a pair that recovers after a failure clears its marker
so a later, unrelated failure doesn't inherit the old count; a pair
previously persisted as confirmed-low is skipped on a subsequent run
without issuing a live query, verified by asserting the mocked
`IWikidataLookupService`/`IWikidataClient` receives zero calls for that
pair). Also: a regression test proving that running REQ-111's stale-QID
cleanup (named or `--all-clubs`) or REQ-112/S-038's `purge-player-pool`
against a pair previously marked confirmed-low OR a persistent technical
failure, followed by a cache-warming run, re-queries that pair live rather
than trusting the stale marker. `PairLookupFailureCleanerTests.cs`
(2026-08-01): a pair at `PersistentFailureThreshold` is removed; a pair
above it is removed; a pair below it is left alone; a mix of both only
removes the ones at/above threshold, leaving the rest untouched; an empty
table is a no-op that doesn't throw; running it twice in a row is safe
(the second run removes nothing). `PlayerCareerPrefetchServiceTests.cs`
(ADR-0055/ADR-0069): a seeded country's or seeded club's pool creates
players and persists careers; a country/club with no `WikidataQid` yet is
skipped and never queried; an empty pool for a country/club is not a
failure; one country's (or one club's) pool-query failure still lets the
rest of the sweep — country or club — proceed, then throws at the end;
both sweeps run in the same `PrefetchAsync` call and their player/stint
totals combine. **(ADR-0077 additions):** a country-pool sweep writes a
`nationality` `PlayerAttribute` per pooled player; a club-pool sweep writes
a `club` `PlayerAttribute` per pooled player; a player who already has the
attribute is not duplicated and does not count toward `AttributesAdded`;
the same player appearing in both a country's and a club's pool in one run
correctly counts two distinct new attributes, not one. `WikidataClientTests.cs` covers `QueryPlayerPoolByClubAsync`'s
own query shape (byte-for-byte, including the full `p:P54`/`ps:P54`
statement path, never the truthy `wdt:P54` shortcut) and error contract.
**(ADR-0078 additions):** `PlayerCareerPrefetchServiceTests.cs` — a
country's or club's pool sweep that completes successfully this run sets
that row's `PlayerPoolSweptAt` to the current time; a country/club skipped
for having no `WikidataQid`, or one whose sweep ends in a caught
`WikidataQueryException` this run, leaves `PlayerPoolSweptAt` unchanged.
`PlayerCacheWarmingServiceTests.cs` — a below-`MinValidAnswers` pair with
both sides' `PlayerPoolSweptAt` set calls `RecordConfirmedLowAsync`
directly with the local count and issues zero calls to the mocked
`IWikidataLookupService`/`IWikidataClient`; the same pair with only one
side set (or neither) falls through to the existing live-query path
unchanged. `StaleClubAttributeCleanerTests.cs` — cleaning a club (named or
`--all-clubs` mode) also nulls that club's `PlayerPoolSweptAt`, not just
its `PlayerAttribute`/`PlayerData` rows. A `purge-player-pool` regression
test — purging resets `PlayerPoolSweptAt` to `null` on every
`CountryDefinition`/`ClubDefinition` row, not only deleting `Player` rows.
**(ADR-0088 additions, S-186):** `PlayerCareerPrefetchServiceTests.cs` —
`REQ110_PrefetchAsync_CountryAlreadySwept_SkipsWithoutQueryingWikidataAgain`
and `REQ110_PrefetchAsync_ClubAlreadySwept_SkipsWithoutQueryingWikidataAgain`
assert a row with a non-null `PlayerPoolSweptAt` issues zero calls to the
mocked `IWikidataClient` and is not passed to the dedup repositories;
`REQ110_PrefetchAsync_CountryAlreadySwept_DoesNotReWriteSweptAtTimestamp`
asserts a skipped row's existing `PlayerPoolSweptAt` value is left exactly
as it was, not re-stamped with a fresh timestamp;
`REQ110_PrefetchAsync_CountryWithNullSweptAt_IsNotSkipped_StillQueriesWikidata`
is the negative case, confirming a never-swept row is unaffected by the
new check; `REQ110_PrefetchAsync_CountryReSweptAfterInvalidation_QueriesWikidataAgain`
nulls `PlayerPoolSweptAt` mid-test (simulating REQ-111's cleaner or
`purge-player-pool`) and confirms a second `PrefetchAsync` call queries
Wikidata again for that row, proving the existing invalidation contract
still forces a real re-sweep after this change. **(ADR-0090 additions,
S-187):** `PlayerCareerPrefetchServiceTests.cs` —
`REQ110_S187_PrefetchAsync_MaxEntitiesToResweepNull_BehavesExactlyAsBefore`
proves the `null` default is an exact regression match for pre-S-187
behavior; `REQ110_S187_PrefetchAsync_MaxEntitiesToResweepSet_NeverSweptCountryStillAlwaysIncluded`
proves a never-swept row is always swept regardless of how small the budget
is; `REQ110_S187_PrefetchAsync_MaxEntitiesToResweepSet_OnlyOldestAlreadySweptRowsAreReSwept`
proves only the N oldest-`PlayerPoolSweptAt` already-swept rows are
re-swept, the rest staying skipped; `REQ110_S187_PrefetchAsync_MaxEntitiesToResweepTwo_SplitsOneCountryAndOneClub`
proves `SplitResweepBudget`'s N=2 default splits into 1 country + 1 club.

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
  fresh `warm-grid-cache` run once this filter shipped.

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

**REQ-114 – National teams as distinct footballing entities**
> As a player, I want England, Scotland, Wales, and Northern Ireland to be
> selectable Country categories in their own right, so a grid can test
> "played for England" specifically rather than only the generic "United
> Kingdom" citizenship umbrella that flattens all four together.

- **Status: Implemented (Tier 0, 2026-07-21, REQ-114/ADR-0035), pulled
  forward from Tier 1 per explicit product decision** (`MVP-SCOPE.md`'s own
  "National teams as distinct footballing entities" trigger). None of the
  four home nations are sovereign states, so they can't be queried via
  Wikidata's `P27` ("country of citizenship") the way every other seeded
  country can — English/Scottish/Welsh/Northern Irish players' `P27` is
  uniformly United Kingdom (`Q145`, already seeded). `P1532` ("country for
  sport") is the property that actually means "country represented in
  international competition." `CountryDefinition` gained a
  `UsesCountryForSportProperty` flag (default `false`); `ReferenceDataSeeder`
  seeds England (`Q21`), Scotland (`Q22`), Wales (`Q25`), Northern Ireland
  (`Q26`) as four *additional* `CountryDefinition` rows (never replacing
  United Kingdom) with this flag set `true`. `WikidataClient` gained a
  parallel `QueryNationalTeamClubIntersectionAsync`/
  `BuildNationalTeamClubIntersectionQuery` (`P1532`, truthy — see that
  method's own comment for why the truthy shortcut is safe here, unlike
  `P54`), and `WikidataLookupService.LookupAndPersistAsync` branches on the
  flag to call it instead of the existing `P27`-based
  `QueryCountryClubIntersectionAsync` — the only place this decision is
  made; every caller (`GridGameModule`) is unaware of the distinction.
  Persists under the same `PlayerAttribute.AttributeType = "nationality"`
  vocabulary as every other country — "England" is just another value in
  that vocabulary, same as "United Kingdom" already is, not a new attribute
  type. See ADR-0035 for the rejected alternative (a separate
  `NationalTeamDefinition` table/category type) and why a per-row flag on
  the existing `CountryDefinition` was chosen instead.
  **QID caveat:** all four QIDs (`Q21`/`Q22`/`Q25`/`Q26`) are
  training-knowledge values, **not independently verified against live
  Wikidata pages this session** (same sandbox network-policy block already
  documented for S-036/S-037/S-031's QIDs) — a human must verify them
  before this is relied on in a real deployment; `ReferenceDataSeeder`'s
  idempotent-by-Name upsert will apply any correction on the next seed run.
- Given `CountryDefinition` rows for England, Scotland, Wales, and Northern
  Ireland, each with `UsesCountryForSportProperty = true`, seeded alongside
  (not instead of) the existing United Kingdom row
- When grid generation picks a Country category candidate, or REQ-211's
  guess-time fallback re-resolves one, and that candidate is one of the
  four home nations
- Then the live Wikidata lookup queries `P1532` ("country for sport")
  instead of `P27` ("country of citizenship") for that candidate only —
  every other seeded country's `P27` query path is completely unaffected
- And a home nation pairs with Club/Trophy category candidates exactly like
  any other Country row — no special-casing anywhere in per-header
  category-type selection (ADR-0089, replacing the now-removed
  `SelectPairing`), `CategoryPairingRules`, or grid-generation's
  pairing/validation logic
- And "satisfies this category" for a home-nation value works identically
  to any other country value: a `PlayerAttribute`/override record of type
  `nationality` with that specific value (e.g. "England"), read through the
  exact same `HasEffectiveAttributeAsync` path as "United Kingdom" or any
  other seeded country
- And citizenship (`P27`) and country represented in competition (`P1532`)
  remain two genuinely separate concepts, never merged into one query or
  one code path — a dual national or naturalized player can differ between
  the two, and this system must not conflate them

**Test level:** Unit (`WikidataClientTests.cs` — the national-team query
path sends `P1532`, never `P27`, and shares `P54`'s full-statement-path
club-membership semantics; `WikidataLookupServiceTests.cs` — `LookupAndPersistAsync`
dispatches to the right underlying query based on
`UsesCountryForSportProperty`, and an ordinary country's dispatch is
unaffected; `GridGameModuleTests.cs` — a flagged country pairs with clubs
exactly like any other country, and both grid-generation's cache-miss path
and REQ-211's guess-time fallback thread the flag through to the correct
dispatch; `ReferenceDataSeederTests.cs` — all four home nations seed with
the flag `true`, every other country seeds with it `false`, and United
Kingdom and England coexist as distinct rows)

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
- **Status note (2026-08-03, direct product feedback): persistent
  correct-cell border added.** States 1 and 4's checkmark-plus-points
  structure above is unchanged, but a correct cell now also gets an
  always-visible `--color-accent-green` border (2px), on `.grid-table__cell`
  (the `<td>`, `Grid.tsx`/`Grid.css`) — not gated behind the tap/hover/focus
  disclosure this REQ already governs, and not applied to an incorrect
  (states 2/3) or unattempted cell. Before this, "correct" was signaled only
  by the checkmark glyph and the gold-tinted points text; the border is an
  additional, always-on cue rather than a replacement for either. See
  `docs/design-document.md` SCREEN-01a's matching 2026-08-03 note for the
  token/contrast rationale and why the border is placed on the `<td>` rather
  than the button or photo-layer element.
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
  each per-`GameKey` round-generation cron actually invokes — `generate-round.yml`
  at the time this fix shipped, split as of S-136/ADR-0072 into
  `generate-grid-round.yml`/`generate-path-round.yml` — Tier 0's only
  production-scheduled trigger point) now also closes a round's predecessor before
  deciding whether to generate a successor — see ADR-0022 for why the round
  to close is never `latest` itself. REQ-806's non-Production-only
  `POST /internal/test-data/force-close-round/{roundId}` still exists too,
  unchanged, for manual/E2E use. Trade-off accepted, not fixed: any rounds
  that had already ended-but-never-closed *before* this fix shipped need one
  additional cron cycle each of that round's own `GameKey`-specific workflow
  (`generate-grid-round.yml`/`generate-path-round.yml` as of S-136) to catch up (or can be
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
- **2026-08-18 addendum (S-142, Epic 13) — explicit threshold value, no
  code change:** suggestions are only fetched/shown once the (trimmed)
  guess is at least 2 characters long; below that, no request is made and
  no suggestions are shown. This was already enforced identically in three
  places — `frontend/src/grid/GuessInput.tsx` (`MIN_QUERY_LENGTH = 2`),
  `frontend/src/path/PathGuessInput.tsx` (same constant), and
  `backend/src/XGArcade.Api/Players/PlayerAutocompleteEndpoints.cs`
  (`MinQueryLength = 2`, enforced server-side independent of either
  frontend) — this addendum only records the value here so a future change
  to any one of them is a REQ violation, not just a cross-file
  inconsistency.
- **2026-08-18 addendum (S-151, Epic 13) — DB-touching warm-up call on
  game-screen mount, distinct from app-load `/health`:** mounting
  `GridScreen.tsx` or `PathScreen.tsx` now also fires
  `GET /players/autocomplete/warmup` (bearer-token authenticated,
  `PlayerAutocompleteEndpoints.cs`), which runs the exact same
  `IPlayerNameIndexRepository.SearchByPrefixAsync` path the real
  `GET /players/autocomplete` route uses, against a trivial 1-character
  query that is server-side only and never reachable via the client's own
  `MinQueryLength = 2` contract above; the result is discarded and the
  endpoint returns `204`. This is a second, narrower warm-up alongside
  `App.tsx`'s existing app-load `/health` ping — `/health` only wakes the
  Container App process (`Results.Ok`, no DB access), so it never opened
  the Postgres connection or compiled the EF Core query shape this route
  needs; that cost previously landed on the player's first real keystroke.
  `warmUpAutocomplete` (`frontend/src/lib/rounds.ts`) is fired from a
  dedicated mount effect in both screens, independent of each screen's own
  round-fetch effect: fire-and-forget, never awaited by the caller, never
  surfaces an error, never blocks or affects round render or its
  loading/error state — same best-effort, no-UI-impact contract as
  `/health`'s own failure handling. No change to the 2-character threshold,
  the suggestion-list data path, or REQ-207's leak-prevention contract
  above — this addendum is about connection/query warm-up timing only.
- **2026-07-27 correction — `Nationality` shipped in the autocomplete
  response (found against `PlayerAutocompleteSuggestion`, shipped as part
  of this requirement's own S-032 work):** as shipped, `GET
  /players/autocomplete`'s `PlayerAutocompleteSuggestion` DTO carried
  `PlayerNameIndex.PrimaryNationality` alongside `BirthYear`, both rendered
  in `GuessInput.tsx`'s suggestion caption line. For a nationality-based
  cell (e.g. Country × Club), this told the player which suggestions
  carried the target nationality before they'd guessed anything — a real
  violation of this requirement's "implies nothing about whether it is
  correct" criterion above, not a hypothetical one. This was never a
  deliberate design choice — it just happened to ship that way, the same
  category of gap as REQ-208's 2026-07-26 correction. **Status: fixed
  2026-07-27 (bug-fix bundle, commit f5d10da/f6d06e3).** `Nationality` was
  removed entirely from `PlayerAutocompleteSuggestion`
  (`PlayerAutocompleteEndpoints.cs`) and from the frontend suggestion type
  and rendering (`frontend/src/lib/types.ts`, `GuessInput.tsx`); only
  `BirthYear` remains in the caption. `BirthYear` is not a leak under the
  same rule — xG Grid's categories are Country/Club/Trophy only
  (`CategoryPairingRules`), so no category can ever be birth-year-based,
  and this correction does not change that stays true. See
  `docs/design-document.md` SCREEN-02's matching note for the UI-side
  detail.
- **Status note (2026-08-01, S-091) — second consumer, no requirement
  change:** `PathGuessInput.tsx` (xG Path's guess field, SCREEN-10) now
  also calls `GET /players/autocomplete`, the same way `GuessInput.tsx`
  (xG Grid) has since S-032 — same debounce/limit constants, same
  keyboard-nav and combobox/listbox ARIA pattern, same graceful-failure
  behavior. No endpoint or backend change: `PlayerNameIndex` was already
  queried globally with no `gameKey`/category scoping to extend. This
  requirement's own text was never game-scoped to begin with (its
  Given/When/Then speaks generically of "a player... typing a guess," not
  a specific game's cell), so unlike REQ-303/REQ-720's status notes this
  is not a correction of stale "exactly one game" language — it is only
  noted here for discoverability. The one piece of REQ-207's prose that
  *is* xG-Grid-flavored — "correct for the current cell" in the acceptance
  criteria above — should be read as "the guess currently being made";
  xG Path has no cell/category axis at all (REQ-1204), so that criterion
  applies to it trivially (there is nothing category-shaped to leak in the
  first place). REQ-209's disambiguation UI remains deferred generally, and
  was separately reviewed and rejected for xG Path specifically
  (`docs/backlog.md` S-091: `XGPathGameModule.ScoreSubmissionAsync`
  resolves correctness independent of which same-named candidate a picker
  would let the player choose).

**Test level:** Unit (verify the autocomplete data source is distinct from
the correctness-check data source; verify `PlayerAutocompleteSuggestion`
never carries `Nationality`, added 2026-07-27), Manual (spot-check that
early/sparse grids don't make guessing trivially easy)

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
- **2026-07-26 correction — whole-name-only prefix matching gap (found
  against `PlayerNameIndexRepository.SearchByPrefixAsync`, shipped as part
  of REQ-207's S-032 autocomplete work):** the criteria above govern
  correctness-checking matching (COMP-06) and were always satisfied; this
  bullet corrects a separate gap in how `PlayerNameIndex` (COMP-10) —
  which reuses this REQ's normalization scheme for the name it indexes,
  per ADR-0007 — is matched for autocomplete (REQ-207). As shipped,
  `SearchByPrefixAsync` matches the query only as a prefix of a player's
  entire normalized name (e.g. `"zlatan ibrahimovic"`), so a query typed
  from a surname alone (e.g. "Ibrahimovic") returns no suggestions at all,
  because that string is never a prefix of the full stored name. This was
  never a deliberate design choice — it just happened to ship that way.
  Diacritic-insensitive matching is unaffected by this correction and
  already works correctly (`PlayerNameNormalizer.Normalize`'s NFKD
  decomposition already makes "Ibrahimovic" and "Ibrahimović" normalize
  identically); this bullet is about word-boundary prefix matching only.
  This is autocomplete-matching text (COMP-10) only — it does not change,
  and must not be read as changing, any correctness-checking behavior
  (REQ-203, COMP-06) or REQ-207's own leak-prevention contract (source of
  suggestions, or the rule that suggestion does not imply validity).
  **Status: fixed 2026-07-26.** `PlayerNameIndexRepository.SearchByPrefixAsync`
  now matches both directions: a plain `StartsWith` scan against
  `NormalizedName` (unchanged) unioned with a `StartsWith` scan against a new
  `PlayerNameIndexWord` child table (`PlayerId`, `Word` — one row per
  space-separated word in `NormalizedName`), keyed and cascade-deleted
  against `PlayerNameIndex`. Both scans stay index-backed at
  `PlayerNameIndex`'s bulk-imported scale — no `Contains()`/leading-wildcard
  `LIKE`, per this correction's own performance note. See ADR-0044 for the
  alternatives considered (notably why a per-word table was chosen over a
  `pg_trgm` GIN index) and the migration
  (`20260726120000_AddPlayerNameIndexWord`).
- **2026-07-27 addendum — pre-migration rows never got word rows:** the
  2026-07-26 fix above only populates `PlayerNameIndexWord` on a fresh
  `UpsertManyAsync` call, so `PlayerNameIndex` rows imported *before* that
  migration (`20260726120000_AddPlayerNameIndexWord`) — via the
  `import-player-name-index` run described in REQ-207's status note, which
  predates this fix — had zero word rows, so a surname-only search still
  failed for them (Clarence Seedorf, from the bug report, was almost
  certainly among them; "Seedorf" returned nothing). **Status: fixed
  2026-07-27.** A new `PlayerNameIndexWordBackfiller`
  (`XGArcade.Data.Seeding`, mirrors `PlayerNormalizedFullNameBackfiller`'s
  exact idempotent, no-external-call pattern) is wired into `Program.cs`'s
  `migrate-and-seed` backfill chain, so this gap self-heals on the next
  deploy rather than needing a manual one-off run.
- Given a player's indexed normalized full name is made up of more than
  one space-separated word (e.g. `"zlatan ibrahimovic"`)
- When an autocomplete query is normalized and matched against that
  indexed name
- Then a match is found if the normalized query is a prefix of the whole
  normalized name, exactly as today (e.g. "zlat" still matches "zlatan
  ibrahimovic")
- And a match is *also* found if the normalized query is a prefix of any
  individual word within the normalized name (e.g. "ibrah" matches
  "zlatan ibrahimovic" via its second word) — this is additive to the
  existing whole-name-prefix behavior, not a replacement of it; both
  directions must keep working at once

**Test level:** Unit — comprehensive case coverage (diacritics, aliases,
typos, and confirming near-miss strings that should NOT match are rejected;
per-word prefix matching for `PlayerNameIndex` autocomplete queries,
including a surname-only query and confirming whole-name-prefix queries
still match too)

**REQ-209 – Disambiguating multiple players with a matching name**
> As a player, I want a fair resolution when my guess matches more than one
> real player, so the cell's categories — not luck — decide correctness.

- **Status: Implemented (S-067), 2026-07-21 — pulled forward ahead of
  `MVP-SCOPE.md`'s original Tier 1 trigger (which had never actually
  fired; see that file's own updated note).** All three branches below are
  now built exactly as specified. When more than one candidate satisfies
  both categories, `GridGameModule` (`AcceptMatchAsync`/
  `BuildDisambiguationCandidatesAsync`) returns each fitting candidate's
  name and their *other* known `PlayerAttribute` values (excluding
  whichever of the cell's own two categories every candidate already
  satisfies, since repeating those wouldn't distinguish anything) — not
  birth year, which `Player` has no column for and which REQ-209's own
  text only offered as an illustrative "e.g." example, not a literal
  requirement. `GuessSubmissionService.SubmitGuessAsync` returns this
  disambiguation-needed result *before ever touching the Guess
  repository* — no row persisted, no attempt consumed — satisfying
  REQ-210's "part of the same attempt that triggered it, not a separate
  attempt" clause structurally, not just by convention. `POST
  /rounds/{roundId}/cells/{cellId}/guesses` gained an optional
  `chosenPlayerId` request field; the response's `Candidates` field (null
  on every ordinary scored response) is the frontend's discriminator for
  when to render `GuessInput`'s new SCREEN-02a picker instead of treating
  the response as scored. A `chosenPlayerId` is always re-verified
  server-side against a freshly-recomputed matching set — never trusted
  blindly — and an invalid/stale one fails closed to an ordinary incorrect
  guess (which does consume an attempt, since that's a real scored guess).
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
- **Status note (2026-07-26, S-077, ADR-0041):** "before calling the owning
  `IGameModule` at all" above now needs a narrow clarification. The fixed
  "2" in this requirement's acceptance criteria is no longer a
  `GuessSubmissionService`-local constant (the old `GuessRules
  .MaxAttemptsPerCell`, now deleted) — it's read per-cell through a new
  `IGameModule.GetMaxAttemptsForCellAsync(instanceId, cellId)` method
  (`GridGameModule`'s implementation still returns `2` unconditionally, so
  this requirement's behavior for xG Grid is completely unchanged).
  `GuessSubmissionService` therefore *does* call into `IGameModule` before
  the lock/cap rejection decision — just not `ScoreSubmissionAsync` (the
  name-resolution call this status note's original text is about), which
  still only runs after every check below has passed. The "checked before
  any name resolution work, not after" ordering itself is unaffected;
  only which single constant vs. which per-game method supplies the cap
  value has changed.
- **Status note (2026-07-27, bug-fix bundle, ADR-0046):** a live-lookup
  timeout during REQ-211's guess-time fallback is a fourth "doesn't consume
  an attempt" case, alongside the existing disambiguation one (REQ-209,
  referenced below) — `GuessSubmissionService.SubmitGuessAsync` returns
  `GuessSubmissionOutcome.LiveLookupUnavailable` before ever touching
  `guessRepository`, the same "return before persisting" shape the
  disambiguation branch already uses. See REQ-211's own 2026-07-27 status
  notes and acceptance criterion for the full detail — not repeated here.
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
- **Status note (2026-07-27, bug-fix bundle `claude/xg-grid-perf-search-r0q708`,
  commits f5d10da/f6d06e3 — supersedes, in effect, the "Partially
  implemented" bullet's "there is no name-index pre-filter yet" claim):**
  `GridGameModule.ScoreSubmissionAsync`'s live-lookup trigger now checks
  `IPlayerNameIndexRepository.ExistsByNormalizedNameAsync` before running
  the live Wikidata lookup — exactly the "this live lookup only triggers
  when the name matched a real `PlayerNameIndex` candidate" acceptance
  criterion below, which had been drifting undone since Tier 0 shipped.
  This is **not** a new Tier 1 pull-forward: `PlayerNameIndex` (REQ-207,
  COMP-10) has existed since S-032 (2026-07-17) — the "Tier 1, not built"
  language in the "Partially implemented" bullet above and in ADR-0018 was
  a stale simplification note that never got updated once its own
  prerequisite shipped, and this fix closes that specific gap rather than
  pulling forward anything new (a new ADR superseding ADR-0018 was
  considered but judged unnecessary — closing a documented gap with the
  gap's own already-specified fix is not a new structural decision). Root
  cause: the un-gated live lookup was firing on every unresolved guess,
  including ones matching nothing in `PlayerNameIndex` and therefore never
  a real player — the dominant cost behind the reported "guessing is slow,
  especially for incorrect guesses" symptom. The remaining part of the
  "Partially implemented" bullet above — a single live source (Wikidata
  only, no API-Football fallback/`ExternalApiUsage` budget-gating) — is
  unaffected and still accurate.
- **Status note (2026-07-27, same bundle) — timeout now distinguished from
  "no match" for this guess-time fallback (ADR-0046):** `WikidataClient`'s
  intersection-query methods previously swallowed their own 15-second
  timeout to an empty result, indistinguishable from "Wikidata answered,
  found nothing" — correct for REQ-103's grid-generation use of the same
  client, but wrong here: a timeout during a genuinely correct guess got
  persisted as a confirmed incorrect answer, consuming one of REQ-210's two
  attempts (the reported symptom: guessing "Clarence Seedorf" for Ajax ×
  AC Milan failed once with a fetch error, and the retry was scored
  incorrect). Fixed by adding an opt-in `throwOnTimeout` parameter to
  `IWikidataClient`'s five intersection-query methods, set only for
  `WikidataLookupOrigin.GuessTimeFallback` (REQ-103's own grid-generation
  call path is completely unaffected — default `false`, unchanged
  behavior). On timeout, `WikidataClient` now throws
  `WikidataQueryException`; `GridGameModule.RefreshCellFromLiveLookupAsync`
  catches it and throws the new `XGArcade.Core.Games
  .LiveLookupUnavailableException` (kept in `Core.Games` so `Core` never
  references a `DataSync`-specific exception type, per ADR-0003);
  `GuessSubmissionService.SubmitGuessAsync` catches that and returns the
  new `GuessSubmissionOutcome.LiveLookupUnavailable`, which
  `GuessEndpoints` maps to HTTP 503 — see the new acceptance-criterion
  bullet below, REQ-210's matching status note, and ADR-0046 for the full
  structural decision (including alternatives considered).
- **Status note (2026-07-27, follow-up to the above — ADR-0046's own status
  note has the full reasoning):** merging the two status notes above
  surfaced a real, reported case (the same "Clarence Seedorf" guess) where
  the guess-time fallback consistently returned `LiveLookupUnavailable`
  rather than ever resolving — `BuildClubClubIntersectionQuery`'s two full
  `P54` statement-path joins are exactly the query shape ADR-0011's own
  evidence says can take up to 27 seconds under WDQS load, and REQ-103's
  15-second budget (reused unmodified by the first status note above)
  doesn't cover that. `WikidataClient` now uses a second, wider budget
  (`guessTimeFallbackQueryTimeout`, 28s) whenever `throwOnTimeout` is set —
  i.e. only for this guess-time fallback — while REQ-103/grid generation's
  15-second budget is completely untouched. This does not reopen the
  "increase the timeout instead of distinguishing timeout from no-match"
  alternative ADR-0046 already rejected: that alternative was about
  widening the timeout *instead of* the exception-based fix, back when the
  fallback still ran on every unresolved guess; now that this REQ's own
  `PlayerNameIndex` gate (previous status note) means the fallback only
  ever runs for a guess that matched a real, indexed player, a wider budget
  for just that narrower case has none of the downside the rejected
  alternative had.
- **Status note (2026-08-10, ADR-0052 follow-up) — known-doomed pairs now
  fail fast instead of re-paying the full timeout:** a player reported the
  guess-time fallback timing out "quite often." Root cause: a Country×Club
  or Club×Club pair `PlayerCacheWarmingService` had already confirmed, on
  its own independent runs, as a persistent technical failure
  (`PairLookupFailure.ConsecutiveFailureCount >= PersistentFailureThreshold`)
  still paid the full ~28s guess-time timeout on every guess against it —
  the guess-time path never consulted that table. `GridGameModule
  .RefreshCellFromLiveLookupAsync` now checks
  `IPlayerStoreRepository.IsPersistentTechnicalFailureAsync` before
  attempting the live call; a known-doomed pair now throws
  `LiveLookupUnavailableException` immediately. This is a latency
  short-circuit only — the pair is still reported genuinely UNKNOWN, not
  "incorrect," and no REQ-210 attempt is consumed either way, same as
  before. Only benefits Country×Club/Club×Club — `PlayerCacheWarmingService`
  doesn't track Trophy pairings, so this check is a guaranteed-false read
  for those. See ADR-0052's matching status note for the full detail.
- **Status note (2026-08-17, S-128, ADR-0070) — this fallback is now
  config-flagged, not unconditional:** new `GridLiveLookupOptions.Enabled`
  (default `true`, config key `GridLiveLookup:Enabled`/env var
  `GridLiveLookup__Enabled`) gates `GridGameModule.ScoreSubmissionAsync`'s
  entire fallback — when `false`, an unresolved guess returns immediately,
  never calling `IPlayerNameIndexRepository.ExistsByNormalizedNameAsync` or
  `IGridLiveLookupDispatcher.TryRefreshCellAsync`, and fails closed exactly
  as it would have before this requirement existed at all (same
  `ScoreResult` shape, no new outcome/HTTP status). This is a deliberate,
  reversible operational toggle, not a removal or a supersession of this
  requirement — the fallback still exists in full and remains the default.
  The product owner wants to empirically validate whether S-127's
  proactively-built cache is complete enough on its own, with REQ-509/510's
  admin player-suggestion approve/commit flow as the remediation path for
  any genuine gap surfaced while testing with the flag off, and an instant
  way back to `Enabled = true` if correct guesses start being wrongly
  rejected again. REQ-103's grid-generation-time live lookup
  (`GridGenerationService.GetMatchCountAsync`) is a separate call path and
  deliberately untouched by this flag. See ADR-0070 for the full decision
  and alternatives considered.
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
- **(Added 2026-07-27, ADR-0046)** Given the live lookup above is triggered
  (a `PlayerNameIndex` match with no existing `PlayerAttribute`/
  `PlayerOverride` record for the cell's category types)
- When the Wikidata query does not complete within its timeout
- Then the guess's correctness is treated as genuinely unknown, not
  incorrect — no `Guess` row is written and none of REQ-210's two attempts
  is consumed — and the API returns HTTP 503
  (`GuessSubmissionOutcome.LiveLookupUnavailable`) so the client can retry
  the same guess without penalty

**Test level:** Unit (all branches: no `PlayerNameIndex` match → incorrect,
no live call; match with existing attribute data → no live call needed;
match with no attribute data and budget available → live call + persist;
match with no attribute data and budget exhausted → fails closed; live
lookup timeout → `LiveLookupUnavailable`, no attempt consumed, added
2026-07-27; `GridLiveLookupOptions.Enabled = false` → neither
`PlayerNameIndex` nor the live-lookup dispatcher is ever called, fails
closed same as pre-REQ-211, added 2026-08-17 S-128/ADR-0070), API

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

- **Status: Implemented (Tier 0, S-041, 2026-07-14 — grid-screen
  reachability and the original six content points below; leaderboard-screen
  reachability and three additional ranking/fairness content points added
  2026-07-21, `docs/backlog.md` S-068).** Replaces the
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
- **Reachability + content extended for the leaderboard (2026-07-21,
  `docs/backlog.md` S-068 — built).** Raised because this
  explainer's content predates two later changes that are now genuinely
  player-visible on the leaderboard screen (SCREEN-03) but explained
  nowhere a player actually reads it: REQ-409's median/participation-gate
  ranking (decided/built 2026-07-20, after this REQ's own 2026-07-14
  content update) and S-056's fairness fix (never-played members excluded
  from ranking, REQ-404's 2026-07-20 note; an untouched cell in the live
  scope counting at max, REQ-406/407's 2026-07-20 note). Two decisions:
  - **Reachability: the leaderboard screen reuses this exact same
    `ScoringExplainer` component, opened from a second, equivalent `(ⓘ)`
    entry point in SCREEN-03's header — not a separate leaderboard-specific
    explainer component.** Rationale: both explainers exist to state the
    same "xG Arcade is scored like golf, lower is better" framing plus
    whatever ranking mechanic is currently in view; a second component
    would inevitably drift from the first over time (exactly the kind of
    divergent-copy problem REQ-204/REQ-213 already replaced once, per this
    REQ's own opening status note). Confirmed against the actual component
    (`frontend/src/grid/ScoringExplainer.tsx`) rather than assumed: it
    takes a single `onClose` prop, holds no round/grid state, and reads no
    context from `GridScreen.tsx` — it already renders correctly with no
    active round, no grid, and no cell data available, so **no new prop is
    required** to open it from the leaderboard screen. Its content is not
    conditioned on which entry point opened it — the same full explainer
    (original six content points plus the three below) renders identically
    from either screen, so a player who opens it from the grid screen also
    sees the ranking content, and vice versa; this is a deliberate choice,
    not an oversight, for the same reason there is one component: one
    explanation of "how the whole thing works," not two partial ones keyed
    to whichever screen happened to open it.
  - **Content: three additional required content points, alongside the
    six the explainer already requires** (see the expanded content list
    below) — REQ-409's median/participation gate, REQ-404/406/407's
    never-played and live-scope fairness rules (both S-056), and an
    explicit restatement that the existing golf framing (lower is better)
    is unchanged by the switch to a median. These are stated here as
    cross-references to REQ-409/404/406/407's own acceptance criteria and
    formulas, which remain the sole source of truth for the actual ranking
    logic — this REQ only requires that the explainer's *text* mentions
    them, not that it restates their formulas.
- **Built as (`docs/backlog.md` S-068, 2026-07-21):** both decisions above
  landed exactly as specified, confirmed against the merged diff, not just
  the plan. `LeaderboardScreen.tsx` gained a second `(ⓘ)` entry point
  (`leaderboard-screen__info-toggle`, next to the "Global leaderboard"
  heading) that opens the same `ScoringExplainer` component
  `GridScreen.tsx` already used, importing it directly from
  `frontend/src/grid/ScoringExplainer.tsx` — no new component, no new
  props. Its open state (`explainerOpen`) is tracked independently of
  `scope`/each scope's own load state, so opening or closing it never
  discards a selected scope tab or a loaded "Load more" page, mirroring
  `GridScreen.tsx`'s existing `explainerOpen`/`activeCell` independence.
  `ScoringExplainer.tsx` itself gained the three content paragraphs listed
  above (median ranking + "lower is better" still applies; the ≥5-round
  gate; never-played exclusion plus the live-scope untouched-cell rule),
  rendered identically regardless of which screen's entry point opened it.
  8 new tests across `LeaderboardScreen.test.tsx` and `GridScreen.test.tsx`
  (288 total frontend tests); `quality-architect` passed the diff with one
  trivial comment fix, no design/architecture changes required.
- **Bug fix (2026-07-21, same-day follow-up):** the content growth from six
  to nine paragraphs above (S-068) pushed `ScoringExplainer.tsx`'s card past
  the viewport height on short/mobile screens, and neither
  `.scoring-explainer` nor its `.scoring-explainer-backdrop` had any
  `max-height`/`overflow-y`, so the excess content overflowed off-screen
  with no way to scroll to it or to the close button — reported directly by
  a player as "fills entire screen and it's not possible to scroll so it
  breaks the UI." Fixed in `ScoringExplainer.css` by giving `.scoring-
  explainer` `max-height: calc(100vh - var(--space-4) * 2)` (accounting for
  the backdrop's own `--space-4` padding) and `overflow-y: auto`, so the
  whole card — header and close button included — scrolls as one block
  instead of clipping. The same missing bound was found and fixed the same
  way in `GuessInput.css`'s `.guess-input` card (`max-height: 90vh;
  overflow-y: auto` — that backdrop has no padding of its own, hence the
  plain `vh` bound rather than a `--space-4` subtraction), which hosts the
  SCREEN-02a disambiguation prompt and had the identical gap; no other
  modal/backdrop pattern exists elsewhere in `frontend/src`.
- **Verification finding (2026-08-04), requested by a product owner
  suspecting a gap despite this REQ's "Implemented" status:** driven with a
  live headless-Chromium session against the real dev stack (Postgres +
  dotnet API in local-e2e auth mode + Vite frontend), not just read from
  code or tests. Two results:
  - **Content confirmed complete and accurate.** The rendered
    `ScoringExplainer.tsx` dialog text was captured live and checked
    verbatim against all nine required content points above (the original
    six plus the three 2026-07-21 ranking/fairness points) — every one is
    present. No content gap. This REQ's status remains **Implemented**;
    nothing below reverses that.
  - **A discoverability defect found in the grid-screen entry point,
    contradicting this REQ's own "next to the round/timer indicator"
    acceptance criterion at a specific, common phone-width range.** The
    `(ⓘ)` button (`GridScreen.tsx`'s `.grid-screen__info-toggle`, inside
    `.grid-screen__title-row` alongside the "Current round" heading and the
    REQ-303 `.grid-screen__end-time` "Ends in Xm" text) is a flex child of
    that title row. Measured with Playwright bounding boxes against the
    real running app across 14 viewport widths from 360px to 600px in a
    single stable session (`page.setViewportSize`), then confirmed visually
    with screenshots at 375/420/768px: at widths from **420px to 480px
    inclusive** — a real, common phone-width range covering
    iPhone 12/13/14/15 Pro Max-class devices (~428-430px CSS width) and
    many larger Android phones — the row wraps such that the `(ⓘ)` button
    lands alone on its own third line, disconnected from both the "Current
    round" heading and the "Ends in Xm" text. Since the button carries no
    visible label (only an `aria-label` — an intentional "deliberately
    plain/quiet" design choice per the existing `GridScreen.css` comment,
    not itself a bug), an orphaned button at these widths has no remaining
    visual cue connecting it to the round header at all. At ≤414px
    (iPhone SE/12/13/14 standard width, most Android phones) and at ≥600px
    (tablet/desktop) the button correctly sits adjacent to the "Ends in
    Xm" text as designed. This is a genuine flexbox wrap-order artifact in
    `GridScreen.css`, not a screenshot fluke or a testing artifact
    (jsdom-based unit tests do not perform real CSS layout and would not
    have caught this). **Filed here as a refinement acceptance criterion
    (below) for a follow-up story — not fixed in this pass**, per this
    exercise's own scope (verification, not implementation).
- **Status note (2026-08-08) — second consumer, distinct content, no
  requirement change.** A player directly reported "no scoring information
  in the game" for xG Path (SCREEN-10) — clarified on follow-up to mean
  this REQ's `(ⓘ)` "How scoring works" explainer pattern specifically, not
  the per-puzzle point value REQ-1206 added earlier the same day (that
  stays as-is). `PathScreen.tsx` had no `(ⓘ)` button or explainer of any
  kind before this. Unlike REQ-303's second-consumer precedent (SCREEN-10
  reusing the grid's *exact same* end-time formatter/component, since that
  content is genuinely identical for both games) and unlike this REQ's own
  2026-07-21 leaderboard extension (SCREEN-03 reusing `ScoringExplainer`
  verbatim, since its content is also identical regardless of entry
  point), xG Path's actual scoring rules share almost nothing with xG
  Grid's: no uniqueness concept at all (`FinalUniquenessScore` is always
  null for this game, REQ-1206), no live/locked distinction (a locked xG
  Path score is final immediately, never a provisional value that changes
  before round close, unlike a live grid cell), a different fixed
  attempt-cap/clue model (7 clues per puzzle: 3 club-reveal turns, then
  one bundled year-range turn, then position/nationality/age, one clue
  revealed per wrong guess — REQ-1203/1205), and no player-pool or
  leaderboard-ranking content belongs here either. Reusing
  `ScoringExplainer.tsx` verbatim would therefore misdescribe xG Path's
  actual rules (stating a live/locked distinction and a uniqueness
  mechanic that don't exist for this game), and branching its content on a
  `gameKey` prop was judged worse than a second small component (every
  paragraph wrapped in a per-game branch, with real risk of one game's
  edit bleeding into the other's copy) — so this is built as a **new
  sibling component**, `frontend/src/path/PathScoringExplainer.tsx`, with
  its own content but the same modal/accessibility shell
  (`role="dialog"`, `aria-modal="true"`, Escape-to-close, focus moves to
  the close button on open and returns to the `(ⓘ)` trigger on close) —
  see that component's own doc comment for the full reasoning. Opened via
  a new `(ⓘ)` button (`path-screen__info-toggle`) in `PathScreen.tsx`'s
  header, same visual position as `GridScreen.tsx`'s entry point (inside
  `.path-screen__title-row`, next to the REQ-303 round end-time
  indicator). Content, verified against the actual implementation (not
  assumed from this REQ's Grid-oriented text): each round has a handful of
  puzzles (`PathGenerationOptions.PuzzleCount`, default 4); a fixed 7-turn
  clue sequence and 7-attempt cap
  (`PathClueSequenceBuilder.TotalTurns`/`XGPathGameModule.
  MaxAttemptsPerPuzzle`, both 7, mirrored by the existing frontend constant
  `MAX_CLUES_PER_PUZZLE` in `frontend/src/lib/pathRules.ts` — reused here
  rather than a second frontend constant, since the two backend values are
  identical by design and this codebase already treats them as one shared
  frontend value for "Clue N of M" in `PathGuessInput.tsx`); a puzzle
  locking unsolved reveals the answer; scoring is
  `round(cluesUsed / 7 * MaxPointsPerCell)` for a correct guess
  (`ClueEfficiencyScoringStrategy`), stated explicitly as golf-style
  (lower is better) rather than assuming the player already knows that
  convention from xG Grid; an unsolved puzzle scores the worst case,
  `MaxPointsPerCell`; and once a puzzle locks its score is final
  immediately, never live/provisional. `MAX_POINTS_PER_CELL` (`frontend/
  src/lib/scoringRules.ts`) is confirmed genuinely shared, not a
  Grid-only value — it mirrors `ScoringRules.MaxPointsPerCell`
  (`backend/src/XGArcade.Core/Scoring/ScoringRules.cs`), which
  `ClueEfficiencyScoringStrategy` (xG Path) calls directly, the same
  constant `UniquenessScoringStrategy` (xG Grid) uses via
  `PointsFromUniqueScore`. No uniqueness/other-players'-answers language
  appears anywhere in this component's copy — deliberately, since that
  mechanic doesn't exist for this game and stating it would be actively
  wrong. Covered by three new tests in `PathScreen.test.tsx`
  (`describe('REQ-213: scoring explainer', ...)`, mirroring
  `GridScreen.test.tsx`'s own REQ-213 coverage): the dialog opens with
  xG Path's own content and never mentions uniqueness; opening it does not
  discard an in-progress, typed-but-not-yet-submitted guess; Escape closes
  it and returns focus to the `(ⓘ)` trigger.
  - **Gap fixed same-day (2026-08-08, follow-up), no longer open:**
    `LeaderboardScreen.tsx`'s own `(ⓘ)` entry point previously opened xG
    Grid's `ScoringExplainer` verbatim — including its uniqueness/
    live-locked/median-ranking content — even when the leaderboard's xG
    Path tab was the one currently active, which didn't describe xG
    Path's actual rules. Reported directly by a player after the gap
    above was flagged. Fixed by making the entry point's modal
    `gameKey`-aware: `gameKey === XG_GRID_GAME_KEY` opens
    `ScoringExplainer`, `gameKey === XG_PATH_GAME_KEY` opens
    `PathScoringExplainer` — both already had the identical `{ onClose }`
    modal-shell shape, so this is a small conditional in
    `LeaderboardScreen.tsx`'s render, not a new component or prop.
    Imported `PathScoringExplainer` directly from `../path/
    PathScoringExplainer`, the same cross-feature-folder import pattern
    this file already used for `ScoringExplainer` from `../grid/
    ScoringExplainer` — no need to relocate either component to a shared
    folder first. **Judgement call on switching games while the modal is
    open:** unlike a scope change (REQ-213's own 2026-07-21 addition
    established that `explainerOpen` is independent of `scope`, since the
    explainer's content didn't vary by scope), a game switch changes
    *which explainer component is even correct*, so the two states can no
    longer be fully independent. Rather than swapping the open modal's
    content live under the player mid-read, or inventing a new behavior
    for this one case, this follows the same "back out rather than leave
    a stale, now-mismatched view on screen" precedent this file's
    `selectedRound`/`pastDetailState` reset effect already established
    for a game switch (REQ-410/S-087): switching the game tab while the
    explainer is open closes it; re-opening it via `(ⓘ)` shows the newly
    selected game's correct content. Covered by four new tests in
    `LeaderboardScreen.test.tsx` (`describe('game-aware scoring
    explainer', ...)`): Grid tab + `(ⓘ)` opens `ScoringExplainer`
    (content-distinguished, not DOM-distinguished, since both render the
    same `role="dialog"`/`aria-label="How scoring works"` shell); Path tab
    + `(ⓘ)` opens `PathScoringExplainer`; switching games while the modal
    is open closes it and a re-open shows the new game's content;
    switching games while it's closed has no effect. `PathScreen.tsx`,
    `PathScoringExplainer.tsx`, and `ScoringExplainer.tsx` themselves were
    not touched.
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
  - **(2026-07-21 addition)** that the all-time leaderboard ranks players by
    the **median** of their per-round scores, not a running sum — and that
    the existing golf-style framing above ("lower is better") applies to
    that median exactly the same way it applies to any single round's score
    (REQ-409); a player reading "median" next to "lower is better" must not
    be left to wonder whether the direction changed — it hasn't
  - **(2026-07-21 addition)** that a player must have played **at least 5
    qualifying rounds** — closed, with at least one guess in that round —
    before they appear on the all-time ranked list at all, stated plainly
    enough that a player with fewer qualifying rounds reads their own
    absence from the list as expected, not as a bug (REQ-409)
  - **(2026-07-21 addition)** that a league member who has never submitted
    a single guess does not appear on the ranked list at all (never ranked
    first with a default of zero, REQ-404), and that, in the Current Round
    (live) scope specifically, once a player has made at least one guess
    anywhere in that round's grid, every other cell in that grid they
    haven't touched at all counts at the maximum score — the same value a
    cell locks at once the round closes without a correct guess
    (REQ-406/407, S-056)
- And the explainer is reachable from the grid screen at any time an active
  round is shown — not gated behind having attempted any particular cell,
  and not a one-time first-visit-only prompt
- And the explainer's content is general to the scoring/live-update
  mechanic — it never includes cell-specific numbers, since it must remain
  valid regardless of which cells, or how many, the player has attempted
- **(2026-07-21 addition, S-068)** Given the leaderboard screen (SCREEN-03)
  is displayed
- When the player activates the explainer entry point in that screen's
  header
- Then the same explainer defined above opens — identical content and
  component to the grid-screen entry point, not a second, divergent
  explainer — and can be dismissed the same way, returning the player to
  the leaderboard screen without discarding any in-progress state (e.g. a
  scope tab selection or a loaded "Load more" page)
- And this entry point requires no active round, no particular scope tab
  selected, and no ranked data loaded to open — it renders identically
  regardless of which SCREEN-03 scope is currently active or whether that
  scope's data is loading, empty, or errored
- And the grid-screen entry point (above) is unaffected by this addition —
  both entry points open the same component with the same content; neither
  is a subset of the other
- **(2026-08-04 addition, verification finding)** Given the grid screen
  (SCREEN-01) is displayed at a viewport width between 420px and 480px
  inclusive
- When the header (`.grid-screen__title-row`) renders
- Then the `(ⓘ)` explainer entry point remains visually adjacent to the
  round end-time text (e.g. "Ends in Xm") — on the same line as that text,
  not wrapped alone onto its own line with no adjacent heading or timer
  text — matching the same adjacency this REQ already requires at other
  widths
- And this holds across the same width range on the leaderboard screen's
  equivalent entry point (`.leaderboard-screen__info-toggle`, next to the
  "Global leaderboard" heading), since both entry points share the same
  requirement that the button stay adjacent to its labeling context,
  regardless of screen
- **(2026-08-08 addition, second consumer)** Given the xG Path puzzle screen
  (SCREEN-10) is displayed with an active round
- When the player activates the explainer entry point in that screen's
  header, next to the round end-time indicator
- Then a **distinct** explainer opens — `PathScoringExplainer.tsx`, not the
  grid/leaderboard `ScoringExplainer.tsx` — describing xG Path's own rules
  (the fixed 7-clue/7-attempt sequence and its order; that a wrong guess
  reveals the next clue and a correct one halts the sequence immediately;
  that an attempt-cap-exhausted puzzle locks unsolved and reveals the
  answer; the clue-efficiency scoring formula stated in golf terms, lower
  is better, explicitly rather than assuming the player already knows this
  from xG Grid; that an unsolved puzzle scores the same worst case as a
  correct guess using every clue; and that a locked score is final
  immediately, never a live/provisional value) — and can be dismissed the
  same way, returning the player to the puzzle screen without discarding
  any in-progress state (e.g. a typed-but-not-yet-submitted guess)
- And this explainer's content never mentions uniqueness or other players'
  answers — that mechanic does not exist for xG Path (REQ-1206's
  `FinalUniquenessScore` is always null for this game) — and never mentions
  a live-then-locked distinction, since an xG Path score is final the
  instant its puzzle locks
- And the grid-screen entry point (above) is unaffected by this addition —
  it continues to open the same `ScoringExplainer.tsx` with the same
  content as before
- **(2026-08-08 addition, same-day follow-up)** The leaderboard-screen entry
  point (above) is, by contrast, directly affected: it now opens whichever
  explainer matches the leaderboard's currently selected game tab —
  `ScoringExplainer.tsx` when xG Grid is selected (unchanged content),
  `PathScoringExplainer.tsx` when xG Path is selected — rather than always
  opening `ScoringExplainer.tsx` regardless of the active tab. If the
  player switches the game tab while the explainer is open, it closes
  (rather than swapping its content live, or leaving the previous game's
  now-mismatched content on screen) — re-opening it via `(ⓘ)` shows the
  newly selected game's explainer

**Test level:** UI (explainer opens from the grid-screen header entry point
and closes without losing in-progress state; contains text covering all six
original content points — presence checks against required concepts, not
exact wording; **(2026-07-21 addition)** explainer also opens from the
leaderboard screen's header entry point regardless of active scope tab, and
its content additionally covers the three ranking/fairness points above;
opening from either entry point renders the same content, verified by
asserting on the same text regardless of which screen triggered it;
**(2026-08-04 addition)** at each width in the 420-480px range, the `(ⓘ)`
entry point's bounding box remains on the same rendered line as its
adjacent heading/timer text on both the grid and leaderboard screens — a
real-layout check (Playwright bounding-box comparison against the running
app), not a jsdom-based unit test, since jsdom does not perform real CSS
flex-wrap layout; **(2026-08-08 addition)** `PathScreen.test.tsx`'s
`describe('REQ-213: scoring explainer', ...)` block covers SCREEN-10's own,
distinct `PathScoringExplainer` entry point: opens with xG Path-specific
content and never mentions uniqueness; does not discard an in-progress,
typed-but-not-yet-submitted guess when opened; closes on Escape and returns
focus to the `(ⓘ)` trigger; **(2026-08-08 addition, same-day follow-up)**
`LeaderboardScreen.test.tsx`'s `describe('game-aware scoring explainer',
...)` block covers the leaderboard entry point's `gameKey` branch: the xG
Grid tab's `(ⓘ)` opens `ScoringExplainer` (content-distinguished from
Path's, not DOM-distinguished, since both share the same dialog shell); the
xG Path tab's `(ⓘ)` opens `PathScoringExplainer`; switching games while the
explainer is open closes it, and re-opening it afterward shows the newly
selected game's content; switching games while it's closed leaves it
closed)

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
  set at the moment a `Player` row is first created (as of a 2026-07-27
  bug-fix bundle's batching fix, that's
  `IPlayerStoreRepository.GetOrCreatePlayersByWikidataQidAsync`, called
  from `WikidataLookupService.PersistMatchesAsync` for the whole match set
  at once — this was a single-player, per-match
  `WikidataLookupService.GetOrCreatePlayerAsync` at the time this addendum
  was written; the method was replaced, not just renamed, by that fix's
  per-cell batching) — a row created by an earlier `warm-grid-cache` run,
  before this requirement's `P18` addition
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

**REQ-215 – Player-submitted answer suggestion for an incorrect or
unresolved guess**
> As a registered (non-guest) player, I want to suggest a player I believe
> genuinely satisfies a cell after my own guess for it was scored
> incorrect or couldn't be verified in time, so a real gap in the data has
> a chance to be fixed for everyone — not just re-scored for me.

**Status: Implemented (submission half — 2026-08-01, S-089; REQ-509/510's
admin review/commit half is also now implemented, S-090, 2026-08-08 — see
those REQs' own status notes).** Backend:
`POST /rounds/{roundId}/cells/{cellId}/suggestions`
(`XGArcade.Api.Suggestions.SuggestionEndpoints`, `[RequireAuthorization]`)
resolves the caller via `ClaimsPrincipal`/`IUserRepository
.GetByAuthProviderUserIdAsync`, returns `401` for no/unmatched token,
`400` if `playerName` is blank, `400` if `clubs` has no non-blank entry
(blank strings trimmed and filtered, not counted), `400` if `nationality`
is blank, and `403` if the resolved user's `IsGuest` is `true` — enforced
server-side regardless of what the client sends, per this REQ's own
"Guest vs. non-guest visibility" clause. On success it persists a new
`PlayerSuggestion` row (`PlayerName`, `AssertedNationality`,
`SubmittingUserId`, `CellId`, `RoundId`, `RowCategoryType`/
`ColCategoryType`, `Status = Pending`, `CreatedAt`) plus one
`PlayerSuggestionClub` child row per asserted club (`XGArcade.Data.Entities`,
migration `20260801120000_AddPlayerSuggestion`), returning `201` with the
created suggestion. Deliberately writes nothing to `PlayerAttribute`,
`PlayerOverride`, or `PlayerNameIndex`, and never touches the triggering
`Guess` row — both this REQ's "queued/pending state only" and "no
retroactive rescoring" clauses. The row/col category types are resolved
authoritatively server-side (never trusted from the request) via a new
`IGameModule.GetCellCategoryTypesAsync(instanceId, cellId)` method,
reached the standard `Round.GameKey → IGameModuleResolver` way
(ADR-0003) — see `architecture-document.md` §5.2's cross-component method
inventory for this contract method (`GetCellCategoryTypesAsync`, REQ-215),
added specifically for this endpoint after an architecture-review fix (the
original commit read `GridCell` directly via `IGridInstanceRepository`
from this Api-layer file, a boundary violation caught same-session and
corrected before
merge). Frontend: `SuggestionEntry.tsx` (`frontend/src/grid/`) renders the
entry point/form and is mounted by `GuessInput.tsx` at exactly the two
trigger points below — a guest sees it present-but-disabled with
registration copy (`SUGGESTION_GUEST_LOCKED_COPY`), a non-guest sees it
enabled, with client-side validation (empty clubs/nationality) before the
API call. Test coverage: backend `SuggestionEndpointTests.cs` (11 NUnit
tests, `REQ215_...` naming — unauthorized/guest-403/not-found/validation/
persisted-pending-with-no-side-effect/category-types-from-the-seeded-cell/
xG-Path-keyed-round-resolves-via-module-resolver) plus
`GridGameModuleTests.cs`/`XGPathGameModuleTests.cs` additions pinning
`GetCellCategoryTypesAsync`'s own behavior (returns the seeded cell's row/
col types; throws `GuessScoringException`
for an unknown cell; xG Path's implementation throws `NotSupportedException`
unconditionally, matching this REQ's frontend never being wired up for
`GameKey = "xg-path"`). Frontend: `SuggestionEntry.test.tsx` (9 tests) plus
`GuessInput.test.tsx` additions (6 `REQ215_...` tests covering the new
outcome-view-instead-of-immediate-close behavior on an incorrect guess,
the `LiveLookupUnavailable` trigger, the guest-disabled entry point, and a
regression guard that a correct result still closes immediately as
before) — 382/382 Vitest tests passing, clean `tsc -b`, clean `oxlint`
(all directly run, not just claimed). **Backend caveat: the `dotnet` SDK
was unavailable in this build environment throughout** — the backend
implementation and its tests were hand-traced against
`GuessEndpoints`/`GuessSubmissionService`/`GridGameModule`/
`XGPathGameModule`'s existing, already-verified patterns rather than
actually built or run; confirm in CI before treating the backend half as
independently verified. **Known, accepted, non-blocking gap:**
`XGPathGameModule.GetCellCategoryTypesAsync`'s `NotSupportedException`
currently falls through to ASP.NET's bare default `500` rather than an
explicit `ProblemDetails` response — unreachable today since nothing
wires this feature up for `GameKey = "xg-path"`, flagged by
architecture-reviewer as worth a deliberate `501`/`409` response if/when
xG Path ever does grow a suggestion entry point, not fixed now.

**Tier framing — resolved 2026-08-01, pulled forward by deliberate product
decision:** this is a new submission/review/commit pipeline end to end —
not a small extension of an already-tiered item the way, say, REQ-211's
timeout handling extended an existing live lookup. Per `MVP-SCOPE.md`'s
own classification criteria this reads as Tier 1/2-sized new work. The
product owner requested this feature directly, by name (not a trigger
firing during normal play), the same basis REQ-108/REQ-214/REQ-402-403/
REQ-717 were each pulled forward on before their own triggers fired — see
`MVP-SCOPE.md`'s Tier 1 section for the matching entry recording this
pull-forward. REQ-215's submission half (S-089) was built the same
session; REQ-509/REQ-510's admin review/commit half was built as S-090
(2026-08-08) — see REQ-509's own status note.

**Scope note:** this is a genuinely new, player-initiated pipeline,
distinct from REQ-501-503's existing admin review of auto-fetched,
unverified sync/lookup data (`PlayerData`/`PlayerOverride`,
`AdminScreen.tsx`) — it introduces a new kind of input (a human assertion
about a specific player, not a Wikidata fetch result) rather than
extending that queue. See REQ-509's own status note for how the two
relate, including the decided-and-recorded ADR-0053 that keeps them as
separate admin views.

**Trigger conditions:**
- Given a submitted guess for a cell is scored incorrect (REQ-203)
- Or given a REQ-211 live lookup for that same guess times out
  (`GuessSubmissionOutcome.LiveLookupUnavailable`)
- Then a suggestion entry point becomes available for that specific
  player name/cell/category-types combination
- And for any other outcome - a correct guess, or a REQ-211 live lookup
  that completes and resolves the guess either way - no suggestion entry
  point is offered; this requirement is scoped to exactly the two
  triggers above, not "any incorrect-feeling result"

**Guest vs. non-guest visibility (advertised, not hidden):**
- Given a logged-in guest account (`IsGuest = true`, REQ-717)
- When one of the trigger conditions above occurs
- Then the suggestion entry point is visibly present but disabled/inert,
  showing copy that explains registering (REQ-717's claim path) is
  required to unlock it - never fully hidden or absent for a guest; the
  point is to advertise the incentive to register, not merely to withhold
  the feature silently
- Given a request to submit a suggestion is made by a guest account,
  regardless of what the client-side UI shows
- Then the backend rejects it - the guest restriction is enforced
  server-side, not only by disabling the entry point in the UI
- Given a logged-in non-guest account
- When one of the trigger conditions above occurs
- Then the suggestion entry point is enabled and opens the suggestion
  form when activated

**Suggestion content and submission:**
- Given the suggestion form for a specific triggering guess (the player
  name is already known from that guess) is open, for a non-guest user
- When the user submits it
- Then submission requires at least one club they assert the player is
  eligible for, and the nationality they assert for the player, and is
  rejected with a clear validation error if either is missing
- And the stored suggestion records the player name, the asserted club(s)
  and nationality, the submitting user's id, the originating cell/
  category types, and a timestamp
- And the suggestion is placed in a queued/pending state - it is never
  automatically written to `PlayerAttribute`, `PlayerOverride`, or
  `PlayerNameIndex` as a result of submission alone

**No retroactive rescoring (decided 2026-08-01 — see section 7 for the
resolved entry):**
- Given a suggestion is submitted following a guess already scored
  incorrect
- Then that guess's own recorded outcome - correctness, REQ-210's attempt
  count, and any points already calculated - is completely unaffected by
  the act of submitting a suggestion; submitting one is a data-correction
  proposal only, never a mechanism for re-scoring the guess that prompted
  it
- **Decided (2026-08-01):** no retroactive rescoring - confirmed by the
  product owner. A later admin-approved suggestion (REQ-509) fixes the
  underlying data for all future guesses only; the guess that prompted it,
  and any identical guess from another player against the same cell during
  the same round, keep their original scored outcome unchanged. This was
  already this requirement's own default (the only option that didn't
  require inventing a new scoring-adjustment mechanism found nowhere else
  in this document) - this decision confirms that default is correct and
  final, not still open.

**Test level:** Unit (trigger scoping - entry point offered only on
incorrect/timeout outcomes, never otherwise; submission validation
requires both fields), API (a guest cannot submit even if the request is
crafted directly - server-side enforcement, not only a disabled UI
control; a submitted suggestion is persisted in a pending state with no
write to `PlayerAttribute`/`PlayerOverride`/`PlayerNameIndex`; the
originating guess's own stored outcome is unchanged after submission),
UI (a guest sees the entry point present-but-disabled with registration
copy; a non-guest sees it enabled and can complete the form)

---

**REQ-216 – Guessed player's photo shown on a locked, final-incorrect cell**
> As a player, I want to see who I actually guessed when a cell locks with
> my final guess still wrong, so I get some feedback about my mistake
> instead of a bare X — even though I never find out who the *correct*
> answer was.

**Status: Implemented (backend 2026-08-03, frontend 2026-08-03).**
`GuessSubmissionService.SubmitGuessAsync` (`XGArcade.Core.Scoring`) now
resolves `IGameModule.ResolveWrongGuessPlayerAsync` exactly once — only on
the submission that locks a cell with its final guess still incorrect,
never for state 2. `GridGameModule`'s implementation
(`XGArcade.Games.XGGrid`) is cache-first (an already-known `Player` row
from resolving some other cell), then ADR-0057's Wikidata-only
`WikidataClient.QueryPlayerPhotoByNameAsync` for the photo only — the
canonical name itself always falls back to `PlayerNameIndex.PrimaryName`
(via a new `IPlayerNameIndexRepository.FindByNormalizedNameAsync`) when
resolvable no other way, since a resolved name never depends on the live
lookup succeeding (only the photo does). Persisted immediately onto two new
nullable `Guess` columns (`MatchedPlayerName`/`MatchedPlayerPhotoUrl`,
migration `AddGuessMatchedPlayerNameAndPhoto`) in the same write as the
locking guess itself — never a second write. `POST
/rounds/{roundId}/cells/{cellId}/guesses` and `GET /rounds/current` both
expose this as `IncorrectGuessMatchedPlayerName`/
`IncorrectGuessMatchedPlayerPhotoUrl`; the round-close read path never
triggers a new live lookup, only reads the persisted columns back — this
is what makes state 4 (round closed, page reload) work. xG Path's
`IGameModule` implementation returns `null` unconditionally (out of scope
per `docs/backlog.md` S-094). The same-day placeholder-avatar amendment
below (whether a null photo renders as nothing or a placeholder graphic)
is a pure frontend rendering decision against these same two nullable
fields — it required no backend change and none was made. **Frontend
half (S-094's remaining half), done same day:** `design-document.md` §2's
"Placeholder avatar" entry was added first, per the amendment's own
flagged note, then `frontend/src/grid/CellState.tsx`'s locked-incorrect
branch (`incorrectMatchedPlayerName`/`incorrectMatchedPlayerPhotoUrl`
props, reusing the existing `CellPhoto` component for the real-photo case
and a new `CellPlaceholderAvatar` for the other two) plus
`frontend/src/grid/Grid.tsx`/`Grid.css`'s `.grid-table__cell--incorrect`
persistent red border, mirroring the correct-cell border's own
`.grid-table__cell`-not-`.grid-cell` placement for the same photo-bleed/
stacking-order reason. `frontend/src/lib/types.ts`'s
`CurrentRoundGuess`/`SubmitGuessResponse` carry the two new camelCase
fields confirmed against the backend records above.

- **Status note (2026-08-03, direct product-owner sign-off this session —
  supersedes, narrowly, `frontend/src/grid/CellState.tsx`'s states-2/3
  comment, "no name is shown at all, not even the raw guess ... showing
  the as-typed text ... was misleading either way"):** that comment
  recorded a deliberate prior decision against ever showing a wrong
  guesser's identity. Asked directly this session, the product owner
  confirmed the opposite is now wanted, but **only for the locked, final
  incorrect outcome** — state 3 (no attempts remaining, round still
  active) and state 4's incorrect branch (round closed, cell's guess was
  wrong) — **never** for state 2 (incorrect, at least one attempt still
  remaining). This was an explicit either/or choice, not a default: an
  in-progress wrong guess still gets no name/photo at all, exactly as
  today, so the player isn't shown "who they guessed" while they might
  still be about to guess someone else. Everything the superseded comment
  said about state 2 is unaffected and remains current — only the
  locked/final case is reversed here. The underlying reason the original
  decision gave (showing the as-typed text is misleading, since it isn't
  a real player's canonical name) is why this REQ does **not** revive
  raw-text display — see below, it only ever shows a canonical name/photo
  for a guess that resolves to a real, identified player, never the
  as-typed string itself.
- **Scope note — this is a genuinely different data problem from REQ-214's
  correct-guess photo:** REQ-214 sources a photo from `Player.PhotoUrl`
  (`PlayerAttribute`/`PlayerOverride`, COMP-06), populated because the
  cell's correctness query (REQ-101/102) already resolved and cached that
  exact player as the cell's answer. A wrong guess has no equivalent
  resolved record by construction — the guess didn't complete the cell.
  The only thing that can confirm a wrong guess string refers to a real,
  identifiable player at all is `PlayerNameIndex` (REQ-207/208, COMP-10,
  ADR-0007) — name-matching only, never correctness data. Per ADR-0007's
  boundary, `PlayerNameIndex` carries no photo of its own (its `PhotoUrl`
  column was deliberately removed, `RemovePlayerNameIndexPhotoUrl`
  migration, 2026-07-18, once autocomplete turned out never to use it —
  **this REQ does not ask for that column back**; whether/how a
  wrong-but-real guess's photo is actually resolved is a separate,
  flagged architecture question below, not assumed here). Consequently:
  a guess string that doesn't match any `PlayerNameIndex` candidate at all
  (a typo, gibberish, a fictional name) has no identity to show and no
  photo to show, full stop — that is an explicit, tested outcome of this
  REQ, not an unhandled edge case.
- **UI template note:** the red border for the locked-incorrect case is
  uncontroversial and blocks on no prior decision. The photo/name display
  itself should follow REQ-214's own already-established constraints
  (no cell-footprint/layout change, no broken-image icon, same component
  family in `CellState.tsx`) rather than re-deriving an equivalent set of
  rules separately — see REQ-214's acceptance criteria for the template.
  **The "graceful silent fallback when no photo is available" clause is
  amended by the 2026-08-03 status note below** — this REQ's own no-photo
  fallback no longer matches REQ-214's (nothing shown); it now shows a
  placeholder avatar. Note this is the **first** time the incorrect branch
  has ever shown a name or photo at all — "the guessed player's name is
  shown" below is new acceptance criteria, not something carried over or
  previously satisfied in a narrower way.
- **Architecture question resolved 2026-08-03, `architecture-reviewer` +
  ADR-0057:** how a wrong-but-real guessed player's photo is resolved,
  given `PlayerNameIndex` itself carries no photo. Decision: reuse
  ADR-0011's `WikidataClient`, but as its own distinct, lower-priority
  trigger, separate from REQ-211 — **Wikidata only, no API-Football
  fallback** (cosmetic display value doesn't justify spending the shared,
  scarce `ExternalApiUsage` budget correctness-critical REQ-211 lookups
  depend on), firing once at cell-lock time only, and **failing silently**
  (render no photo, REQ-214's existing graceful-fallback path) on timeout
  or no-match — never fail-closed-as-incorrect, since there is no
  correctness verdict left to compute for a guess already known to be
  wrong. This still never fires for a guess matching nothing in
  `PlayerNameIndex` at all, per the CLAUDE.md "guess-time live lookups are
  narrow and never deferred" rule. The rejected alternative (only show a
  photo when incidentally already cached, no new lookup) would have made
  the confirmed ask unreliable by construction; see ADR-0057 for the full
  reasoning and the other alternatives considered. This REQ's acceptance
  criteria below are written against this resolved mechanism.
- **Status note (2026-08-03, direct product-owner sign-off via
  AskUserQuestion, same session as this REQ's original draft above —
  amends, not supersedes, the two no-photo branches below):** the two
  "no real photo to show" branches were originally written as a graceful
  fallback to nothing, matching REQ-214's own no-broken-image-icon
  precedent for correct cells. Asked directly, the product owner chose a
  different treatment for **both** branches: a dummy/placeholder avatar
  graphic is now shown in place of "nothing," specifically —
  - a real `PlayerNameIndex` match whose photo isn't resolvable (ADR-0057
    timeout, error, or genuinely no `P18` image) now shows the placeholder
    avatar **alongside the matched player's canonical name** (previously:
    name only, no image element); and
  - a guess matching no `PlayerNameIndex` candidate at all now shows the
    placeholder avatar **with no name** (previously: red border only, no
    name, no image element, unchanged from pre-REQ-216 behavior).

  In both cases the red border is unchanged from the original draft. The
  only branch that shows a real photo remains the one where the guess
  matched a real player and ADR-0057's lookup actually resolved one — that
  branch's wording is untouched. State 2 (incorrect, attempt remaining) is
  also untouched — it still shows no name, no photo, and no placeholder
  avatar under any circumstance, exactly as originally drafted.

  **Asymmetry, recorded plainly rather than resolved:** this creates a
  direct inconsistency with REQ-214's own no-photo fallback for a
  *correct* cell, which shows no image element at all (just a checkmark
  and points value) — REQ-214's fallback is genuinely nothing, while this
  REQ's no-photo fallback is now a placeholder avatar. This is a deliberate
  product choice specific to the incorrect-cell case, asked and confirmed
  directly for this REQ only — it is not derived from, and does not
  revisit, REQ-214's own precedent, and this document is not inventing a
  justification for why the two differ (contrast REQ-214's own
  "the user's own explicit choice, not one this document is inventing a
  justification for" status note, which records the same discipline for
  a different, unrelated choice on that requirement).

  **Flagged, not resolved here:** the placeholder/dummy avatar graphic is
  a new visual element with no corresponding entry in
  `design-document.md` §2's token system. Per CLAUDE.md's "Frontend visual
  consistency" convention, that document needs a token/component added
  for this graphic *before* either no-photo branch below can be
  implemented in code. This document does not own `design-document.md`
  and does not add that entry itself — it is `ui-implementer`'s
  responsibility when it picks up the frontend half of this requirement's
  implementation story.
- Given a cell is incorrect and at least one attempt remains (state 2)
- Then no name and no photo are shown, unchanged from today — only the
  incorrect marker and remaining-attempts text (REQ-210); this REQ does
  not apply to state 2 under any circumstance
- Given a cell locks with its final guess incorrect — state 3 (round
  still active, no attempts remaining) or state 4's incorrect branch
  (round closed)
- And that final guess string matched a real candidate in
  `PlayerNameIndex` (a real, known footballer — just not the one that
  correctly completes this cell)
- And a Wikidata-only live lookup (ADR-0057) for that matched player
  resolves a photo before its own timeout
- Then the cell renders with a red border, and the guessed player's
  canonical name and photo are shown, following REQ-214's own
  no-layout-change/no-broken-image-icon/graceful-fallback constraints
- Given the same locked-incorrect case, and the guess matched a real
  `PlayerNameIndex` candidate, but ADR-0057's Wikidata-only lookup times
  out, errors, or genuinely has no photo for that player
- Then the cell renders with a red border, the guessed player's canonical
  name, and a dummy/placeholder avatar graphic shown in place of the photo
  (2026-08-03 product-owner decision, see status note above) — this is
  still a silent, graceful fallback in the sense that it is never a
  fail-closed/incorrect outcome (there is no correctness verdict left to
  compute here) and never a broken-image icon or visible error state, but
  it is **not** the same fallback shape as REQ-214's no-photo case: REQ-214
  shows no image element at all in its equivalent case, this REQ now shows
  the placeholder avatar — see the asymmetry note above
- Given the same locked-incorrect case, and the guess string matched no
  candidate in `PlayerNameIndex` at all (a typo, gibberish, or a fictional
  name)
- Then the cell renders with a red border and the same dummy/placeholder
  avatar graphic, but no name — nothing resolved to a real player, so none
  is shown — no checkmark/cross icon renders in this branch either,
  consistent with the other two locked-incorrect combinations above (the
  red border is what signals "incorrect" instead), and the points value is
  still shown (2026-08-03 product-owner decision, see status note above);
  this supersedes this REQ's own original wording that this branch was
  "today's existing behavior, unchanged" — it is no longer unchanged from
  pre-REQ-216 behavior, though state 2 (attempt remaining) still is
- And in every case above, the cell's rendered width and height are
  identical regardless of branch — red border alone, red border with a
  placeholder avatar (with or without a name), red border with a real
  photo and name, or a correct cell with or without a photo (REQ-214) —
  none of these may ever change the cell's footprint or push neighboring
  cells

**Test level:** Unit/UI (state 2 is completely unaffected — no name/photo/
placeholder avatar under any circumstance; locked-incorrect + real
`PlayerNameIndex` match + resolvable photo shows red border, name, and the
real photo; locked-incorrect + real match + no resolvable photo shows red
border, name, and the placeholder avatar graphic — never a broken-image
icon, and never REQ-214's own no-image-element fallback; locked-incorrect
+ no `PlayerNameIndex` match at all shows red border and the placeholder
avatar graphic with no name; cell footprint is identical across every
branch above, matching REQ-214's own regression-test approach against the
cell's bounding box, not a visual snapshot alone). Unit/API (ADR-0057's
Wikidata-only lookup: fires exactly once at cell-lock time, never for a
guess with no `PlayerNameIndex` match, never calls the API-Football
client, persists a resolved photo immediately in the same request, and
degrades to the placeholder-avatar branch above — not an
incorrect/fail-closed outcome — on timeout, error, or genuine no-match,
mirroring REQ-211/ADR-0046's own timeout-handling test shape without
reusing its fail-closed assertion).

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
  cron (now daily, `0 6 * * *`; split into `generate-grid-round.yml`/
  `generate-path-round.yml` as of S-136/ADR-0072 — see that ADR for why the
  split is now safe) triggers this via the bearer-token-protected
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
  `RoundSchedulingOptions` singleton), exposed via each per-`GameKey`
  workflow's own `workflow_dispatch` input (`generate-grid-round.yml`/
  `generate-path-round.yml` as of S-136 — each input now affects only its
  own `GameKey`, fixing a prior bug where the shared input applied to both).
  The old requirement that `RoundDuration` and
  the cron cadence be hand-matched against each other is gone: the cron is
  now daily, giving a constant 24h max gap between firings, and
  `RoundGenerationService`'s existing idempotency check makes the daily
  firing a no-op until the current round actually ends — so any
  `RoundDuration >= 24h` (including the 48h default) is safe by
  construction rather than needing hand-verification every time either
  value changes. See ADR-0027 for the full reasoning, including why a
  cron cadence that fires exactly every N days was rejected. **As of
  S-084 (ADR-0051):** this same mechanism — one `RoundSchedulingOptions`
  instance per `GameKey`, resolved via the new
  `IRoundSchedulingOptionsResolver` rather than a single directly-injected
  singleton — now also serves `GameKey = "xg-path"`, with its own
  independently-configured `RoundDuration`; `RoundGenerationServiceTests.cs`
  proves both this REQ's "one round ahead" rule and REQ-302's lifecycle
  rules hold for `"xg-path"` exactly as they do for `"xg-grid"`, and neither
  `GameKey`'s generation touches the other's. See REQ-1202's own status
  note for the xG-Path-specific template-resolution detail. What's
  **still not built**, relative to this requirement's full long-term
  acceptance criteria below: an admin-facing configuration surface — "a
  cron expression configured in the system" still means editing
  `appsettings.json`/an env var (a config change, not a code change, but
  still not an in-app admin control) and, for the cron cadence itself,
  editing `generate-grid-round.yml`/`generate-path-round.yml` (each
  independently, per `GameKey`, as of S-136). That remains Tier 1/2 scope
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
- **Status note (2026-07-25, superseded in part by REQ-720):** the S-029
  bullet immediately above reflected a premise — that xG Arcade would host
  exactly one game, permanently — that the product owner has since
  reversed (more games are planned). REQ-720 deliberately reintroduces a
  "Games" nav entry on that corrected premise; see REQ-720 for what it does
  and why this is a documented supersession, not a silent contradiction of
  the bullet above. The other half of that bullet — the "xG Arcade" title
  routing to this game-selection landing screen — is unchanged and still
  accurate; REQ-720 adds a second, different affordance alongside it rather
  than replacing it.
- **Status note (2026-08-01, S-085):** the S-021 bullet above still holds
  exactly as written — no "list games" endpoint exists or is needed, since
  a second game's key (`GameKey="xg-path"`) is, like the first, a
  client-side constant, not fetched data (`GameSelectScreen.tsx`'s own
  `XG_PATH_GAME_KEY`). Only its "while Tier 0 has exactly one game"
  framing is now a point-in-time description rather than the current
  state — `GameSelectScreen` renders two tiles as of S-085 (SCREEN-09);
  this endpoint's own contract is unchanged either way.
- **(2026-07-21 addition — acceptance criteria only, not yet built.)**
  `docs/design-document.md`'s SCREEN-01 mock has always shown a round
  end-time indicator in the header (`Round #14 ⏱ 1d 4h`, next to the `(ⓘ)`
  scoring explainer entry point REQ-213 opens), and `endTime` has been in
  this endpoint's response since S-010 — but no acceptance criteria ever
  covered whether or how the frontend displays it, and `GridScreen.tsx`
  currently renders no end-time text at all. The bullets below close that
  gap; this endpoint's own response contract (`endTime` already present)
  is unchanged. **Note for whoever implements this:** the mock's `Round
  #14` numbering is a separate, pre-existing gap — no field in
  `CurrentRoundResponse` carries a human-friendly round number today, only
  `roundId` (opaque) — out of scope here and not addressed by the criteria
  below.
- **Status note (2026-08-17, S-135/REQ-304):** the "Note for whoever
  implements this" immediately above is now addressed — see REQ-304 for the
  new human-readable per-`GameKey` `sequenceNumber` field, which
  `CurrentRoundResponse` (and every other round-shaped DTO) now also
  carries alongside `roundId`. This endpoint's own `roundId`/`endTime`
  contract, and every acceptance criterion above and below this note, are
  otherwise unchanged — REQ-304 governs the new field itself, not this one.
  - Given the grid screen (SCREEN-01) is showing an active round with a
    known `endTime`
  - When the round data has loaded
  - Then the header shows an end-time indicator next to the `(ⓘ)` scoring
    explainer entry point (REQ-213), whose visible text is a relative
    duration computed from `endTime` minus the client's local clock at the
    moment the round was fetched, floored (never rounded up) to whole
    units, formatted as:
    - `"Ends in {D}d {H}h"` when 24 hours or more remain (the hour part
      omitted if it floors to 0, e.g. `"Ends in 2d"`)
    - `"Ends in {H}h {M}m"` when between 1 and 24 hours remain (the minute
      part omitted if it floors to 0, e.g. `"Ends in 3h"`)
    - `"Ends in {M}m"` when between 1 minute and 1 hour remain
  - Given `endTime` is less than 60 seconds away or already in the past
    (clock skew, or the round-close job hasn't run yet)
  - When the header renders
  - Then it shows the fixed label `"Ending soon"` instead of a computed
    duration — never a negative, zero, or otherwise nonsensical value
  - Given the end-time indicator is rendered
  - When a screen-reader user, or a mouse/keyboard user hovering or
    focusing it, reaches the indicator
  - Then the exact end date/time in the player's local timezone is also
    exposed via its accessible name (not conveyed by a visual-hover-only
    tooltip alone) — so the absolute time isn't lost to a player who can't
    see, or can't act on, a relative countdown that will read differently
    a few minutes later
  - Given the relative duration text is computed once, at the moment the
    round is fetched
  - When time passes in the same browser session without a page reload or
    a fresh call to this endpoint
  - Then the displayed text is not required to update live — no
    periodic-tick/interval requirement — and next reflects reality only on
    the following fetch of `GET /rounds/current` (e.g. a reload); this is
    a deliberate Tier 0 simplification, not a bug to "fix" later with a
    ticking clock
  - And this indicator conveys its meaning through text alone, never color
    alone — consistent with every other state signal this document already
    requires to be text-paired (REQ-204/REQ-210, and the color-only-never-
    conveys-meaning rule in §6/REQ-716's dark-theme criteria)

**Test level:** API, E2E (`tests/e2e/play-grid.spec.ts`'s REQ-303-tagged
case covers the game-selection step added in S-021); UI (unit tests
covering each duration-format bucket above, the "Ending soon" fallback for
a past/near-past `endTime`, and the accessible-name assertion)

**REQ-304 – Human-readable, per-`GameKey` round sequence number**
> As an admin, I want to identify a round by a small, human-readable number
> instead of its opaque database id, so I can talk about, look up, or refer
> to a specific round without copying a GUID.

**Assignment, uniqueness, and gaplessness:**
- Given a new Round is being created for a specific `GameKey` (REQ-301's
  "one round ahead" generation, or any other round-creation path)
- When the round is persisted
- Then it is assigned a `SequenceNumber` equal to the current maximum
  `SequenceNumber` already assigned to that `GameKey`, plus one (starting
  at 1 for a `GameKey`'s first-ever round), guarded by a unique index on
  `(GameKey, SequenceNumber)` — the read of the current maximum and the
  insert of the new Round row are two separate operations, not one
  transaction, so if two creation attempts for the same `GameKey` were
  ever to race, the losing attempt's insert fails on that constraint
  rather than persisting a duplicate `SequenceNumber`
- Given two rounds created for the same `GameKey`
- When both are queried
- Then their `SequenceNumber` values are always distinct, and consecutive
  when ordered by `SequenceNumber` — no gap can appear as a normal
  consequence of round creation

**Independence per `GameKey`:**
- Given two rounds created for two different `GameKey`s (e.g. `"xg-grid"`
  and `"xg-path"`)
- When both are queried
- Then they may carry the same `SequenceNumber` value — `SequenceNumber`
  is an independent counter per `GameKey`, not a single global counter,
  matching `IRoundSchedulingOptionsResolver`'s existing per-`GameKey`
  independence (REQ-301)

**Display-only — never an identifier for routing, submission, or lookup:**
- Given any client, automated or human-driven, that needs to submit a
  guess or suggestion, fetch a specific round's grid, or look up a
  leaderboard entry tied to a round
- When it makes that request
- Then it uses the round's `roundId` (GUID) exactly as it does today —
  `SequenceNumber` is never accepted as a path/route parameter, request
  body identifier, or foreign-key value anywhere in the system, and no
  endpoint resolves a round by `SequenceNumber`; `roundId` remains the
  real primary/foreign key for every internal wiring path this document
  already describes (REQ-303, REQ-401-410's leaderboard lookups, guess/
  suggestion submission)

**Backfill of historical rows:**
- Given the migration that introduces `SequenceNumber` runs against a
  database with existing Round rows for one or more `GameKey`s
- When the migration completes
- Then every existing row has a `SequenceNumber` assigned by ordering that
  `GameKey`'s own rows by `StartTime` ascending and numbering them 1, 2,
  3, ... with no gap and no duplicate within a `GameKey` — the backfilled
  history is indistinguishable, by the two rules above, from a sequence
  generated entirely by the assignment behavior going forward

**Surfaced on every round-shaped response:**
- Given any endpoint that already returns a Round in one of the following
  shapes: the active round for display (`CurrentRoundResponse`, REQ-303),
  the active xG Path round for display (`CurrentPathResponse`,
  REQ-1201/1202), a closed-round listing entry (`ClosedRoundSummary`,
  REQ-408), a round-generation result (`GenerateRoundResponse`, REQ-301),
  or an admin round-control read (`AdminRound`, REQ-505)
- When that endpoint responds
- Then the response also includes the round's `sequenceNumber`, alongside
  its existing `roundId` — `roundId` is unchanged and remains present on
  every one of these shapes; no existing field is removed or renamed

**Display in the admin round-control section:**
- Given an admin viewing the round-control section of the admin screen
  (`RoundControlSection.tsx`, REQ-505)
- When the active round loads
- Then the displayed label uses the round's `sequenceNumber`, not its
  `roundId` — `"Grid Round #{sequenceNumber}"` when the section's
  `GameKey` is `"xg-grid"`, `"Path Round #{sequenceNumber}"` when it is
  `"xg-path"` — and no raw GUID appears as visible text anywhere in this
  section

  Note: as of this writing, `RoundControlSection.tsx` is hardcoded to
  `"xg-grid"` and no equivalent admin round-control UI element exists for
  `"xg-path"` (`XGPathCycleSection.tsx` shows cycle/pool metrics, not a
  round GUID or number, so there is nothing to fix there today). The
  `"Path Round #{sequenceNumber}"` phrasing above is the forward-looking
  convention to apply whenever a `"xg-path"` round-control UI element is
  added, not an unimplemented gap in this story — this half of the
  criterion is currently vacuously satisfied.

**Test level:** Unit (`SequenceNumber` assignment — `MAX + 1` scoped to
`GameKey`, read immediately before the creation insert), API/Integration
(new REQ-304-named tests prove two same-`GameKey` rounds are always
distinct and gapless, and that two different-`GameKey` rounds each
independently land on `SequenceNumber == 1` rather than sharing a single
global counter; every DTO listed above carries `sequenceNumber` alongside
an unchanged `roundId`), Component (`AdminScreen.test.tsx` updated to
assert the `"Grid Round #N"`/`"Path Round #N"` text and that no GUID
substring is ever rendered). The migration's backfill logic (raw SQL,
`ROW_NUMBER() OVER (PARTITION BY "GameKey" ORDER BY "StartTime")`) is
verified by manual/code review of the migration, not an automated test —
this repo's test suite runs against the EF Core InMemory provider, which
does not execute raw-SQL migrations, and no real-Postgres-backed test
infrastructure exists here yet.

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
- **Status note (2026-07-27, REQ-410/S-078 — implemented):** membership
  itself (below) is unaffected by ADR-0043 — there remains exactly one
  `League(type="global")`, auto-joined at signup, regardless of how many
  games the platform hosts. What REQ-410 changed is that the all-time
  *ranking* read from that membership (REQ-409) is now computed per
  `GameKey` rather than blended across every game's rounds — see REQ-410
  for the acceptance criteria and ADR-0043 for the rationale.
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
- **Status note (2026-08-30 — ADR-0095):** the "sorted ascending... per
  ADR-0021" description above no longer holds platform-wide. ADR-0095
  records a named, single-`GameKey` exception: `GameKey="xg-predict"`
  (REQ-1301-1305) sorts its own leaderboard **descending** (highest total
  first), a deliberate, product-confirmed departure from ADR-0021 for that
  one game. Every other `GameKey` (including the global leaderboard this
  requirement describes) is unaffected and still sorts ascending exactly as
  written above — per this document's ID-stability rule, this REQ's own
  text is not rewritten in place; see ADR-0095 for the full decision and
  REQ-1304's own scoring-direction acceptance criterion for xG Predict's
  side of it. `LeaderboardService`'s sort direction becoming per-`GameKey`
  rather than a single hardcoded order is implementation work not yet
  built — see ADR-0095's own follow-up note.
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
- **Status note (2026-07-27, REQ-410/S-078 — implemented):** ADR-0043
  found that `GetGlobalLeaderboardAsync` (REQ-409's median ranking, the
  method this REQ's own leaderboard resolves to) computed across every
  game's rounds with no `GameKey` filter at all — harmless while xG Grid
  was the only shipped game, but not correct once a second game (xG Path)
  ships its first round. REQ-410 now scopes the all-time ranking this REQ
  and REQ-409 describe per `GameKey`; `LeaderboardEndpoints` currently
  always requests xG Grid's ranking (no frontend game switcher yet,
  tracked separately as S-087/SCREEN-03). This REQ's own acceptance
  criteria above are unchanged and remain accurate as a description of the
  single-game case; see REQ-410 and ADR-0043 for the per-game scope.
  **(2026-08-02, S-087 — implemented):** the frontend game switcher above
  now exists, and `LeaderboardEndpoints` accepts an explicit `gameKey`
  query parameter rather than always requesting xG Grid's ranking — see
  REQ-410's own 2026-08-02 status note.
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
- **Cross-reference (2026-07-21, REQ-717 — built 2026-07-22):** two narrowings
  to this REQ's qualifying-rounds query are specified by REQ-717, not
  here, since both are guest-play-specific — this REQ's own text is
  otherwise unchanged. (1) A guest identity's rounds never count as
  qualifying rounds at all, regardless of count. (2) A claimed
  (guest-then-upgraded) account's rounds closed before the moment of
  claiming never count as qualifying rounds either — only rounds closed
  after claiming do. See REQ-717 for the full rationale for both.
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
- **Cross-reference (2026-07-27, REQ-410/S-078 — implemented):** ADR-0043
  found that the median ranking this REQ defines was computed across
  every game's rounds combined, with no `GameKey` filter —
  `GetGlobalLeaderboardAsync` and `GetPerRoundFinalPointsByUserIdsAsync`
  took no `gameKey` parameter, unlike the other three
  `ILeaderboardService` methods. REQ-410 now scopes this ranking per
  `GameKey` — this REQ's own median definition, qualifying-round
  definition, and 5-round minimum above are unchanged by that; REQ-410
  adds a per-game filter on top of them, it does not alter the formula
  itself. See REQ-410 for the acceptance criteria and ADR-0043 for the
  full rationale.
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

**REQ-410 – Global League's all-time ranking is scoped per game**
*(Status: Implemented, 2026-07-27, S-078 — see ADR-0043 for the full
context and rationale, not re-derived here. xG Grid is the only shipped
game today, so `LeaderboardEndpoints` passes
`GridGameModule.XGGridGameKey` ("xg-grid") explicitly and behavior for
that one game is unchanged; there is nothing to scope against yet in
practice, even though the code change itself is small. Dedicated
REQ410-named tests in `LeaderboardServiceTests.cs` seed a second, real
`"xg-path"` `GameKey` and confirm qualifying rounds/medians/the 5-round
minimum are computed independently per game and never blended. Frontend
game-switcher UI remains a separate follow-up, S-087/SCREEN-03.)*
*(Status note, 2026-08-02, S-087 — implemented: `LeaderboardEndpoints`
now accepts an optional `gameKey` query parameter on every route that
reads a specific game's data, instead of always hardcoding
`GridGameModule.XGGridGameKey` — omitted defaults to xg-grid (preserves
prior behavior), an unrecognized value 400s. `LeaderboardScreen.tsx`
gained the game-switcher tab row this REQ's original status note said was
still missing. See `docs/backlog.md` S-087's "Built as" for the full
implementation.)*
> As a player on a platform with more than one game, I want the Global
> League's all-time ranking to reflect only the game I'm currently
> viewing, so a game with a different scoring model isn't blended into my
> ranking, and so I'm not compared against players who only play a
> different game.

- **Status:** Implemented. `GetGlobalLeaderboardAsync`
  (REQ-409's median ranking) gains a required `gameKey` parameter,
  matching the shape `GetActiveRoundLeaderboardAsync` (REQ-407),
  `GetClosedRoundsAsync`/`GetClosedRoundLeaderboardAsync` (REQ-408), and
  `GetWindowedLeaderboardAsync` (REQ-405) already have.
  `IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync` gains the
  matching `gameKey` parameter, added as a `round.GameKey == gameKey`
  filter to its existing `Guess`-`Round` join. `League` membership itself
  (REQ-401) is unchanged — there remains exactly one Global League; only
  the ranking read from it is scoped per game. **(2026-08-02, S-087):**
  `LeaderboardEndpoints` now accepts an optional `gameKey` query parameter
  (defaulting to xg-grid when omitted) on every route above except the
  single-round-by-id one, so the ranking is no longer always xG Grid's —
  see this REQ's own 2026-08-02 status note above. `LeaderboardScreen.tsx`
  gained the frontend game-switcher tab row this bullet previously said
  was still missing.
- Given the platform hosts more than one game, each with its own `GameKey`
- When a player requests the Global League's all-time ranking (REQ-409)
  for a specific game
- Then only rounds whose `Round.GameKey` matches the requested game count
  towards that player's qualifying-round total, median calculation, and
  5-round minimum (REQ-409's own definitions, unchanged — this REQ adds a
  filter on top of them, it does not alter the median formula itself)
- And rounds belonging to a different game contribute nothing to this
  ranking — a player who has played 5+ qualifying rounds of one game and
  zero of another is ranked (or correctly excluded, per REQ-409) for each
  game independently, never combined into one number
- And a request for the all-time ranking must specify which game's rounds
  to rank by — there is no ranking that silently spans every game, the
  same requirement REQ-405/407/408 already impose by taking an explicit
  `gameKey` or a specific `Round`

**Test level:** Unit (`GetGlobalLeaderboardAsync`/
`GetPerRoundFinalPointsByUserIdsAsync` filtered by `gameKey` return only
that game's qualifying rounds in the median/count; a player's rounds in
one game never appear in another game's qualifying-round count or
median) — covered, `LeaderboardServiceTests.cs`'s `REQ410_*` cases. API
(requesting the all-time leaderboard for two different games returns two
independent rankings, and a player who qualifies in one game but not the
other is present in exactly one of the two responses) — covered as of
2026-08-02 (S-087), `LeaderboardEndpointTests.cs`'s `REQ410_*` cases,
now that `LeaderboardEndpoints`'s route accepts an explicit `gameKey`
query parameter. UI (switching games on SCREEN-03 re-queries the active
scope with the new `gameKey`; the selected scope tab is preserved across
a game switch) — covered, `LeaderboardScreen.test.tsx`'s `REQ410`-named
cases.

---

**REQ-411 – Player stats / profile view (own and another player's)**
*(Status: Implemented, 2026-08-24. Backend (S-178) — `GET
/users/{userId}/stats?gameKey=` (`UserEndpoints.cs`), backed by a new
`ILeaderboardService.GetUserStatsAsync`. No new aggregate path: rounds
played/best/average `FinalPoints` reuse the existing
`IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync` query (REQ-408/409),
and rank reuses `GetRankedMembersAsync`, a helper extracted from
`GetGlobalLeaderboardAsync` with no behavior change to that method — never
a second, independently-drifting rank formula. `UserStatsResult
.HasRoundsPlayed` is the "no rounds played" discriminator this REQ
requires (`false` ⇒ `RoundsPlayed = 0`, `Best`/`Average`/`Rank` all `null`,
never `0`-filled). **Bug found and fixed mid-implementation:**
`GetPerRoundFinalPointsByUserIdsAsync`'s REQ-717/ADR-0036 guest/claimed-
account exclusion was unconditional, so the first version of this endpoint
always returned the zero-rounds-played shape for guest accounts and for a
claimed account's pre-claim rounds, even with 5+ genuinely qualifying
rounds — contradicting this REQ's own "Out of scope" carve-out that only
the *rank* figure inherits guest-eligibility rules. Fixed by adding an
`applyGuestEligibilityRules` parameter (default `true`, so
`GetRankedMembersAsync`'s existing ranking call and its pre-existing tests
are unaffected); `GetUserStatsAsync` is the one caller that passes `false`
for the three stats figures, while Rank still goes through the unchanged,
guest-excluding path.
**Frontend (S-179, same day):** a single new `UserStatsScreen.tsx`
(SCREEN-13, `frontend/src/users/`) renders both "own stats" and "another
player's stats" — the component itself has no own-vs-other concept beyond
the `userId`/`displayName` props it's handed, matching this REQ's own "the
same stats view" framing for the other-player case. Two entry points, as
this REQ's acceptance criteria require: an unconditional "My stats" link
on `SettingsScreen.tsx` (own stats — every account, guest or claimed, not
admin-gated), and every row's `DisplayName` on the leaderboard
(`LeaderboardRowsList.tsx`) becoming a `<button>` navigation target
(another player's stats) when a new optional `onSelectPlayer` prop is
supplied, threaded through all four leaderboard-scope components and
`LeaderboardScreen.tsx` up to `App.tsx`. Judgement call, recorded inline in
`LeaderboardRowsList.tsx`: the requesting user's own row, when already
visible in a loaded page of the main ranked list, is clickable too, for
list consistency — the REQ-607 pinned "you" footer row stays plain text
since Settings already covers that destination. `App.tsx` gained a `'stats'`
screen value on its existing hand-rolled `Screen` union plus a `#/stats`
hash entry (ADR-0039), and an in-memory `statsTarget`/`statsReturnScreen`
navigation seed (same pattern `leaderboardInitial`/`LeaderboardRoundTarget`
already established, ADR-0083) so "Back" returns to whichever screen
(Settings or the leaderboard) the player actually came from. Renders
roundsPlayed/bestFinalPoints/averageFinalPoints/rank when
`hasRoundsPlayed` is true (rank independently omitted, not shown as 0 or
an error, when it's `null` below REQ-409's 5-round minimum); a distinct
"no rounds played yet" empty state when `hasRoundsPlayed` is false; a
401 routes to `onAuthError` (same convention as every other authenticated
screen); a nonexistent `userId` (404) is a distinct not-found state, never
confused with the empty state. `architecture-reviewer`: PASS, no ADR
needed (narrow, spec-driven extension of already-decided patterns — ADR-0039
hash routing, ADR-0083 nav-seed). `quality-architect`: pass, after one
follow-up round closing a test gap. This REQ's UI acceptance criteria are
now covered end-to-end; no open scope remains under REQ-411 itself.)*
> As a player, I want to see my own performance stats (best score, average
> score, rounds played) and look up another player's stats the same way,
> so I have somewhere to check progress beyond the leaderboard's single
> ranked list.

**Own stats:**
- Given a logged-in player (guest or claimed account)
- When they open their own stats/profile view
- Then it shows, scoped to a single game's `GameKey` at a time (same
  per-game scoping REQ-410 already established for the all-time ranking —
  this REQ reuses that convention, not a new cross-game aggregate):
  rounds played (REQ-409's existing qualifying-round definition — a
  closed round the player submitted at least one guess in, no new
  "played" definition introduced), best single round's `FinalPoints`
  total (REQ-408's definition), average `FinalPoints` across their
  qualifying rounds, and their current all-time rank if they meet
  REQ-409's 5-round minimum (omitted, not zero, when they don't — the
  same distinction REQ-409 already makes on the leaderboard itself)
- And if the player has zero qualifying rounds for the selected game, the
  view shows that plainly (zero rounds played, no best/average/rank
  figures rather than a `0`) rather than an empty or broken-looking screen

**Viewing another player's stats:**
- Given a logged-in player and any other player's `DisplayName` shown
  somewhere in the app they already have access to (the leaderboard is
  the only such surface as of this REQ)
- When they select that display name
- Then they see the same stats view, scoped to that player, read-only —
  no action is available on someone else's stats beyond viewing them
- Given the viewed player has zero qualifying rounds for the selected game
- Then the same "no rounds played yet" presentation applies, not an error

**Authorization boundary:**
- Given a request for any player's stats (own or another's)
- When the caller has no valid session
- Then the request is rejected with `401` — this view is not reachable by
  a fully logged-out visitor, unlike REQ-511's banner
- Given a valid session
- Then the same stats are returned regardless of whose stats are
  requested — there is no additional per-player privacy toggle in this
  REQ (see Out of scope)

**Out of scope for this REQ:** an opt-out/privacy setting letting a player
hide their stats from others (every account's stats are visible to any
other logged-in account, the same exposure level `DisplayName` already
has on the leaderboard); a stricter "solved" definition based on getting
every cell in a round correct (this REQ's "rounds played" reuses REQ-409's
existing qualifying-round definition — closed round, at least one guess —
not a new all-correct threshold); any cross-game combined total (each
`GameKey` is scoped independently, same as REQ-410); guest-specific
exclusions beyond what REQ-409 already applies to the rank figure (a
guest's rounds-played/best/average figures are shown the same as a
claimed account's — only the *rank* figure inherits REQ-409's existing
guest-eligibility rules, nothing new here).

**Test level:** Unit (the stats aggregate reuses REQ-408/409's existing
per-round total and qualifying-round queries, scoped by `GameKey` and
`UserId`; zero-qualifying-rounds returns the "no rounds played" shape, not
a `0`-filled one; rank is omitted below REQ-409's 5-round minimum). API
(`GET /users/{userId}/stats?gameKey=` returns 401 with no session; returns
the same shape for a caller's own id and another player's id; a
nonexistent `userId` returns 404). UI (own stats reachable from an entry
point in Settings/header nav; another player's stats reachable by
selecting their display name on the leaderboard; the zero-rounds-played
empty state renders distinctly from a loading/error state) — covered as of
2026-08-24 (S-179): `UserStatsScreen.test.tsx` (own/other-player rendering,
populated/empty/not-found/error states, omitted-rank, per-game switching),
`LeaderboardRowsList.test.tsx` (row-name-as-nav-target), and extensions to
`SettingsScreen.test.tsx`/`LeaderboardScreen.test.tsx`; three new
`App.test.tsx` routing cases cover own-stats-from-Settings-and-Back,
other-player-from-leaderboard-and-Back-to-leaderboard, and
reload-restore-fallback-to-own-stats.

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

**REQ-507 – Admin guest/user metrics view** *(Status: Implemented, Tier 0,
S-073, 2026-07-25)*
> As an admin, I want a live count of how many accounts exist and how many
> of those are guests, so I can gauge guest-play adoption and how many
> guest accounts are accumulating, without having to query the database
> directly.

- **Built as (S-073):** `GET /admin/accounts/metrics`
  (`XGArcade.Api.Admin.AdminAccountsEndpoints`, Admin policy, registered
  unconditionally including Production per the scope note below) returns
  `AdminAccountMetricsResponse(TotalUserCount, CurrentGuestCount,
  ClaimedGuestCount)`, backed by three new `IUserRepository` methods
  (`CountUsersAsync`/`CountGuestsAsync`/`CountClaimedGuestsAsync`), each a
  single `CountAsync` query — no in-memory materialization of the `User`
  table. Covered by `AdminAccountsEndpointTests.cs` and new
  `UserRepositoryTests.cs` cases; not independently run against a live
  `dotnet test` in this build environment (no .NET SDK available) —
  hand-traced against this REQ's own acceptance criteria instead.
- **Scope note (why this is not gated like REQ-505/506):** REQ-505/506 are
  restricted to non-Production because their entire stated rationale is
  managing seeded/test data ("so I don't have to wait for real time to
  pass," "clean up seeded/test accounts") — neither has any legitimate use
  against real Production data. This REQ is different: it is a read-only
  view of real account counts, and knowing how many real users vs. guest
  accounts exist in the actual running system is itself the point, not a
  side effect of test-data management. It is therefore visible to any
  authenticated admin in every environment, including Production, gated
  only by the existing "Admin" authorization policy (`Admin__UserIds`) —
  the same policy every other admin action in this document already
  requires, not a weaker one, and not an environment check.
- Given the admin is authenticated (`Admin__UserIds`) and opens the admin
  screen's metrics view (REQ-504)
- When the view loads
- Then it displays, as a live count as of the time of the request (not a
  cached/stale snapshot): total user count (every `User` row, regardless
  of `IsGuest`/`ClaimedAt`), current guest count (`IsGuest = true`), and
  claimed-guest count (`ClaimedAt IS NOT NULL` — accounts that originated
  as a guest and have since been claimed into a real account, per
  REQ-717)
- And "current guest count" is the same as "unclaimed guest count" by
  construction — `IsGuest` and `ClaimedAt` can never disagree (claiming
  clears `IsGuest` and stamps `ClaimedAt` atomically, REQ-717/ADR-0036) —
  this view labels the count "current guests" rather than requiring an
  admin to know that invariant to interpret it correctly
- Given a non-admin user reaches this view directly
- Then they receive the same 403 defense-in-depth REQ-504's other
  sections already apply
- **Out of scope:** "rounds played" or any other Round/Guess-derived
  count. This view's purpose is account-shape visibility (how many
  accounts exist, how many are guests, how many converted) — round/
  participation counts are a different data domain, already surfaced
  elsewhere (leaderboards, round history), and adding them here would
  blur this view's scope without a stated need.

**Test level:** API, UI

**REQ-508 – Admin force-clear guest accounts (bulk)** *(Status: Implemented,
Tier 0, S-073, 2026-07-25)*
> As an admin, I want to immediately delete every current guest account on
> demand, so I can clear accumulated guest accounts right now — before
> REQ-718's scheduled purge exists or runs, as a manual remedy if it ever
> fails, or to quickly reset seeded guest test data in a non-Production
> environment — without waiting on either of REQ-718's time-boxed rules.

- **Mechanism (binding, per ADR-0038):** deletes each matching account by
  calling `IAccountDeletionService.DeleteAccountAsync` once per account —
  the exact same anonymize-and-keep-`Guess`-rows mechanism REQ-710,
  REQ-506, and REQ-718 all use. This REQ introduces no second/raw
  bulk-delete code path, per ADR-0038's explicit instruction that "any
  future admin path" delete a guest account only through this service.
- **Selection mechanism vs. REQ-506 (deliberate, not an oversight):** this
  is a new capability, not an extended form of REQ-506's `DELETE
  /admin/users?email=`. REQ-506 identifies one already-known account by
  email; this REQ selects an unbounded set of accounts by a filter
  (`IsGuest = true`) with no identifier supplied at all — a fundamentally
  different selection shape, not an extra query parameter on the same
  endpoint. A new read path (list ids where `IsGuest = true`) and a new
  bulk-delete action are required.
- **Scope: every current guest, unconditionally — no age or inactivity
  filter.** This action deletes every account with `IsGuest = true` at
  the moment it runs, full stop. It does not filter by how long an
  account has been unclaimed (REQ-718 rule 2's 30-day threshold) or how
  long it has been inactive (REQ-718 rule 3's 7-day threshold, via
  `LastActiveAt`) — those graduated, automatic thresholds belong to
  REQ-718's scheduled job. This action's purpose is different: an
  immediate, deliberate, admin-triggered full sweep, not a gentler/
  filtered variant of REQ-718's rules. A claimed account (`IsGuest =
  false`) is never eligible, automatically, purely because selection is
  exclusively on `IsGuest = true` — no separate exemption logic is
  needed or added.
- **No "currently active" exemption.** A guest account created moments
  ago, or one with a live/active login session right now, is deleted
  exactly the same as any other matching account — this action does not
  attempt to detect or special-case "recently active" guests. The
  pre-confirmation count (below) is this action's intended safeguard
  against unintended blast radius, not an automatic scope carve-out.
- Given an admin is authenticated (`Admin__UserIds`)
- When the admin initiates this action
- Then the exact count of accounts currently matching `IsGuest = true` is
  shown before anything is deleted (a dry-run count, not an estimate)
- And a second, explicit confirmation step is required before the
  deletion actually fires — at least as strong as REQ-506's existing
  two-step "Yes, delete permanently" / "Cancel" client-side confirm,
  extended to show the count from the previous step so the admin
  confirms a known, specific number of accounts, not an open-ended action
- Given the admin confirms
- Then every account matching `IsGuest = true` at that moment (which may
  differ slightly from the count shown if a guest account was created or
  claimed in between — this action is not required to re-verify the
  count is unchanged before executing) is deleted via
  `IAccountDeletionService.DeleteAccountAsync`
- And the action reports a per-account outcome (succeeded / not found /
  failed) rather than a single all-or-nothing result — the same
  reporting discipline REQ-503's bulk "approve" action already
  establishes for this document's other bulk admin actions
- Given `ASPNETCORE_ENVIRONMENT == Production`
- Then this action remains available, unlike REQ-505/506 — bulk-clearing
  guest accounts is a legitimate operational action against real account
  data (not a test-data-management action with no Production use case),
  and is gated by the existing "Admin" authorization policy in every
  environment exactly as REQ-507's metrics view is, not by an environment
  check
- Given a non-admin user
- When they attempt to reach or call this action directly
- Then they receive a 403, matching every other admin action in this
  document

- **Built as (S-073):** `GET /admin/accounts/guests/count` (the dry-run
  count) and `POST /admin/accounts/guests/clear` (the execute action),
  both in `XGArcade.Api.Admin.AdminAccountsEndpoints`, Admin policy,
  registered unconditionally including Production. Both reuse REQ-507's
  `IUserRepository.CountGuestsAsync` for the dry-run count; the clear
  action selects fresh ids via a new `IUserRepository.GetAllGuestIdsAsync`
  and deletes each via `IAccountDeletionService.DeleteAccountAsync` — the
  same service REQ-710/REQ-506/REQ-718 already use, no second deletion
  path (ADR-0038). A new `AccountDeletionService.UserNotFoundErrorMessage`
  const lets the endpoint classify each per-account outcome as
  `Succeeded`/`NotFound`/`Failed` without a second existence check.
  Covered by `AdminAccountsEndpointTests.cs`; not independently run
  against a live `dotnet test` in this build environment (no .NET SDK
  available) — hand-traced against this REQ's own acceptance criteria
  instead.

**Relationship to REQ-718:** REQ-718 (Implemented, `docs/backlog.md`
S-072, 2026-07-25 — see that REQ's own Status line) purges unclaimed
guests automatically after 30 days and inactive guests automatically
after 7 days, via a scheduled job. This REQ is the human-triggered,
immediate equivalent — usable as a manual remedy if the scheduled job
fails, an immediate full sweep is otherwise needed, or (now that both
REQs coexist as implemented) simply on demand, without waiting for either
of REQ-718's time-boxed rules to fire. **Resolution of the "shared
building block" question this section originally left open:** REQ-718
and this REQ ended up with separate, unfiltered-vs-filtered selection
queries rather than one shared query — `IUserRepository
.GetAllGuestIdsAsync` (this REQ, unconditional `IsGuest = true`) is
deliberately not built from REQ-718's
`GetUnclaimedGuestsOlderThanAsync`/`GetInactiveGuestsOlderThanAsync`
(30-day/7-day age filters), since this REQ's own scope note above is
explicit that no age/inactivity filter applies here — the two queries'
filter conditions are genuinely different, not a missed reuse
opportunity. Both still call the exact same `IAccountDeletionService`
per account, which remains the one piece ADR-0038 requires every guest
deletion caller to share. Note this is also the scenario ADR-0038's own
alternatives table already anticipated ("can be introduced later if a
third caller ever needs shared guest-selection logic") — this REQ is
that third caller.

**Test level:** API, UI (count preview, two-step confirm, per-account
outcome reporting), Integration (seeded unclaimed/claimed/mixed guest
rows — confirms only `IsGuest = true` accounts are deleted and claimed
accounts are untouched)

**REQ-509 – Admin review of player-submitted suggestions, with live
Wikidata commit**
> As an admin, I want to check a player-submitted suggestion (REQ-215)
> against a fresh Wikidata lookup and, if it holds up, commit the
> corrected data myself, so a genuinely correct suggestion actually fixes
> the game's data instead of just sitting unreviewed.

**Status: Implemented (2026-08-08, S-090).** Backend:
`GET /admin/suggestions` (lists every pending suggestion — player name,
asserted club(s), asserted nationality, submitting user's resolved display
name, and submission timestamp — batched via `IUserRepository
.GetByIdsAsync`, no N+1), `POST /admin/suggestions/{id}/lookup` (runs the
live Wikidata career/nationality query for that suggestion's own stored
`PlayerName`; a `WikidataQueryException` is reported as `503`
"lookup unavailable, try again," never silently treated as no-match,
per ADR-0046; `404` if the suggestion doesn't exist, `409` if it's already
resolved), `POST /admin/suggestions/{id}/commit` (writes the admin's
reviewed/confirmed values, moves the suggestion to `Committed`, records
`ResolvedByAdminId`/`ResolvedAt`), and `POST /admin/suggestions/{id}/reject`
(writes nothing, moves the suggestion to `Rejected`, records the same
audit fields) — all in a new `XGArcade.Api.Admin.AdminSuggestionEndpoints.cs`
file, `[RequireAuthorization("Admin")]`, deliberately kept separate from
`AdminEndpoints.cs` (REQ-501-503's file) rather than folded into it, per
ADR-0053. Commit and reject both call `IPlayerSuggestionRepository
.ResolveAsync`, so a suggestion is never left pending after either action.
The commit write path does not go through a single uniform mechanism:
nationality (single-valued) is written via `PlayerOverride`, exactly as
REQ-501's existing manual-override path already writes it (`Reason`/
`LockedByAdminId`/`LockedAt` set); club(s) (multi-valued, per REQ-113's
"ever played for, at any career point") are written as additive
`PlayerAttribute` rows instead, one per confirmed club not already
effective for that player — this split, and the reasoning for not routing
everything through `PlayerOverride`, is recorded in ADR-0060 (new). Neither
path ever writes `PlayerNameIndex` (ADR-0007/ADR-0053, unconditionally).
Frontend: `SuggestionsScreen.tsx` (`frontend/src/admin/`) is a new,
dedicated screen — never merged into `AdminScreen.tsx`'s existing
unverified-data queue (ADR-0053) — reachable via a "Player suggestions"
link added to `AdminScreen.tsx`, wired into `App.tsx` routing. **Bug found
and fixed during implementation:** an early version of the Wikidata career
lookup (`IWikidataClient
.QueryPlayerCareerAndNationalityByNameAsync`/`WikidataClient
.ParsePlayerCareerAndNationalityByNameBindings`) gated club detection on
the SPARQL row's `?startTime` qualifier parsing successfully (reusing
`WikidataCareerStintEntry`, whose `StartYear` is non-nullable by design for
ADR-0054's xG Path stint log) — since not every real P54 club-membership
statement carries a P580 start-time qualifier, this silently dropped clubs
with no recorded start date from the admin lookup's result, contradicting
this method's own "every non-deprecated P54 statement" contract and this
REQ's "fetch every club the player has ever been recorded as a member of"
acceptance criterion. Fixed same-session (before merge) by changing
`WikidataPlayerCareerLookupResult.Clubs` to a plain distinct-name list
gated only on `?clubLabel` being bound, never on `?startTime`; a regression
test (`WikidataClientTests.cs`) pins a club with no `startTime` binding
still appearing in the result. Test coverage: backend
`AdminSuggestionEndpointTests.cs` (21 NUnit tests, `REQ509_...`/
`REQ510_...` naming) plus `WikidataClientTests.cs` extensions for the new
query method and the bug-fix regression case; frontend
`SuggestionsScreen.test.tsx` (9 tests) plus an `App.test.tsx` navigation
test — 486/486 Vitest tests passing (independently verified), clean
architecture review and quality review. **Backend caveat: the `dotnet` SDK
was unavailable in this build environment** — the backend implementation
and its 21 tests were hand-traced against `AdminEndpoints`/
`SuggestionEndpoints`/`WikidataClientTests`'s existing, already-verified
patterns rather than actually built or run; confirm in CI before treating
the backend half as independently verified.

**Tier framing:** see REQ-215's own Tier framing note — this REQ is part
of the same new pipeline, not scoped or tiered independently of it.

**Relationship to REQ-501–503 and ADR-0029/ADR-0032 (status note recording
a resolved architecture question — decided 2026-08-01, ADR-0053):**
ADR-0029's original follow-up note anticipated that "when a real
user-suggestion channel exists, it should feed the same
`Confidence = "unverified"` review queue" REQ-503 already exposes (`GET
/admin/player-data/unverified`) — at the time, ADR-0029 still kept the
guess-time-fallback path unverified. ADR-0032 later reversed that (every
Wikidata-sourced write, including the guess-time fallback, now persists
`verified` immediately), so today REQ-503's queue is empty by construction,
with no code path writing `unverified` at all (see REQ-503's own
2026-07-20 status note). This REQ's suggestions are a genuinely different
kind of input from what that queue was built around — a human assertion
(club(s), nationality, submitter), not a Wikidata sync/lookup result — that
doesn't map cleanly onto a `PlayerData` row's existing shape. **Decided
(2026-08-01, ADR-0053):** this REQ's pending suggestions get their own,
separate admin view — never surfaced as a new row type in REQ-503's
existing queue and never a shared row shape or merged UI. ADR-0053 also
explicitly reconfirms that ADR-0007's autocomplete/correctness-boundary
rule applies without exception to this REQ's commit action — committing a
suggestion may only ever write `PlayerAttribute`/`PlayerOverride`, never
`PlayerNameIndex`.

**Listing pending suggestions:**
- Given one or more pending suggestions (REQ-215) exist
- When an admin opens the suggestion review view
- Then every pending suggestion is listed with the player name, the
  asserted club(s), the asserted nationality, the submitting user, and the
  submission timestamp

**Live Wikidata query:**
- Given a specific pending suggestion
- When an admin triggers a live lookup for that suggestion's player name
- Then the system runs the same Wikidata SPARQL query shape already used
  for player-attribute resolution (occupation `P106`, citizenship `P27`,
  club membership `P54` — REQ-103/REQ-211's existing intersection-query
  pattern, ADR-0011) to fetch every club the player has ever been
  recorded as a member of (REQ-113's "ever played for, at any career
  point" definition) and the player's nationality
- And a query that fails to complete is reported to the admin as "lookup
  unavailable, try again" — it is never silently treated as "no data
  found," the same timeout-vs-no-match distinction ADR-0046 already
  established for REQ-211's guess-time path

**Review and commit:**
- Given the fetched Wikidata data for a pending suggestion
- When an admin reviews it against the suggestion's asserted claim and
  marks it correct
- Then the corresponding `PlayerAttribute`/`PlayerOverride` data is
  written the same way REQ-501's manual-override path writes it today
  (admin-authenticated, audit fields set; a reason is required and recorded
  whenever the commit includes a nationality, since that's the only path
  with a column to persist it to — a clubs-only commit does not require a
  reason, since `PlayerAttribute` carries no audit columns for it to be
  written to; see ADR-0060's 2026-08-10 status note) — never
  through `PlayerNameIndex` (ADR-0007's autocomplete/correctness boundary
  applies here without exception: committing a suggestion changes
  correctness-checking data only, and must never be implemented as a
  write to the name index)
- And the action is logged with `admin_id` and a timestamp, the same
  discipline REQ-503's existing approve/correct/remove actions already
  establish
- And the suggestion's own stored state moves to a resolved/committed
  state — it is never left pending after a commit
- Given the fetched Wikidata data does not confirm the suggestion's claim
- When an admin marks the suggestion rejected
- Then no `PlayerAttribute`/`PlayerOverride`/`PlayerNameIndex` write
  occurs, the suggestion's state moves to rejected, and the rejection is
  logged with `admin_id` and a timestamp exactly as a commit is

**Test level:** Unit (fetched data vs. the suggestion's claim is presented
for admin judgment, never auto-approved), API (the commit path writes only
through the override/attribute mechanism, never `PlayerNameIndex`; the
reject path writes nothing; both actions are Admin-policy-gated and
logged), Integration (Wikidata query mocked, matching the existing pattern
in `WikidataClientTests.cs`; a query timeout is distinguished from a
genuine no-match, not conflated), UI (admin)

- **Status note (2026-08-17, S-129, backend half only):** neither this
  REQ's acceptance criteria above nor REQ-510's said anything about what
  the commit response itself communicates back to the admin — both were
  silent on response shape, only specifying what gets written server-side.
  In practice this let `CommitPlayerDataResponse` merely echo back the
  admin's confirmed input regardless of whether a write actually happened
  (e.g. every asserted club already effective for that player, so nothing
  was written), which was indistinguishable from a genuine write in the
  response shape — and `SuggestionsScreen.tsx`'s main approval flow
  (`PendingSuggestionRow`) showed no confirmation at all on commit.
  `CommitPlayerDataAsync`/`CommitPlayerDataResponse` (`AdminSuggestionEndpoints.cs`)
  now report what was actually written: `PlayerCreated` (a new `Player` row
  vs. an existing one reused for that `WikidataQid`), `NationalityWritten`
  (a `PlayerOverride` insert/update actually happened), and `ClubsAdded`
  vs. `ClubsAlreadyEffective` (a partition of the confirmed clubs by
  whether each got a new `PlayerAttribute` row or was already effective
  and skipped). No write-path behavior changed — see ADR-0060's 2026-08-17
  status note for the full reasoning. This REQ's own acceptance criteria
  above are left as written (they describe the write, which is unchanged);
  a future REQ update should fold "the response confirms what was written"
  into this REQ's Given/When/Then text if the frontend half (planned as a
  follow-up story) makes it a first-class UI requirement rather than
  purely a response-shape implementation detail. The identical note applies
  to REQ-510, which shares the same `CommitPlayerDataAsync`/
  `CommitPlayerDataResponse`, not duplicated in that REQ's own section.

**REQ-510 – Admin manual Wikidata search-and-add (independent of a
suggestion)**
> As an admin, I want to search Wikidata directly by player name and add
> the result to the database, without needing a player-submitted
> suggestion to exist first, so I can proactively fix or extend the data.

**Status: Implemented (2026-08-08, S-090).** Backend:
`POST /admin/player-search/lookup` (runs the identical live Wikidata
fetch REQ-509's `/admin/suggestions/{id}/lookup` uses, but for a
name supplied directly in the request body rather than a suggestion's
stored `PlayerName`) and `POST /admin/player-search/commit` (writes
through the identical commit path as REQ-509's — same nationality-via-
`PlayerOverride`/club(s)-via-additive-`PlayerAttribute` split, ADR-0060 —
`[RequireAuthorization("Admin")]`), both in the same
`AdminSuggestionEndpoints.cs` file as REQ-509. Per that file's own header
comment, the fetch and commit logic are each implemented exactly once
(`LookupPlayerAsync`/`CommitPlayerDataAsync` helpers) and called from both
this REQ's standalone endpoints and REQ-509's suggestion-scoped ones,
rather than duplicated — no suggestion record is read, created, or
required by either of this REQ's endpoints. Frontend: the same
`SuggestionsScreen.tsx` exposes this as a standalone search-and-add entry
point alongside the suggestion-review list. Test coverage: included in
`AdminSuggestionEndpointTests.cs`'s 21 tests (`REQ510_...` naming) and
`SuggestionsScreen.test.tsx` — see REQ-509's own status note for the full
shared coverage/caveat detail (same file, same caveats, not duplicated
here).

**Tier framing:** see REQ-215's own Tier framing note — same new pipeline.

- Given an admin is in the admin data-review area
- When the admin searches by player name directly, with no pending
  suggestion (REQ-215) involved
- Then the system runs the identical live Wikidata fetch REQ-509 uses
  (occupation `P106`, citizenship `P27`, club membership `P54`) for that
  name
- Given the fetched result
- When the admin reviews it and commits
- Then it is written through the identical commit path as REQ-509's —
  `PlayerAttribute`/`PlayerOverride`, never `PlayerNameIndex`,
  admin-authenticated, reason recorded, `admin_id`/timestamp logged
- And this action requires no suggestion record to exist before, during,
  or after it — using this path leaves REQ-215/REQ-509's suggestion
  pipeline completely unaffected, and no suggestion row is created as a
  side effect of this action

**Test level:** API (search triggers the same live-lookup mechanism as
REQ-509; commit uses the identical write path; no suggestion record
required or created), UI (admin)

**REQ-511 – Site-wide announcement banner (admin-managed)**
> As an admin, I want to post a notification/banner with information
> visible to all users on the site (e.g. maintenance notices,
> announcements), and be able to edit or take it down later, so I can
> communicate with every visitor without needing a code deploy.

**Creating and editing the banner:**
- Given no banner exists yet, or a banner (active or inactive) already
  exists with saved text
- When an admin submits a non-blank message (with a reasonable max
  length, exact limit left to implementation) via the same Admin-only
  area used for the rest of §4.5's admin actions
- Then the banner's message is created (if none existed) or the existing
  banner's message is replaced with the new text — there is exactly one
  banner record at a time, never a list or queue of concurrent banners
- And a blank/empty message is rejected with a validation error and does
  not change the stored banner

**Activating and deactivating:**
- Given a banner with saved text exists and is currently inactive (or has
  never been activated)
- When an admin activates it
- Then it becomes visible to every visitor the next time they fetch it
  (e.g. on page load or the frontend's next poll) — no push/real-time
  delivery is required
- Given a banner is currently active
- When an admin deactivates it
- Then it stops being visible to every visitor the next time they fetch
  it, and deactivating does not delete the banner's saved message — an
  admin can reactivate the same text later, or edit it first, without
  retyping it from scratch
- Given an admin edits the message text of a banner that is currently
  active
- Then the updated text is what subsequent visitors see on their next
  fetch — an edit to an already-active banner does not require a
  separate deactivate/reactivate step

**Visibility to every user, including fully logged-out visitors:**
- Given an active banner exists
- When any visitor — logged-in, guest, or fully logged-out with no
  session at all — fetches the current banner
- Then they receive its message, and fetching it requires no
  authentication of any kind, the same way a public health-check-style
  endpoint would behave (maintenance notices must reach a logged-out
  visitor too, not only signed-in players)
- Given no banner exists, or the only banner on record is inactive
- When any visitor fetches the current banner
- Then the response indicates there is no active banner (not an error),
  and no banner is shown

**Authorization boundary on write actions:**
- Given a request to create, edit, activate, or deactivate the banner
- When the caller has no valid session
- Then the request is rejected with `401`
- Given a request to create, edit, activate, or deactivate the banner
- When the caller is authenticated but is not in the `Admin:UserIds`
  allowlist
- Then the request is rejected with `403`, using the same "Admin"
  authorization policy already enforced by `AdminEndpoints`/
  `AdminManagementEndpoints`/`AdminAccountsEndpoints` — no new
  authorization policy is introduced for this REQ
- And in both rejection cases above, no banner state changes as a result
  of the rejected request

**Out of scope for this REQ:** multiple concurrent/queued banners (a
second `POST`/edit replaces the single existing banner, it does not
create an additional one); scheduled start/end times (an admin must
activate and deactivate it manually — no "go live at" or "expire at"
fields); per-user dismiss-and-remember (there is no per-user dismissed
state — a banner that is active is shown to everyone who fetches it,
including someone who dismissed an earlier version of it, since there is
no dismissal to remember); severity/color levels or categorization (a
single, unstyled message type); rich text or HTML formatting (plain text
only for v1).

**Test level:** Unit (blank-message validation), API (create/edit
replaces the single banner rather than creating a second one;
activate/deactivate flip visibility for subsequent reads; deactivating
preserves the saved message; the read endpoint requires no
authentication and returns a clear no-active-banner state when
applicable; write actions reject `401`/`403` under the Admin policy with
no state change on rejection), UI (an active banner is visible to a
logged-in user, a guest, and a fully logged-out visitor; an admin can
create/edit/activate/deactivate it from the existing admin area)

---

**REQ-512 – Admin notification badge for pending player suggestions**
> As an admin, I want a clear notification (a count/badge) in the admin UI
> when there are pending player-submitted suggestions waiting for review,
> so I don't have to open the Suggestions screen just to check.

**Badge count source and display:**
- Given at least one `PlayerSuggestion` row is in `Pending` status
  (REQ-215/509)
- When an admin who satisfies the existing `"Admin"` authorization policy
  loads `AdminScreen.tsx` (SCREEN-04)
- Then the "Player suggestions" entry point shows a count equal to the
  number of pending suggestions, derived from the same pending-suggestion
  data REQ-509's `GET /admin/suggestions` already returns — no new
  backend count endpoint, and no second data source duplicating that
  list, is introduced by this REQ
- Given zero `PlayerSuggestion` rows are in `Pending` status
- When an admin loads `AdminScreen.tsx`
- Then no badge is shown next to "Player suggestions" — a zero count is
  represented by the badge's absence, not a badge displaying "0"

**Badge freshness:**
- Given an admin resolves a suggestion (commits or rejects it, REQ-509)
  from `SuggestionsScreen.tsx`, changing the number of pending
  suggestions
- When the admin navigates back to `AdminScreen.tsx`
- Then the badge reflects the updated count as of that navigation — the
  same "fetch on load, no polling" behavior REQ-511's banner and
  REQ-503/504's existing admin queue already use; no push/real-time
  update is required
- Given the admin remains on `AdminScreen.tsx` without navigating away,
  and a suggestion is resolved by a different admin session in the
  meantime
- Then the badge is not required to reflect that change until the admin
  next loads or reloads `AdminScreen.tsx` — this REQ does not require a
  live-updating count within a single page view, and no
  polling/websocket mechanism is introduced to provide one

**Authorization boundary:**
- Given a request for the pending-suggestion data this badge is derived
  from
- When the caller has no valid session, or is authenticated but not in
  the `Admin:UserIds` allowlist
- Then the request is rejected the same way REQ-509's existing
  `GET /admin/suggestions` already rejects it (`401`/`403`) — no new
  authorization policy is introduced, and no suggestion data or count is
  exposed to a non-admin or guest as a side effect of this REQ
- Given a non-admin or guest is using the site
- Then no pending-suggestion badge or count is rendered anywhere in their
  UI — the badge is only ever visible from within the already-gated
  `AdminScreen.tsx`, the same reachability boundary the existing "Player
  suggestions" entry point (REQ-504/509) already has

**Out of scope for this REQ:** a live/polling/websocket-driven badge that
updates without a page load or navigation (no push mechanism exists
anywhere in this system); breaking the count down by category, age, or
any other dimension — the badge is a single aggregate count; a badge for
anything other than pending player suggestions (REQ-903's incident
reports are S-098, an explicitly separate story with no existing data
source to badge against — see that backlog entry).

**Tier framing:** same admin-review-area extension pattern as
REQ-501–511, not a new pipeline. Per `docs/backlog.md` S-097's own note,
this is low-risk relative to S-098: REQ-509's pending-suggestion data
already exists and is already fetched for `SuggestionsScreen.tsx`; this
REQ adds a read of that same existing data to a second screen
(`AdminScreen.tsx`), not a new data source, endpoint, or pipeline.

**Test level:** Unit (a positive pending count renders a badge; a zero
count renders no badge, not a "0" badge), API (the pending-suggestion
data this badge is derived from is reachable only under the existing
`"Admin"` policy — `401` with no session, `403` for a non-admin,
consistent with REQ-509's existing endpoint), UI (the badge appears next
to "Player suggestions" on `AdminScreen.tsx` when pending suggestions
exist, and reflects an updated count after navigating back from
resolving one on `SuggestionsScreen.tsx`; no badge/count is rendered for
a non-admin or guest)

**REQ-513 – Admin refresh of an existing Player's data from Wikidata**
*(Status: Implemented, `POST /admin/players/{id}/refresh-from-wikidata`,
`AdminEndpoints.cs`, ADR-0086; test coverage in `AdminEndpointTests.cs`/
`PlayerRepositoryTests.cs`/`WikidataClientTests.cs` — not yet
compiler-verified in this sandbox, no `dotnet` SDK available; confirm in
CI before merge. No admin UI built for this yet, API only, matching
REQ-501-503's own starting point before REQ-504 added a UI.)*
> As an admin, I want to re-fetch a specific player's name, position, birth
> year, and photo from Wikidata using the player's already-stored
> `WikidataQid`, so a bad or stale value frozen in at creation (REQ-1207)
> — e.g. a garbled name shown to a player as ground truth (#239) — can be
> corrected without editing the database by hand.

**Scope note (refresh from source, not free-text editing):** this is a
re-fetch action, not a new data-entry path — the admin never types a
corrected name/position/birth year/photo directly, only triggers a refresh
against the player's own already-stored `WikidataQid`. Consistent with
ADR-0032's trust model (all Wikidata-sourced data is treated as verified by
default, with no human review step gating it), this REQ re-applies that
same trust later, against the same already-trusted QID, rather than
introducing a second, manual way to set these fields that could itself
become a new source of error. If the re-fetched Wikidata value is itself
wrong, that is a Wikidata data problem outside this REQ's scope — fixing it
there, then re-running this action, is the intended remediation path. This
REQ does not add manual-override support for `Player.FullName`/`Position`/
`BirthYear`/`PhotoUrl` (there is no `PlayerOverride`-equivalent mechanism
for these four scalar columns; see REQ-1207's scope note on why they live
directly on `Player`, not as `PlayerAttribute` rows). This action never
writes `PlayerNameIndex` — that table is populated only by a separate
import pipeline (`PlayerNameIndexImporter`, ADR-0007/ADR-0053's
autocomplete/correctness boundary); a stale entry there, if any, is a
pre-existing, separate concern this REQ does not address.

**Where it lives and environment gating:** `AdminEndpoints.cs` (REQ-501-503's
file), behind the existing "Admin" authorization policy, same pattern as
every other endpoint in that file. Unlike REQ-505/506 (round control, user
deletion — deliberately non-Production-only testing tools, per ADR-0006),
this action is registered and available in every environment, including
Production, the same as REQ-501-503/509/510: its entire purpose is
correcting real player data that goes wrong in production — restricting it
to non-Production would defeat the requirement that prompted it.

**Refreshing a player:**
- Given an existing `Player` row identified by its id, with a non-null
  `WikidataQid`
- When an admin triggers a refresh for that player
- Then the system re-queries Wikidata for that specific `WikidataQid`,
  fetching the same four properties `Player` already stores at creation
  (label → `FullName`, P413 → `Position`, P569 → `BirthYear`, P18 →
  `PhotoUrl`) — no admin-supplied value for any of these fields is ever
  accepted by this action

**Only changed fields are written:**
- Given the freshly-fetched value for a field is non-null/non-empty and
  differs from the value currently stored on the `Player` row
- Then that field is updated to the freshly-fetched value
- Given the freshly-fetched value for a field is null/empty (Wikidata
  currently has no binding for that property), regardless of what is
  currently stored
- Then that field is left unchanged — a missing binding is treated as "this
  query returned no answer for this property," never as evidence the
  existing stored value is wrong, the same "absence is not evidence of
  wrongness" principle ADR-0046 already establishes for a guess-time
  timeout; this action never wipes an existing value to null
- Given the freshly-fetched value for a field is identical to the value
  already stored
- Then that field is not written — this action writes only the fields that
  actually changed on a given refresh, never a blanket rewrite of all four
- Given a refresh completes
- Then the response indicates, per field, whether it changed and, if so,
  its old and new value — so the admin can see exactly what was corrected,
  not just an unconditional success message

**No manual value acceptance / no reason field:**
- Given this action re-applies data from a source already trusted by
  default (ADR-0032), not a new manual admin assertion
- Then, unlike `PlayerOverride`'s "correct" action (REQ-501), no `reason`
  field is required or accepted — matching REQ-503's "approve" action,
  which also requires no reason, for the same underlying reason: applying
  already-trusted source data is not recording a new manual judgment

**Error handling:**
- Given a `Player` id that does not exist
- Then the request is rejected with `404`
- Given a `Player` id that exists but has a null `WikidataQid`
- Then the request is rejected with `409` — there is no QID to refresh
  from, and this action never falls back to a name-based search (that is
  REQ-510's separate, existing capability, not this one)
- Given the Wikidata query for the stored `WikidataQid` fails to complete
  (times out or errors)
- Then the request is rejected with `503`, "lookup unavailable, try
  again" — the same contract REQ-509's `/admin/suggestions/{id}/lookup`
  already establishes (ADR-0046) — never silently treated as "no fields
  changed"
- Given a caller with no valid session, or an authenticated caller not in
  `Admin:UserIds`
- Then the request is rejected `401`/`403` respectively, the same
  authorization boundary every other `/admin/*` endpoint in this file
  already enforces

**Audit trail:**
- Given `Player` has no admin-audit columns of its own (unlike
  `PlayerOverride`'s `LockedByAdminId`/`LockedAt` or `PlayerSuggestion`'s
  `ResolvedByAdminId`/`ResolvedAt`) — REQ-1207 added no such columns to
  `Player` when it introduced `Position`/`BirthYear`, and this REQ does not
  add them either; two new columns on `Player` for one narrow admin action
  do not carry their weight relative to the alternative below
- Then the action is recorded via a structured `ILogger` line at refresh
  time (admin id, player id, `WikidataQid`, and each field's old/new
  value) — the same "no row to attach an audit trail to → structured log
  line instead" precedent REQ-503's "remove" action already established,
  not a new general-purpose audit-log table

**Relationship to REQ-1207's set-once contract (deliberate, scoped
exception — not a silent contradiction):** REQ-1207 establishes that
`Position`/`BirthYear` (and, by the same code path, `FullName`/`PhotoUrl`)
are set once at `Player` row creation and never overwritten by any
automatic sync/backfill/live-lookup path, with one narrow, already-recorded
exception (the raw-URI `Position` bug fix). This REQ adds a second,
equally narrow exception: an explicit, single-player, admin-triggered
action — never an automatic background process. No automatic path (grid
generation, cache warming, the guess-time live fallback, the position/
birth-year backfill, the photo backfill) gains any new ability to overwrite
an existing value as a result of this REQ — REQ-1207's set-once contract
for every non-admin-triggered path is otherwise unchanged.

**Out of scope for this REQ:** an admin UI/page for this action (API only,
same starting point REQ-501-503 had before REQ-504/S-026 added a UI);
bulk/multi-player refresh (single player per call only — REQ-503's
"approve" bulk pattern is not extended here); a way to browse/search for
which `Player` row to refresh (this REQ assumes the admin already knows the
target `Player`'s id, e.g. from investigating a bug report — building a
player-browsing admin view is a separate concern).

**Test level:** Unit (per-field diff/no-op logic: a differing non-null
fetched value overwrites, a null/empty fetched value never overwrites an
existing value, an identical fetched value is a no-op for that field), API
(404/409/503/401/403 error contract; a successful refresh persists only
the fields that changed and returns a per-field changed/unchanged old/new
result; Admin-policy-gated; registered and reachable in every environment
including Production, unlike REQ-505/506)

**REQ-514 – Admin UI for refreshing a Player from Wikidata**
*(Status: Deprecated, 2026-08-24 — superseded by REQ-515. Its own standalone
entry point (`PlayerRefreshSection.tsx`, a raw-Player-id text field) is
removed: REQ-515's inline refresh, surfaced directly from admin player
search once a matching local `Player` row is found, covers the same action
with no separate id-entry step — product-owner decision, since nothing
else in the admin UI ever surfaced a raw `Player` id for this standalone
field to consume in the first place. REQ-513's underlying endpoint
(`POST /admin/players/{id}/refresh-from-wikidata`) is UNCHANGED and still
in active use — only this REQ's standalone UI entry point is removed; the
shared `PlayerRefreshFieldsList` result-display component REQ-514
introduced is kept, now owned by REQ-515's inline entry point instead.)*
> As an admin, I want to trigger REQ-513's Wikidata refresh for a specific
> player and see what changed, from the admin page I already use for other
> player-data corrections, so I don't have to script an HTTP request to
> fix a bad or stale value I've found.
>
> **Superseded (2026-08-24):** this standalone entry point is removed — see
> REQ-515, which surfaces the same action inline from admin player search
> instead, requiring no id-entry step at all.

**Scope note (UI over REQ-513, no new backend behavior):** this REQ adds no
endpoint of its own — it is a UI surface over `POST
/admin/players/{id}/refresh-from-wikidata` (REQ-513), the same relationship
REQ-504 has to REQ-501-503/505/506. It follows REQ-504's own precedent of
starting an admin capability as API-only and adding a UI once there's a
concrete need to use it without scripting a request by hand.

**Where it lives:** a new `PlayerRefreshSection` component
(`frontend/src/admin/PlayerRefreshSection.tsx`), added to `AdminScreen.tsx`
(SCREEN-04) as an independent section following the same
own-fetch/own-state pattern every other section there already uses (e.g.
`UnverifiedDataSection`, `UserDeletionSection`). Placed near
`UnverifiedDataSection`, given both sections are about administering
`Player`/`PlayerData` — exact ordering is a UI-polish detail, not part of
this REQ's acceptance criteria. Unlike `RoundControlSection`/
`UserDeletionSection`, this section is not gated by the non-Production-only
`activeRound` probe: REQ-513's endpoint is registered and reachable in every
environment including Production (matching REQ-501-503/509/510's own
`AdminScreen.tsx` gating), so this section renders unconditionally, the
same as `UnverifiedDataSection`/`AccountMetricsSection`.

**Triggering a refresh:**
- Given an admin viewing `AdminScreen.tsx`
- When they type a `Player` id (a GUID, plain text input — no
  player-search/browse UI is added by this REQ, matching REQ-513's own
  "assumes the admin already knows the target Player's id" scope cut) and
  submit
- Then the UI calls REQ-513's endpoint for that id and, while the request is
  in flight, disables the input/submit control and shows a pending state
  (mirroring `UserDeletionSection`'s `deleting`/disabled-while-submitting
  pattern) — there is no confirmation step before submitting, since this
  action is non-destructive (it can only apply already-trusted Wikidata
  data, per REQ-513's scope note) and does not need
  `UserDeletionSection`'s "Yes, delete this user permanently" confirm/cancel
  pattern

**Displaying the result:**
- Given a refresh request succeeds
- Then the UI shows all four fields (`FullName`, `Position`, `BirthYear`,
  `PhotoUrl`) from REQ-513's response, each clearly marked as changed or
  unchanged — a changed field shows both its old and new value, an
  unchanged field is visibly distinguished from a changed one (e.g. a
  "Changed"/"Unchanged" label or equivalent styling using
  `design-document.md` §2 tokens only) — this is not satisfied by a single
  generic "success" message or by dumping the raw response as JSON, since
  REQ-513's own stated purpose is giving the admin visibility into exactly
  what changed
- Given a refresh request succeeds and zero of the four fields changed
- Then the UI still shows all four fields as unchanged (with their current
  stored values, per REQ-513's response), not an empty or blank result

**Error states:**
- Given the submitted id does not correspond to an existing `Player`
  (REQ-513's `404`)
- Then the UI shows a message stating the player was not found, not a
  generic error
- Given the `Player` exists but has no `WikidataQid` (REQ-513's `409`)
- Then the UI shows a message stating this player has no Wikidata id to
  refresh from, not a generic error
- Given the Wikidata lookup fails or times out (REQ-513's `503`)
- Then the UI shows a message stating the lookup is unavailable and to try
  again, not a generic error — mirroring how `describeError`/
  `ApiError`-derived messaging is already used elsewhere in this directory
  (e.g. `UserDeletionSection`) rather than introducing a second, separate
  error-formatting convention
- Given the request returns `401`
- Then the same `onAuthError` re-authentication flow every other admin
  section already uses on `401` fires (e.g. `UserDeletionSection`'s
  `handleDeleteConfirmed`), not a section-local error message

**Non-admin/guest access:**
- Given a non-admin or guest reaches `AdminScreen.tsx` directly
- Then this section is not reachable at all — it is part of the same
  `rowsHidden || activeRoundHidden` page-wide access-denied gate every
  other section on this screen already sits behind (REQ-504's
  defense-in-depth), and there is no separate, standalone entry point to
  it anywhere else in the UI

**Out of scope for this REQ:** anything REQ-513 itself scoped out (bulk/
multi-player refresh, a player-browsing/search UI, manual field editing);
any change to REQ-513's backend behavior, response shape, or error
contract; a new authorization policy (this reuses the existing "Admin"
policy REQ-513's endpoint already enforces).

**Test level:** Unit (Vitest/Testing Library, `PlayerRefreshSection.test.tsx`,
matching this directory's existing `*.test.tsx` naming): submitting an id
calls REQ-513's endpoint and shows a pending state; a successful response
with at least one changed field renders all four fields with changed ones
showing old/new values and unchanged ones visibly distinguished; a
successful response with zero changed fields still renders all four fields
as unchanged; each of 404/409/503 renders its own specific message (not a
shared generic one); a 401 response triggers `onAuthError` rather than a
section-local message; the section does not render (or is not reachable)
for a non-admin/guest, consistent with `AdminScreen.test.tsx`'s existing
access-denied coverage.

**REQ-515 – Surface WikidataQid and an inline REQ-513 refresh from admin
player-search/suggestion-lookup results**
*(Status: Implemented. Backend: `ExistingPlayerId` added to
`WikidataPlayerLookupResponse`/`LookupPlayerAsync` (`AdminSuggestionEndpoints.cs`),
covering both `/admin/suggestions/{id}/lookup` and `/admin/player-search/lookup`.
Frontend: `PlayerReviewPanel` (`SuggestionsScreen.tsx`) always shows the
WikidataQid and, when `existingPlayerId` is present, an inline refresh
action reusing the new shared `PlayerRefreshFieldsList` component
(extracted from REQ-514's `PlayerRefreshSection.tsx` to avoid duplicating
its field-diff rendering). Verified locally: `npx tsc -b`, `npm run lint`,
`npm run test` (45 files/663 tests) — all passed. Backend tests written
and manually traced but not compiler-verified in this sandbox (no .NET
SDK); must be confirmed in CI.)*
> As an admin, I want the player I just looked up on Wikidata (via
> suggestion review or manual search) to show its `WikidataQid`, and — when
> a `Player` row already exists locally for that QID — a one-click way to
> run REQ-513's refresh right there, so I don't have to copy an id into the
> separate REQ-514 section to fix a value I can already see is stale.

**Scope note (bridges REQ-509/510's lookup to REQ-513's refresh; no new
backend action):** this REQ adds no new write behavior. It (a) surfaces an
already-fetched value (`WikidataQid`) that REQ-509/510's shared
`LookupPlayerAsync` helper already returns but `PlayerReviewPanel` never
renders, and (b) adds a second call site for REQ-513's existing
`POST /admin/players/{id}/refresh-from-wikidata` endpoint, reusing the same
`refreshPlayerFromWikidata` client function REQ-514's `PlayerRefreshSection`
already calls. No new endpoint, no new authorization policy, no change to
REQ-513's request/response contract or error contract, and no change to
REQ-509/510's commit path. Because `LookupPlayerAsync` is the single shared
helper behind both `/admin/suggestions/{id}/lookup` (REQ-509) and
`/admin/player-search/lookup` (REQ-510), and `PlayerReviewPanel` is the
single shared component both `PendingSuggestionRow` (suggestion review) and
`ManualSearchSection` (standalone search) render, this REQ's acceptance
criteria apply identically in both contexts with no endpoint- or
context-specific carve-out.

**Backend: resolving whether a local `Player` already exists for the found
QID:**
- Given a Wikidata lookup (`/admin/suggestions/{id}/lookup` or
  `/admin/player-search/lookup`) returns `Found = true` with a non-null
  `WikidataQid`
- When the response is built
- Then the system resolves whether a `Player` row already exists for that
  `WikidataQid` (via `IPlayerRepository.GetPlayerByWikidataQidAsync`) and
  includes that `Player`'s id in the response
- Given a Wikidata lookup returns `Found = true` but no local `Player` row
  exists yet for that `WikidataQid`
- Then the response indicates no existing player id (null), the same as the
  `Found = false` case below
- Given a Wikidata lookup returns `Found = false`
- Then the response indicates no existing player id (null), consistent with
  every other field on a `Found = false` response already being null/empty
- Given this new field is added to the single shared
  `WikidataPlayerLookupResponse`/`LookupPlayerAsync` helper
- Then both `/admin/suggestions/{id}/lookup` and `/admin/player-search/lookup`
  include it with no endpoint-specific special-casing — verified by a test
  against each endpoint, not just the shared helper in isolation

**Frontend: showing the QID:**
- Given `PlayerReviewPanel` is in its `found` phase (a successful lookup)
- Then the fetched `WikidataQid` is rendered as visible text somewhere in
  that phase's output — no interactivity or further behavior is required of
  this display beyond being visible to the admin, in both the
  suggestion-review context (`PendingSuggestionRow`) and the standalone
  manual-search context (`ManualSearchSection`)

**Frontend: inline refresh action when a local `Player` already exists:**
- Given `PlayerReviewPanel` is in its `found` phase and the lookup response's
  new existing-player-id field is non-null
- Then an inline "Refresh from Wikidata" control is rendered in that phase's
  output, in addition to (not instead of) the existing commit/reject form
- Given `PlayerReviewPanel` is in its `found` phase and the lookup response's
  existing-player-id field is null (no local `Player` row for this QID yet)
- Then no inline refresh control is rendered — there is no existing `Player`
  row for REQ-513's endpoint to act on
- Given the admin activates the inline refresh control
- Then the UI calls REQ-513's existing endpoint (via the same
  `refreshPlayerFromWikidata` client function `PlayerRefreshSection.tsx`
  already uses, not a duplicate implementation) for the existing player id
  from the lookup response, and disables the control while the request is in
  flight — the same pending-state pattern REQ-514 already establishes,
  applied to this second call site
- Given the refresh request succeeds
- Then all four fields (`FullName`, `Position`, `BirthYear`, `PhotoUrl`) are
  shown with the same per-field changed/unchanged (with old/new values on a
  changed field) presentation REQ-514 already defines for
  `PlayerRefreshSection` — this result display is independent of, and does
  not alter, the separate commit/reject form `PlayerReviewPanel` already
  renders in the `found` phase
- Given this action is not a destructive one (it can only apply
  already-trusted Wikidata data, the same reasoning REQ-514 already
  establishes for its own entry point)
- Then there is no confirmation step before it runs — this is REQ-513's own
  existing action, invoked from a second entry point, not a new action with
  its own confirmation policy

**Error handling:**
- Given the refresh request made from this inline action fails
- Then it is handled with the identical 404/409/503/401 contract REQ-513's
  endpoint already defines and REQ-514 already establishes UI messages for
  (404 "player not found," 409 "no Wikidata id to refresh from," 503 "lookup
  unavailable, try again," 401 triggers the same `onAuthError` re-
  authentication flow `PlayerReviewPanel` already uses on its own lookup/
  commit/reject calls) — this REQ introduces no new error states or
  messages of its own; a 404/409 here would only occur if the local `Player`
  row was deleted or its `WikidataQid` cleared between the lookup and the
  refresh click, an edge case REQ-513's existing contract already covers

**Out of scope for this REQ:** any change to `LookupPlayerAsync`'s Wikidata
query itself, to REQ-513's refresh endpoint/response/error contract, or to
REQ-509/510's commit write path; a player-browsing/search UI beyond what
REQ-509/510 already provide (this REQ only adds visibility/an action to an
already-fetched lookup result, it does not add a new way to find a player);
removing or changing REQ-514's existing standalone `PlayerRefreshSection`
(that entry point remains, for the case where an admin already knows a
`Player` id without having just run a Wikidata lookup for it).

**Test level:** Unit (backend: `ExistingPlayerId`/equivalent field is
present with the correct value when a local `Player` exists for the found
QID, and null both when no local `Player` exists and when `Found = false`
— verified against both `/admin/suggestions/{id}/lookup` and
`/admin/player-search/lookup`, extending `AdminSuggestionEndpointTests.cs`;
frontend: the QID renders as visible text in the `found` phase for both
`PendingSuggestionRow` and `ManualSearchSection` contexts; the inline
refresh control renders only when an existing player id is present and not
otherwise; activating it calls `refreshPlayerFromWikidata` with that id and
shows a pending state; a successful response renders all four fields with
the same changed/unchanged presentation as REQ-514; each of 404/409/503
renders its own specific message and a 401 triggers `onAuthError`, extending
`SuggestionsScreen.test.tsx`). No new API-level test class — this extends
existing coverage for `/admin/suggestions/{id}/lookup`,
`/admin/player-search/lookup`, and REQ-513's endpoint (called from a second
site, not a new one).

---

**REQ-516 – Admin UI grouped navigation**
> As an admin, I want the admin page organized into grouped sections
> (Users, Grid, Path, Announcements, Issues) with a way to switch between
> them, instead of one long scrolling page, so I can find the control I
> need without scanning past unrelated sections.

- Given the admin page (REQ-504) as it exists today — a single vertical
  stack of independent sections (player suggestions entry, incident
  reports entry, announcement banner, unverified data review, account
  metrics, xG Path cycle control, round control, user deletion, and this
  section's own REQ-517 avatar moderation once built)
- When an admin opens the admin page
- Then those sections are grouped and reachable via a persistent
  navigation control (tabs or an equivalent grouped nav), with groups at
  minimum: **Users** (account metrics, user deletion, guest force-clear,
  REQ-517's avatar moderation), **Grid** (unverified data review, player
  suggestions entry, round control), **Path** (xG Path cycle control),
  **Announcements** (the announcement banner), **Issues** (incident
  reports entry) — only one group's sections are visible at a time
- And switching groups does not re-fetch a section's data if it was
  already loaded during this page visit — each section keeps the
  independent fetch/refetch behavior REQ-504/505/507/508/511/512/1209
  already established; this REQ only changes which sections are visible
  at once, not how or when any of them fetch
- And every existing per-section behavior is unchanged: a section absent
  in Production (REQ-505/506's non-Production-only sections) is still
  entirely absent from the DOM, not merely hidden behind an unselected
  tab; a non-admin still gets the page-level "access denied" message
  (REQ-504) before any group or section renders

**Out of scope for this REQ:** any new admin capability, endpoint, or
permission — this is a pure navigation/layout change over sections that
already exist (or, for REQ-517, are being added in the same area);
persisting the admin's last-selected group across a page reload (always
opens to the same default group, left to implementation).

**Test level:** UI (the admin page renders grouped nav instead of one long
scroll; selecting a group shows only that group's sections; the existing
Production-hiding and non-admin access-denied behavior for
REQ-504/505/506 is unchanged under the new nav; a section's own
loading/error/refetch behavior — e.g. `AccountMetricsSection`'s — is
unaffected by switching groups away from and back to it).

---

**REQ-517 – Admin review of pending avatar uploads**
> As an admin, I want to see every player's pending avatar upload with a
> preview image and approve or reject it, so no image becomes visible to
> other players without a human checking it first.

**Reviewing the queue:**
- Given one or more players have an avatar submission in `Pending` status
  (REQ-722)
- When an admin opens the avatar moderation section (grouped under
  REQ-516's "Users" nav group)
- Then they see every pending submission with a preview of the uploaded
  image, the submitting player's `DisplayName`, and the submission time —
  oldest first, matching REQ-509's existing pending-suggestion ordering
  convention

**Approving:**
- Given a pending avatar submission
- When an admin approves it
- Then that submission becomes the player's visible avatar (REQ-722), the
  submission's status becomes `Approved`, and it leaves the pending queue
- And if the same player already had a previously-approved avatar, the
  new one replaces it — a player has at most one visible avatar at a time

**Rejecting:**
- Given a pending avatar submission
- When an admin rejects it
- Then the submission's status becomes `Rejected`, it leaves the pending
  queue, no image becomes visible to anyone but the submitting player
  (who sees their own submission's rejected status, REQ-722), and the
  player's previously-approved avatar if any is unchanged
- And rejecting requires no reason field in v1 — a player who wants to
  know why can only infer it from the fact of rejection, the same minimal
  bar REQ-509's suggestion rejection already sets

**Authorization and notification:**
- Given a request to list, approve, or reject avatar submissions
- When the caller is not authenticated, or is authenticated but not in
  the `Admin:UserIds` allowlist
- Then it is rejected with `401`/`403` respectively — the same "Admin"
  policy every other admin endpoint in §4.5 already uses
- Given at least one submission is pending
- Then the admin page shows a pending-count badge next to this section's
  own heading — this section renders inline under REQ-516's "Users" group
  rather than behind a separate click-through entry point, so the badge
  sits on the heading itself rather than on a nav entry — mirroring
  REQ-512's existing "(N)"/no-"(0)" pending-count convention for player
  suggestions; no separate REQ is needed for the badge itself, it reuses
  that convention directly

**Out of scope for this REQ:** automated image content scanning (human
review via this queue is the only moderation mechanism for v1); a reason/
comment field on rejection; re-review of an already-decided submission (a
rejected or approved submission is terminal — a player must submit a new
image to try again, per REQ-722).

**Test level:** Unit (approving replaces any prior approved avatar and
clears pending status; rejecting leaves a prior approved avatar
untouched). API (`GET /admin/avatar-submissions` returns only `Pending`
rows oldest-first, 401/403 under the Admin policy; approve/reject
transition status correctly and reject acting twice on an already-decided
submission with a clear error, not a silent success). UI (the moderation
section renders image previews; approving/rejecting removes the row from
the pending list; the pending-count badge matches the number of rows
returned).

**Status note (2026-08-24, S-181 — backend built):** `GET
/admin/avatar-submissions`/`POST .../{id}/approve`/`POST .../{id}/reject`
(`XGArcade.Api.Admin.AdminAvatarEndpoints`) are built, mirroring
REQ-509's `AdminSuggestionEndpoints` list/act-on-one-by-id/terminal-
state-409 shape, under the same `"Admin"` policy. `IAvatarStorage` gained
`GetPreviewUrlAsync` (ADR-0087's own anticipated Follow-up) to resolve
the "image preview" criterion — a short-lived (5 min) Supabase Storage
signed URL, generated server-side per request. Approving supersedes any
prior `Approved` row for the same player by deleting it in the same
write (no new `AvatarSubmissionStatus` member added) and best-effort
deletes its now-orphaned image; rejecting never touches a prior
`Approved` row. Race-safety (acting twice on an already-decided
submission → 409, not a silent success) is enforced at the repository
level (`IAvatarSubmissionRepository.ApproveAsync`/`RejectAsync` re-check
`Status==Pending` inside the same tracked load before writing), not just
in the endpoint. The "pending-count badge" criterion and every UI
criterion were built subsequently by S-183 — see the status note below.
Built without a local `dotnet` SDK in-sandbox — hand-traced against
`AdminSuggestionEndpointTests`/`AvatarEndpointTests`'s existing patterns;
CI verification pending as of this note.

**Status note (2026-08-24, S-183 — frontend built, all acceptance criteria
now satisfied):** a new `AvatarModerationSection.tsx`
(`frontend/src/admin/`) consumes S-181's three endpoints
(`fetchPendingAvatarSubmissions`/`approveAvatarSubmission`/
`rejectAvatarSubmission`, `frontend/src/lib/admin.ts`, and the new
`PendingAvatarSubmission` type in `frontend/src/lib/types.ts`) and renders
unconditionally (registered in every environment, not gated behind the
Non-Production-only `activeRound` probe `UserDeletionSection`/
`RoundControlSection` share) inline in `AdminScreen.tsx`'s "Users" group
(REQ-516), immediately below `AccountMetricsSection`. "Reviewing the
queue" is satisfied: every pending submission lists an image preview
(`<img src={imagePreviewUrl}>` — S-181's already-resolved, short-lived
signed URL, never a storage key resolved client-side), the submitting
player's `DisplayName` (falling back to "a deleted user" when null, per
REQ-710, matching `SuggestionsScreen`'s `PendingSuggestionRow` convention
exactly), and the submission time, oldest first (the backend's own
ordering; this UI never re-sorts). Approve/reject are per-row actions with
per-row action/error state (not a single panel-wide state), disabling only
the acting row's own buttons while in flight. A `409` (already resolved by
another admin) is tracked separately from a validation/network error and
renders its own "Already resolved by another admin" message plus a
"Refresh list" action, rather than looking like a random failure — the
same distinct-conflict-state approach `SuggestionsScreen`'s
`PlayerReviewPanel` already established, not a new pattern. The
pending-count badge criterion is satisfied by an "Avatar moderation (N)"
heading badge — mirroring `UnverifiedDataSection`'s own inline heading-
badge convention (not `PlayerSuggestionsEntry`'s button-label badge, since
this section has no separate click-through entry point) — with REQ-512's
existing "absence not a 0 badge" convention applied: a count of 0 omits
the "(N)" suffix but the section itself still renders, with an empty-state
message. No new visual token: the only new CSS
(`.admin-screen__avatar-row-summary`/`.admin-screen__avatar-preview`, a
64px rounded image thumbnail) reuses existing spacing/color tokens and the
8px radius already established elsewhere in this file. Verified with
`AvatarModerationSection.test.tsx` (queue rendering with previews,
approve/reject removing a row, the pending-count badge matching row count
and omitting "(0)", the 409-conflict row state, 401 routing to
`onAuthError`) and an extension of `AdminScreen.test.tsx` (confirms the
section renders only inside the "Users" group — visible on that tab,
hidden and not re-fetched on others, with no separate top-level nav tab
of its own). 689/689 frontend tests passing; `tsc -b` and lint both
clean. No backend changes — S-181's endpoints are untouched.
`architecture-reviewer`: PASS, no ADR needed (pure frontend consumption of
an already-documented COMP-14 endpoint, no component/data-flow boundary
change). `quality-architect`: PASS, after one wording-drift finding (this
REQ's own badge-placement bullet still said "nav entry"; fixed in a
separate commit on the same branch, reflected above). CI verification via
a `ci.yml` `workflow_dispatch` run was pending as of this note.

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

**Captcha requirement for signup and login (2026-07-25 addition — now
built, same day; see ADR-0037's amendment and REQ-717's matching
scope-correction addition for the full mechanism):**

- Given the account-creation form (this REQ) or the log-in form (no
  dedicated REQ of its own yet; recorded here since this is where
  email/password authentication's rules already live)
- When a person submits either one
- Then a valid Cloudflare Turnstile token, obtained by the frontend before
  the respective endpoint is called, is required before Supabase Auth is
  called — mirroring REQ-717's guest-flow captcha mechanism exactly, not a
  separately-designed check (see REQ-717 for the full Given/When/Then and
  ADR-0037 for the wiring)
- And a missing, expired, or invalid token produces a distinct rejection
  the frontend can act on — for signup, this rejection must not be
  swallowed by this REQ's own account-enumeration-safe generic fallback
  message above (that message stays exactly as specified for every other
  signup-rejection reason; only a captcha rejection is carved out from it)
- **Correction (verified against the shipped code, 2026-07-25):** an
  earlier version of this bullet stated the requirement "holds regardless
  of whether Supabase's captcha protection setting happens to be enabled
  ... when it is disabled, no token is required." That was never accurate
  and has been corrected here: this backend has no way to observe
  Supabase's project-wide "Enable Captcha Protection" dashboard toggle at
  request time, so `AuthController.Signup`/`AuthController.Login` require
  a non-empty token unconditionally, on every request, regardless of that
  toggle's state — there is no code path where a missing token is
  accepted. Supabase's own toggle is what determines whether the token is
  actually *verified* against Cloudflare on Supabase's side; this
  backend's own requirement that a token be present at all is not gated on
  it.

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
- **Status note (2026-07-21): both permit counts are configurable**
  (`RateLimiting:AuthSignupPermitLimit`/`AuthLoginPermitLimit`, default 10,
  unchanged), added after the real 10/min production value started
  rejecting `ci.yml`'s own E2E job — one Playwright suite's full
  signup+auto-login traffic across every spec file lands on one backend
  process from the single CI-runner IP within the same window, a
  fundamentally different shape than the abuse case this REQ targets.
  `ci.yml`'s E2E step overrides both to 1000 for that job only; every other
  environment, including local dev, keeps the real 10 default.
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
  unchanged. **Extended again in S-088:** a second, parallel endpoint,
  `POST /internal/test-data/seed-guessable-path-round`
  (same `XGArcade.Api.Rounds.InternalRoundEndpoints` file), was added for
  xG Path's E2E coverage — same non-Production gate, same
  repository-only write discipline (ADR-0006 boundary rule 4), just
  against `IPathInstanceRepository` instead of `IGridInstanceRepository`.
  It creates a `Player` with three chronologically distinct
  `PlayerCareerStint` rows (enough content for
  `PathClueSequenceBuilder`'s three club-reveal turns), a `PathInstance`
  with one `PathPuzzle` targeting that player, and an active `Round`
  referencing that instance — bypassing `XGPathGameModule
  .GenerateInstanceAsync` entirely, so REQ-1201's seeded-club/appearance-
  count eligibility rules never apply to rows created this way, same as
  the grid endpoint above already bypasses xG Grid's own generation-time
  eligibility logic. The acceptance criteria below now cover both
  endpoints; this REQ's original text ("only grid/round content is seeded
  this way") predated the second game and is corrected below.
- Given `ASPNETCORE_ENVIRONMENT` is not `Production`
- When a test calls `POST /internal/test-data/seed-guessable-round`
- Then an active Round and a single-cell `GridInstance` are created, together
  with one `Player` whose `PlayerAttribute` rows satisfy that cell's row and
  column categories
- And the response returns the created round id, cell id, and the exact
  correct player name (`RoundId`/`CellId`/`CorrectPlayerName`, plus
  `AlternateCorrectPlayerName` per the S-011 extension above), so a test can
  deterministically submit both a correct and an incorrect guess
- Given `ASPNETCORE_ENVIRONMENT` is not `Production`
- When a test calls `POST /internal/test-data/seed-guessable-path-round`
- Then an active Round and a single-puzzle `PathInstance` are created,
  together with one `Player` whose `PlayerCareerStint` rows give
  `PathClueSequenceBuilder` real content for all clue-reveal turns
- And the response returns `RoundId`/`PuzzleId`/`CorrectPlayerName` —
  `PuzzleId` is the "cell id" a test submits guesses against via the
  existing game-agnostic `POST /rounds/{roundId}/cells/{cellId}/guesses`,
  per `IGameModule.GetCellIdsAsync`'s PathPuzzle.Id-is-the-cell-id contract
- And both endpoints above are never registered when
  `ASPNETCORE_ENVIRONMENT == Production`, enforced in startup
  configuration, same discipline as REQ-801/REQ-806
- And test users are still created via the real signup endpoint (REQ-806's
  existing convention) for both endpoints — only grid/round or
  xg-path/round content is seeded this way, never user accounts

**Test level:** Integration (both endpoints absent when Production), used
as E2E setup by S-010's Playwright suite (`seed-guessable-round`) and
S-088's Playwright suite (`seed-guessable-path-round`)

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

**Captcha requirement for the password re-confirmation step (2026-07-25
addition — now built, same day; see ADR-0037's second amendment for the
full mechanism and why this fourth call site exists):**

- Given the password re-confirmation field on `DeleteAccountScreen`
- When a logged-in user submits their current password to confirm account
  deletion (this REQ's existing confirmation step, `DeleteAccountScreen`'s
  password field)
- Then a valid Cloudflare Turnstile token, obtained by the frontend before
  `DELETE /auth/account` is called, is required before the password
  re-verification call (`ISupabaseAuthClient.SignInWithPasswordAsync`, the
  same call `Login` uses) is made — mirroring REQ-717's guest-flow captcha
  mechanism exactly, the same mechanism REQ-701's 2026-07-25 addition
  already applies to signup and login; see REQ-717 for the full
  Given/When/Then and ADR-0037 for the wiring, not re-derived here
- And a missing, expired, or invalid token produces a distinct rejection
  the frontend can act on, so it can reset the Turnstile widget and obtain
  a fresh token before allowing another attempt — never a silent retry
  re-using the same rejected or expired token
- And this distinct captcha-rejection response's title (e.g. `"Captcha
  verification failed"`, the same title the guest/signup/login flows
  already use per REQ-717/REQ-701) **must not collide with
  `DeleteAccountScreen.tsx`'s existing string-match on the `"Incorrect
  password"` response title** — that existing match is what the screen
  uses to distinguish a wrong password (shown as an inline error, nothing
  deleted) from a 401 caused by an expired/invalid JWT (which signs the
  user out instead, per this REQ's S-039 "Built as" note above); a captcha
  rejection must be distinguishable from both of those existing outcomes,
  not merged into either
- **Correction (verified against the shipped code, 2026-07-25):** an
  earlier version of this bullet stated the requirement "holds regardless
  of whether Supabase's captcha protection setting happens to be enabled
  ... when it is disabled, no token is required." That was never accurate
  and has been corrected here — see REQ-701's matching correction for the
  full explanation, which applies identically: this backend has no way to
  observe Supabase's dashboard toggle at request time, so
  `AuthController.DeleteAccount` requires a non-empty token
  unconditionally, on every request

**Test level:** Unit (anonymization logic specifically — verify no
reversible link remains), API (a missing/invalid Turnstile token on
`DELETE /auth/account` produces a distinct captcha-rejection response,
distinguishable from both the wrong-password and JWT-expiry 401 outcomes —
exercised with Cloudflare's documented always-pass/always-fail test site
keys, not a live network call, the same way REQ-717's own captcha tests
avoid live third-party calls), UI (`frontend/src/auth/DeleteAccountScreen.test.tsx`,
`frontend/tests/unit/App.test.tsx` — including a case confirming the
captcha-rejection title does not trigger the screen's existing
"Incorrect password" inline-error branch or its JWT-expiry logout branch)

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
- **Status note (2026-08-25 — product decision, reverses this REQ's
  original unrestricted scope; not yet implemented):** a guest account
  (`User.IsGuest = true`) can no longer edit their display name from
  Settings. This REQ originally had no guest exclusion at all — `PUT
  /auth/display-name` (`AuthController.UpdateDisplayName`) has no
  `IsGuest` check today — and REQ-717's own "Display name" sub-section
  explicitly said a guest could use this mechanism "exactly as any other
  account can." The product owner (johan.pearson) reversed that directly,
  in this Settings-redesign session: a guest must claim their account
  (`POST /auth/claim`, REQ-717) before editing their display name. See
  the new "Guest exclusion" criterion below for the required enforcement
  shape, and REQ-717's own dated status note under its "Display name"
  sub-section for the corresponding correction there. This is a narrow
  business-rule change, following the same plain `IsGuest` gate
  REQ-215/REQ-903 already use for their own guest-exclusion paths
  elsewhere — not a new architectural pattern, so no ADR is needed.
- **Status note (2026-08-25 — follow-up: now implemented, S-185):** the
  guest exclusion described immediately above is built.
  `AuthController.UpdateDisplayName` (`PUT /auth/display-name`) now
  returns a `403` with claim-first guidance for any `IsGuest = true`
  caller, checked before the length-bound validation. The 403 plumbing is
  shared with REQ-722's identical avatar-upload check (and REQ-215/
  REQ-903's pre-existing ones) via a new
  `backend/src/XGArcade.Api/Auth/GuestRejectionProblem.cs` helper. Covered
  by `REQ714_UpdateDisplayName_Returns403_WhenCallerIsGuest`
  (`AuthEndpointTests.cs`). See `docs/backlog.md` S-185 for the full build
  record, including the frontend pencil-icon panel redesign built in the
  same story.
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

**Guest exclusion (added 2026-08-25):**
- Given a logged-in GUEST account (`User.IsGuest = true`, REQ-717)
- When they attempt to edit their display name (`PUT /auth/display-name`)
- Then the request is rejected with a `403`, enforced server-side
  regardless of what the client sends — same boundary rule REQ-215/
  REQ-903 already establish for their own guest-exclusion paths
- And the rejection tells the guest why, not a generic error — that they
  must claim their account first (`POST /auth/claim`, REQ-717) before
  they can edit their display name — the same "server's own detail text
  shown inline" convention this Settings screen already uses elsewhere
  (this REQ's own display-name conflict error, REQ-722's avatar upload
  limit error)
- And a non-guest (claimed) account is completely unaffected by this
  criterion — the acceptance criteria above apply to it unmodified

**Test level:** Unit (uniqueness check excludes the account's own row;
length validation), API (a guest account, `IsGuest = true`, receives a
`403` with claim-account guidance; a non-guest account is unaffected), UI
(Settings screen edit form; conflict error shown inline, not a generic
failure; a guest sees the claim-first guidance inline, not a generic
error)

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

**REQ-717 – Guest play (auto-provisioned identity, no email/password
required)**

- **Status: Implemented (backend), 2026-07-21 — ADR-0036 (auth mechanism).**
  `POST /auth/guest` provisions a real `User` row (`IsGuest = true`,
  `Email = null`, an auto-generated `Guest####`-style `DisplayName`) via a
  backend-mediated Supabase Anonymous Sign-in (`ISupabaseAuthClient.
  SignInAnonymouslyAsync`), auto-enrolled in the Global league exactly like
  any other signup. `POST /auth/claim` is the claim/upgrade path
  (`ISupabaseAuthClient.LinkEmailPasswordAsync` + `IUserRepository.
  ClaimGuestAsync`): sets `Email`, clears `IsGuest`, stamps `ClaimedAt`, and
  touches no `Guess`/`LeagueMembership` row. `LeaderboardService`/
  `GuessRepository.GetPerRoundFinalPointsByUserIdsAsync` (REQ-409's
  qualifying-rounds query) excludes `IsGuest` rows outright and excludes a
  claimed account's rounds closed before `ClaimedAt`. A new `auth-guest`
  rate-limit policy (3/min per IP by default, tighter than auth-signup/
  auth-login's 10/min) gates guest creation.
- **Status: Implemented (frontend), 2026-07-21 (S-070).** `AuthScreen.tsx`
  gained a "Play as guest" entry point (calls `POST /auth/guest` and routes
  through the exact same success path a normal login/signup already uses —
  no separate "guest mode" client-side state). `SettingsScreen.tsx` gained
  a "Save your progress" claim section, visible only while the account is a
  guest, calling `POST /auth/claim`. `App.tsx` also added a small header
  banner nudging a guest toward that claim section — a UX addition beyond
  this REQ's own acceptance criteria, documented in `design-document.md`.
  **Gap since closed (2026-07-21 follow-up, same day):** this note
  originally flagged that the backend's `MeResponse` DTO had no dedicated
  `isGuest` field, and that the frontend derived guest status as
  `email === null` instead. A same-day backend follow-up added
  `MeResponse.IsGuest` (mirroring `User.IsGuest` directly), and a matching
  frontend follow-up switched `AuthScreen.tsx`/`SettingsScreen.tsx`/
  `App.tsx` over to that real field, removing the `email === null`
  inference entirely. See `docs/backlog.md`'s S-070 entry for the
  full before/after.

**Tier framing:** Tier 1/2 by `MVP-SCOPE.md`'s own classification (a new
auth flow that touches the account boundary Tier 0 already locked in) —
pulled forward by explicit product decision, same pattern as
REQ-108/214/402-403's own precedent (each pulled forward ahead of its
trigger firing, by deliberate choice, not because a trigger fired).
**Unlike those three, this was not an existing Tier 1 bullet being pulled
forward** — `MVP-SCOPE.md`'s Tier 1 list had no "guest play" trigger before
this; `MVP-SCOPE.md`'s "Guest play" bullet now records the pull-forward
decision itself (added in the same session ADR-0036 was drafted).

> As a person who wants to try the game before committing to an account, I
> want to play immediately without providing an email or password, so I
> can experience a round with zero signup friction.

- **Scope note:** the auth mechanism itself (e.g. Supabase Anonymous
  Sign-ins, token issuance, session handling) is ADR-0036's concern, being
  drafted in parallel — this requirement describes observable behavior
  only, never how the identity is technically minted.

**Guest identity:**
- Given a person chooses to play as a guest
- When the guest identity is provisioned
- Then a real `User` row is created with no email and no password set, and
  a durable flag distinguishing it as a guest (the exact field/column is
  an implementation detail, not part of this requirement — `User.IsGuest`
  is assumed only as the minimum signal REQ-409's exclusion below needs to
  exist)
- And REQ-702's "unconfirmed accounts cannot play" rule does not apply to
  this row — a guest is a self-contained identity kind with no
  confirmation step, not an unconfirmed ordinary signup
- And this `User` row participates in REQ-201–210 (submitting, locking,
  and scoring guesses), REQ-204 (live uniqueness), and REQ-406/407/408
  (round-scoped leaderboards) completely unmodified, through the same
  `LeagueMembership` mechanism REQ-401 already grants every new account in
  the Global league — none of those requirements gain a new code path,
  query, or guest-specific branch as a result of this requirement

**Display name:**
- Given a new guest identity with no display name supplied
- When it is provisioned
- Then a default display name is auto-generated (e.g. `Guest8317`-style),
  satisfying REQ-701's existing 1-30 character bound and case-insensitive
  uniqueness check — a generation collision is retried with a new random
  suffix, the same way any other conflicting write is retried elsewhere in
  this system
- And REQ-714's existing display-name-edit mechanism applies completely
  unmodified — a guest can set a real display name from Settings exactly
  as any other account can, with no second, guest-specific edit path
- **Status note (2026-08-25, direct product-owner decision, johan.pearson,
  this Settings-redesign session — corrects the criterion immediately
  above):** the sentence above is superseded and no longer accurate. A
  guest account (`IsGuest = true`) can no longer edit their display name
  from Settings at all — they must claim their account first (`POST
  /auth/claim`, this REQ's own claim mechanism, described above under
  "Guest identity") before REQ-714's edit mechanism becomes available to
  them. See REQ-714's own new "Guest exclusion" criterion for the
  enforcement shape: a server-side `403` on `PUT /auth/display-name` for
  any `IsGuest = true` caller, with the rejection telling the guest why
  (claim first) rather than a generic error. Nothing else in this
  "Display name" sub-section is affected — the auto-generated default
  name on provisioning, and its 1-30 character bound, case-insensitive
  uniqueness check, and collision-retry behavior, are all unchanged; only
  the ability to *edit* it before claiming is removed. This is a narrow
  business-rule reversal, following the same plain `IsGuest` gate
  REQ-215/REQ-903 already use for their own guest-exclusion paths
  elsewhere — not a new architectural pattern, so no ADR is needed here.

**Scoring and uniqueness — no special-casing:**
- Given a guest has submitted a guess
- Then that guess counts fully and normally toward REQ-204's live
  uniqueness calculation and REQ-206's per-round total, exactly as any
  other player's guess — never excluded, weighted differently, or flagged
  in either calculation (more real guesses, including guests', is the
  entire point of this requirement — a better uniqueness signal, not a
  stripped-down guest experience)

**Leaderboard participation (the core split):**
- Given a guest `User` is a member of the Global league (REQ-401)
- When REQ-407's currently-active-round leaderboard, or REQ-408's
  past-round-browsing leaderboard, is requested
- Then the guest appears ranked exactly like any other participant, via
  the same ordinary `LeagueMembership` row any account has — no new query
  logic for either of these two requirements
- Given the same guest `User`
- When REQ-409's all-time, median-ranked leaderboard's qualifying-rounds
  query runs
- Then the guest is excluded from that ranking entirely, regardless of how
  many qualifying rounds they've accumulated — via a check on the guest
  flag added to REQ-409's existing qualifying-rounds query
- And the reason is two-fold, both stated because the second is the real
  reason even though the first is also true in practice: (a) a guest
  rarely accumulates REQ-409's 5-round qualification floor before
  abandoning the session, so the exclusion is often moot in practice; (b)
  REQ-409's median is meant to measure one consistent identity's
  performance over time, and a guest identity has no guaranteed persistent
  login across sessions — folding guest-era history into that measure
  would be measuring something incoherent (an identity not reliably "the
  same person" returning), not merely a rarely-populated case

**Rate limiting for guest creation:**
- Given the guest-creation endpoint
- Then it is protected by its own rate-limiting rule, distinct from and
  tighter than REQ-606's existing `auth-signup`/`auth-login` policies — a
  guest flow has even less friction than email signup (no email address at
  all), making it a more attractive target for spinning up many identities
  to probe a cell's answer or to inflate/manipulate a cell's uniqueness
  denominator (REQ-204) than either existing endpoint
- And exceeding the limit is rejected the same way REQ-606's existing
  limits reject (a clear 429, no queueing) — never silently degraded or
  allowed through
- And the exact numeric threshold is left unresolved here, the same way
  REQ-606's own thresholds are resolved elsewhere (§5) rather than fixed
  by the requirement itself — an implementation/tuning detail, not a
  product decision this requirement needs to make

**Bot-check (captcha) for guest creation (2026-07-21 addition; backend
pass-through implemented 2026-07-22 per ADR-0037; frontend Turnstile
widget/token-acquisition implemented 2026-07-22, same day):**
`frontend/src/lib/turnstile.ts` loads Cloudflare's script once and exposes
`getTurnstileToken()`/`resetTurnstileWidget()`; `AuthScreen.tsx`'s "Play as
guest" calls `getTurnstileToken()` before ever calling `playAsGuest()`
(`lib/api.ts`, now `POST`ing `{ captchaToken }` as its request body), and
branches on the backend's distinct `"Captcha verification failed"`
`ApiError.title` to call `resetTurnstileWidget()` — any other guest-sign-in
failure shows the same generic inline error as before, with no widget
reset. Complementary to, not a replacement for,
the rate-limiting criteria immediately above — a per-IP rate limit alone is
weaker against a distributed/multi-IP scripted attacker than a captcha
check is, which is exactly the abuse pattern Supabase's own dashboard warns
about when enabling Anonymous Sign-ins ("Enable captcha for anonymous
sign-ins — this will prevent potential abuse on sign-ins which may bloat
your database and incur costs for monthly active users (MAU)"). Both
layers apply together; neither supersedes the other. Mechanism: Cloudflare
Turnstile — see ADR-0037 for the provider choice and exact wiring into
Supabase Auth's native captcha-token verification.

**Scope correction (2026-07-25 addition — supersedes the "Scoped to guest
creation... only" line above):** the line immediately above originally
read "Scoped to guest creation (`POST /auth/guest`) only — this does not
extend to `POST /auth/signup` or `POST /auth/login`." That scoping was a
mistaken assumption, now confirmed wrong against a real Supabase project
(see `NOTES.md`'s 2026-07-25 entry and ADR-0037's matching amendment):
Supabase's "Enable Captcha Protection" dashboard toggle is a single
project-wide setting covering every `gotrue` endpoint that authenticates
or creates an identity, not one this project can enable for guest
creation alone. Enabling it (per `SETUP.md` step 6) to satisfy this REQ's
own acceptance criteria below silently broke real password-based login
and signup, since neither endpoint sent a captcha token at all.

Captcha now applies to every identity-creating/authenticating endpoint
this backend exposes: `POST /auth/guest`, `POST /auth/signup`, and
`POST /auth/login` — that is this REQ's own scope, described below. (A
separate, fourth call site, `DELETE /auth/account`'s password
re-confirmation step, gained the identical captcha requirement per
REQ-710's 2026-07-25 addition — it is not itself an identity-creating/
authenticating endpoint, so its acceptance criteria live there rather than
being duplicated here. This paragraph deliberately does not pin an exact
count going forward — see ADR-0037 for the authoritative, maintained list
of every call site this captcha check currently covers, so a future
additional call site doesn't require the same repeated correction here.)
Each of the three endpoints in this REQ's own scope requires a valid
Turnstile token before the endpoint calls Supabase Auth at all, and each
returns the same kind of distinct, specific rejection (e.g. a
`"Captcha verification failed"`-style response, distinguishable from that
endpoint's other failure modes) on a missing, expired, or invalid token,
so the frontend can reset the Turnstile widget and obtain a fresh token
before retrying — exactly mirroring the acceptance criteria already stated
below for the guest flow, applied identically to signup and login. This
also means `AuthController.Signup`'s REQ-701 account-enumeration-safe
generic fallback message must not be the response returned for a captcha
rejection specifically — that message remains correct for every other
signup-rejection reason (REQ-701 is unchanged there), but a captcha
rejection needs to be distinguished from it first, the same way it must be
distinguished from `POST /auth/guest`'s pre-existing generic
`"Guest sign-in failed"` response. REQ-606's own existing rate limits on
`auth-signup`/`auth-login` are unaffected and unchanged by this
correction — captcha and rate limiting remain independent, additive
layers on every endpoint they both apply to, per this REQ's "Rate limiting
for guest creation" acceptance criteria above. See REQ-701 for signup's
own acceptance-criteria line recording this, REQ-710 for the fourth
(account-deletion re-confirmation) call site, and ADR-0037 for the amended
wiring decision.

- Given the "Play as guest" entry point
- When a person activates it
- Then the frontend first attempts to obtain a Cloudflare Turnstile token
  (via Turnstile's client-side widget/JS) before calling `POST /auth/guest`
  at all — the endpoint is never called without first attempting to
  obtain a token
- Given the frontend has obtained a Turnstile token
- When it calls `POST /auth/guest`
- Then the request includes that token, and the backend passes it through
  unmodified to Supabase Auth's anonymous sign-in call, for Supabase's own
  server-side verification against Cloudflare — this backend performs no
  independent captcha verification of its own, the same "mediate, don't
  reimplement" principle ADR-0013 already established for signup/login
- Given `POST /auth/guest` is called with a missing, expired, or otherwise
  invalid Turnstile token
- When Supabase's anonymous sign-in call rejects the request for that
  reason
- Then the response is a distinct, specific rejection the frontend can act
  on — never the same generic "Guest sign-in failed" response this
  endpoint already returns for its other failure modes (e.g. display-name
  generation exhausted) — so the frontend can tell "the captcha check
  failed, reset the widget and retry" apart from any other failure
- Given the frontend receives that distinct captcha-rejection response
- Then it resets/reinitializes the Turnstile widget and obtains a fresh
  token before allowing another guest-creation attempt — never a silent
  retry re-using the same rejected or expired token

**Signup and login (2026-07-25 addition) — identical structure, applied to
the other two identity endpoints:**

- Given the account-creation form or the log-in form
- When a person submits either one
- Then the frontend first attempts to obtain a Cloudflare Turnstile token
  before calling `POST /auth/signup` or `POST /auth/login` respectively —
  neither endpoint is ever called without first attempting to obtain a
  token, mirroring the guest flow above exactly
- Given the frontend has obtained a Turnstile token
- When it calls `POST /auth/signup` or `POST /auth/login`
- Then the request includes that token, and the backend passes it through
  unmodified to Supabase Auth's signup/password sign-in call
  respectively, for Supabase's own server-side verification against
  Cloudflare — this backend performs no independent captcha verification
  of its own, on either endpoint
- Given `POST /auth/signup` or `POST /auth/login` is called with a
  missing, expired, or otherwise invalid Turnstile token
- When Supabase's corresponding call rejects the request for that reason
- Then the response is a distinct, specific rejection the frontend can act
  on — for signup, never the same generic account-enumeration-safe
  fallback message this endpoint already returns for other signup
  rejections (that message is unchanged for every non-captcha rejection
  reason — see REQ-701); for login, never whatever generic failure
  response this endpoint already returns for other rejections (e.g. wrong
  password) — on both, distinguishable enough that the frontend can tell
  "the captcha check failed, reset the widget and retry" apart from any
  other failure
- Given the frontend receives that distinct captcha-rejection response
  from either endpoint
- Then it resets/reinitializes the Turnstile widget and obtains a fresh
  token before allowing another attempt on that same form — never a
  silent retry re-using the same rejected or expired token

**Widget UX recommendation (superseded 2026-07-25 — see below):**
Turnstile's invisible/managed mode (no visible checkbox interaction
required unless Cloudflare's own risk scoring escalates to an interactive
challenge) was originally recommended over the always-shown checkbox
widget — consistent with "Play as guest" being a zero-friction entry
point by design (this REQ's own user story above); an always-visible
checkbox would reintroduce, for the overwhelming majority of legitimate
players, exactly the friction guest play exists to remove. The same
default was recommended for signup and login (2026-07-25 addition) for
consistency and the same minimal-friction reasoning, even though those two
flows already involve more friction than guest play (an email/password
form to fill in either way) — this was a recommendation, not a hard
acceptance criterion, exactly as it was for guest above.

**Widget UX recommendation, corrected (2026-07-25, sign-in latency
investigation — ADR-0037's third amendment):** reversed to an
**always-visible checkbox** (`size: 'normal'`) on all four call sites
(guest, signup, login, account-deletion re-confirmation), decided
directly by the product owner after a live investigation
(NOTES.md/infra/README.md's 2026-07-25 entries) found the invisible
widget gave no feedback at all while verifying — reported as
indistinguishable from the app being stuck — and that an invisible-type
Turnstile site has no interactive fallback if Cloudflare's risk scoring
is ever unsure, unlike a visible checkbox. Still a recommendation on the
widget's visual mode, not a hard acceptance criterion about *whether* a
token is required (that remains the acceptance criteria above, unchanged)
— but this project's own frontend (`frontend/src/lib/turnstile.ts`) now
implements the visible-checkbox version, not the invisible one this
section originally described.

**External precondition (not application behavior — recorded here for
traceability; full steps belong in `SETUP.md`):** a Cloudflare Turnstile
site must be created (free) before any of the above can function, yielding
a site key (public, safe in frontend code) and a secret key. The secret key
is configured directly in Supabase's own Auth settings dashboard, never in
this application's backend or frontend — Supabase verifies the token with
Cloudflare directly, not through this backend.

**Claim/upgrade path:**
- Given a guest wants to add an email and password
- When they complete the claim/upgrade flow (the UI/flow itself is left to
  a future story; the auth-provider mechanics are ADR-0036's concern)
- Then the same `User.Id` row gains an email and password — a conversion
  of the existing identity, never the creation of a second, disconnected
  `User` row
- And every `Guess` row already attributed to that `User.Id`, and every
  `LeagueMembership` row already held by it, remains attributed to it
  unchanged — no re-linking, no anonymize-and-recreate step. Contrast
  REQ-710's anonymize-not-delete precedent, the closest existing analogue
  for "an identity transition must preserve the historical link" — that
  requirement severs a `Guess` row's link to a `User` on account deletion;
  this is the opposite direction (gaining a durable identity, not losing
  one), so guess history stays fully attached throughout, never severed
- And the guest flag clears at the moment of claiming — from that point
  on the account is indistinguishable from one that signed up with
  email/password from the start, for every purpose except the
  qualifying-rounds rule below
- And a claimed account's rounds closed **before** the claim moment do
  **not** retroactively count toward REQ-409's 5-round qualification floor
  or its median — only rounds closed after claiming are qualifying rounds
  for REQ-409's purposes. This is a deliberate recommendation, not a
  default left to chance: without this rule, a player could guest-play
  extensively, then claim an account moments before a competitive event
  and instantly qualify for (and potentially top) the all-time median
  leaderboard off guest-era rounds where their identity was never durable
  in the first place — the same identity-coherence argument above applies
  just as much to a newly-claimed account's guest-era history as it does
  to an unclaimed guest's

**Test level:** Unit (guest provisioning produces the correct guest flag
with no email/password; default display-name generation collision-retries
and satisfies REQ-701's bounds; REQ-409's qualifying-rounds query excludes
guest rows and excludes a claimed account's pre-claim rounds), API (guest
creation endpoint is rate-limited distinctly from `auth-signup`/
`auth-login`; REQ-407/408 leaderboard responses include a guest row;
REQ-409's response excludes one; on each of `POST /auth/guest`,
`POST /auth/signup`, and `POST /auth/login`, a missing/invalid Turnstile
token produces that endpoint's own distinct captcha-rejection response,
distinguishable from its other failure responses — required by the
2026-07-21 addition above (guest) and its 2026-07-25 scope-correction
addition (signup, login) — exercised with Cloudflare's documented
always-pass/always-fail test site keys, not a live network call to
Cloudflare, the same way automated tests avoid live third-party calls
elsewhere in this system), Manual (spot-check that a claimed account's
guess history and league memberships survive the conversion unchanged;
the "Play as guest", account-creation, and log-in flows end-to-end with
the always-visible checkbox widget against a real Cloudflare Turnstile
site — see this REQ's corrected Widget UX recommendation above)

**REQ-718 – Guest account lifecycle cleanup (logout deletion, unclaimed
purge, inactive purge)**
> As the platform operator, I want unclaimed and inactive guest accounts
> (REQ-717) removed automatically, so guest play doesn't leave an
> unbounded, ever-growing set of throwaway accounts behind with no
> corresponding real person.

**Scope note:** this requirement only ever removes an account with
`User.IsGuest = true` at the moment a rule below fires. Claiming a guest
account (`POST /auth/claim`, REQ-717) clears `IsGuest` to `false` at the
moment of claiming (ADR-0036) — from that point on the account is a real
account and is never eligible for any of the three rules below, regardless
of how old `CreatedAt` or `ClaimedAt` is. This requirement introduces no
new automatic deletion of any kind for non-guest accounts — those remain
governed solely by REQ-710 (an explicit, user- or admin-initiated
deletion).

**Mechanism note:** all three rules below remove a qualifying account
through the exact same anonymize-and-keep-`Guess`-rows mechanism REQ-710
already defines (sever the `Guess.UserId` link rather than deleting the
row, remove `LeagueMembership` rows, delete the local `User` row, then
delete the Supabase Auth identity) — not a second, guest-specific deletion
path. A guest's `Guess` rows carry the exact same "other players'
historical uniqueness (REQ-204) and leaderboard totals (REQ-409) depend on
the total guess count staying intact" property REQ-710 already established
for real accounts (REQ-717/ADR-0036 already makes a guest's guesses count
normally toward both), so hard-deleting them here would corrupt those same
denominators identically. See ADR-0038 for this decision in full.

**1. Deletion at logout:**
- Given a guest account (`IsGuest = true`) that has never been claimed
- When that guest logs out of the application
- Then the backend deletes that account via the mechanism above, as part
  of handling the logout
- Given the same account has since been claimed (`IsGuest = false`) before
  logging out
- Then logging out deletes nothing — it behaves exactly as any other
  account's logout already does (REQ-715)
- And this is a best-effort deletion: it depends on a client-initiated
  logout call actually reaching the backend and completing, so a browser
  closing before that call completes, or the call itself failing, does not
  leave the account permanently un-purged — rule 3 below (the 7-day
  inactivity purge) independently catches any guest account not removed at
  logout, so correctness never depends on the logout call always
  succeeding

**2. Unclaimed-guest purge (30 days):**
- Given a guest account where `IsGuest = true AND ClaimedAt IS NULL`, and
  more than 30 days have passed since `CreatedAt`
- When the scheduled cleanup job runs
- Then that account is deleted via the mechanism above
- And a guest account that was claimed at any point (`ClaimedAt` set,
  `IsGuest = false`) is never purged by this rule, no matter how long ago
  it was created or claimed

**3. Inactive-guest purge (7 days):**
- Given a guest account where `IsGuest = true`, and more than 7 days have
  passed since `User.LastActiveAt` (the new tracked field defined below)
- When the scheduled cleanup job runs
- Then that account is deleted via the mechanism above
- And because claiming an account clears `IsGuest`, a claimed account is
  never subject to this rule from the moment it is claimed onward,
  regardless of how inactive it later becomes — this requirement adds no
  inactivity-based purge for real (non-guest) accounts

**Activity tracking (new field):**
- Given any account, guest or not
- When that account is created, logs in (`POST /auth/login`), is
  provisioned as a guest (`POST /auth/guest`), claims a guest account
  (`POST /auth/claim`), or submits a guess (REQ-201)
- Then `User.LastActiveAt` is set to the current time — initialized to
  `CreatedAt` at account creation, so a brand-new account's first 7-day
  window is measured from creation, never left undefined
- And no other request (e.g. viewing a leaderboard, fetching the current
  grid without guessing) updates `LastActiveAt` — the signal this field
  exists to capture is genuine play, not passive viewing, and updating it
  on every read request would add write volume with no benefit to either
  purge rule above
- And this field is tracked for every account, not only guests — a single,
  unconditional write path with no `IsGuest` branch in the login/guess/
  claim code that updates it; only rule 3's purge job filters by `IsGuest`
  when deciding what to act on, the same "guest flag consulted in exactly
  one place" discipline REQ-409's exclusion already established for a
  different field (ADR-0036)

**Interaction between rules 2 and 3:** these are not redundant. A guest
that keeps playing every few days without ever claiming is never caught by
rule 3 (its `LastActiveAt` keeps refreshing) but is still caught by rule 2
once 30 days have passed since creation — bounding how long an unclaimed
guest identity can persist even if it stays "active" indefinitely. A guest
that stops playing after a single session is caught by rule 3 well before
rule 2 would ever apply. A single cleanup run checks both conditions and
purges any account satisfying either one.

- **Status: Implemented (Tier 0, `docs/backlog.md` S-072, 2026-07-25).**
  `User.LastActiveAt` (non-nullable `DateTime`, migration
  `20260725120000_AddUserLastActiveAt`) is set at account creation
  (Signup/Guest) and updated by `IUserRepository.UpdateLastActiveAtAsync`
  (Login, a submitted guess in `GuessEndpoints`) or folded into
  `ClaimGuestAsync`'s existing write (Claim) — no `IsGuest` branch in any of
  those four paths. `POST /auth/logout` (new, `[Authorize]`) implements
  rule 1: for an unclaimed guest, calls the same
  `IAccountDeletionService.DeleteAccountAsync` REQ-710 already uses, then
  always responds `204` regardless of outcome (best-effort, per this
  requirement's own clause). `POST /internal/purge-guest-accounts` (new,
  bearer-token-gated like `/internal/generate-round`) implements rules 2
  and 3 via two new `IUserRepository` queries
  (`GetUnclaimedGuestsOlderThanAsync`/`GetInactiveGuestsOlderThanAsync`),
  deduping a row matching both before deleting, run daily by a new
  `purge-guest-accounts.yml` GitHub Actions workflow (07:00 UTC, offset from
  `generate-round.yml`'s 06:00 — now `generate-grid-round.yml`'s/
  `generate-path-round.yml`'s shared 06:00, S-136). The bearer-token constant-time-compare
  check itself was extracted from `InternalRoundEndpoints` into a shared
  `XGArcade.Api.Internal.InternalJobAuthorization` helper so this second
  `/internal/*` endpoint doesn't hand-duplicate it. Frontend: `App.tsx`'s
  `handleLogout` now also fires a best-effort, non-blocking `POST
  /auth/logout` (new `lib/api.ts` `logout()`) — never awaited in the local
  clear-and-reset path, so REQ-715's instant logout UX is unaffected.
  Real NUnit/API coverage has been added (`AuthEndpointTests.cs`,
  `GuessEndpointTests.cs`, `InternalGuestCleanupEndpointTests.cs`,
  `UserRepositoryTests.cs`) but **not independently run against a live
  Postgres/`dotnet test`** in this build environment (no `dotnet` SDK
  available) — both the implementation and the tests were hand-traced
  against REQ-718's own acceptance criteria instead; confirm in CI.

**UI: logout confirmation and guest-expiry copy (2026-07-28 addition —
Status: Implemented, 2026-08-01.)** Two small, additive UI-only
changes to the guest experience. Neither changes the deletion mechanism
above (rules 1–3) or its backend implementation in any way — nothing here
alters when or how an account is actually deleted, only what a guest sees
immediately before, or is told about, that deletion. Today, `App.tsx`'s
`handleLogout` deletes an unclaimed guest's account silently and
unconditionally on logout (rule 1 above) with no confirmation step and no
UI copy explaining guest expiry at all — this addition adds both, without
touching `handleLogout`'s existing best-effort, non-blocking `POST
/auth/logout` call or REQ-715's logout behavior for a non-guest account.

**4. Confirmation before logout-triggered deletion:**
- Given a logged-in guest account (`IsGuest = true`)
- When that guest clicks "Log out"
- Then a confirmation prompt appears first, stating plainly that logging
  out will delete this guest account and its progress (rule 1 above) —
  the existing `handleLogout` flow (local token/state clear, plus the
  existing best-effort, non-blocking `POST /auth/logout`) does not fire
  until this prompt is confirmed
- Given the guest cancels that prompt
- Then nothing happens: the session, stored tokens, and current screen are
  left exactly as they were, and no `POST /auth/logout` call is made
- Given the guest confirms the prompt
- Then the existing `handleLogout` flow fires exactly as it does today,
  completely unmodified by this addition — same best-effort, never-awaited
  `POST /auth/logout`, same immediate local clear-and-reset
- Given a logged-in non-guest account (`IsGuest = false`)
- When that user clicks "Log out"
- Then no confirmation prompt appears at all — logout proceeds exactly as
  REQ-715 already specifies, byte-for-byte unchanged from today's behavior

**5. Guest-expiry copy:**
- Given a logged-in guest account
- When that guest views the existing guest banner (`App.tsx`) and/or the
  guest-facing section of `SettingsScreen.tsx`
- Then visible copy states that guest accounts are temporary and names the
  actual policy this REQ's rules 2 and 3 already define — removed
  automatically after 7 days of inactivity, or after 30 days if never
  claimed, whichever comes first — not a vague "temporary account"
  statement with no numbers
- And if rule 2's or rule 3's threshold value ever changes, this copy must
  be updated in the same change — it is a live restatement of this REQ's
  own numbers, not an independently-maintained, hardcoded approximation of
  them
- Given a logged-in non-guest account
- Then neither the banner nor the Settings screen shows this copy —
  scoped identically to the existing guest-only banner/claim section
  REQ-717 already describes ("visible only while the account is a guest")

- **Status: Implemented, 2026-08-01.** Rule 4: a new
  `GuestLogoutConfirm.tsx`/`.css` (`frontend/src/nav/`) renders a
  `role="dialog"`/`aria-modal` confirmation, reusing `ScoringExplainer`'s
  modal shell/a11y pattern (backdrop-click, Escape, focus-in/focus-return)
  and `DeleteAccountScreen`'s two-button confirm styling — no new
  design-document.md tokens or SCREEN entry, since both patterns were
  already documented. `App.tsx`'s existing "Log out" click handler
  (`handleLogoutClick`) opens this dialog only when `isGuest === true`;
  cancelling calls only `onCancel` (dialog closes, nothing else happens,
  no backend call); confirming calls `onConfirm`, wired straight through
  to the existing, completely unmodified `handleLogout` — same
  best-effort, never-awaited `POST /auth/logout`, same immediate local
  clear-and-reset. A non-guest account's "Log out" click still calls
  `handleLogout` directly, with no dialog in between, exactly as REQ-715
  already specifies. Rule 5: a single new `guestExpiryCopy.ts`
  (`frontend/src/lib/`) exports `GUEST_EXPIRY_COPY`, the one string
  stating the actual 7-day/30-day thresholds, imported by both `App.tsx`'s
  guest banner and `SettingsScreen.tsx`'s guest claim section — no
  independently-hardcoded copy of either number in either place. Test
  coverage: 8 new tests across `App.test.tsx` (6, covering the dialog
  appearing for a guest, cancel leaving session/tokens/screen untouched
  with no backend call, confirm running the unmodified `handleLogout`, a
  non-guest getting no dialog at all, and the expiry copy rendering for a
  guest / being absent for a non-guest in the banner) and
  `SettingsScreen.test.tsx` (2, the same expiry-copy present/absent check
  for the Settings guest section) — full suite green at 367/367 Vitest
  tests, clean `tsc -b`, clean `oxlint`.

- **Rule 5 addendum (2026-08-25):** on narrow/mobile viewports the
  always-visible expiry sentence forced the guest banner onto two lines,
  taking up disproportionate screen space — reported directly by the
  product owner. The sentence is now behind a collapsible disclosure
  toggle, `aria-expanded`/`aria-controls`, same accessible pattern as
  `HeaderNav`'s existing toggles, collapsed by default. This narrows rule
  5's "visible copy states..." criterion below: the copy is present and
  reachable in one tap on every guest banner view, not necessarily
  rendered open by default — the acceptance criterion is satisfied by the
  toggle disclosing the exact, unmodified `GUEST_EXPIRY_COPY` sentence, not
  by it always being on-screen without interaction. The "Playing as
  {name}." line and "Save your progress" action are unaffected — always
  visible, never collapsed. `SettingsScreen.tsx`'s own guest-expiry copy
  (reached only by navigating to Settings, not passively taking up header
  room) is unaffected by this addendum and stays always-visible there.
  **Same-day icon revision:** the toggle's initial text label ("Guest
  account details" / "Hide guest account details") was itself wide enough
  to keep the collapsed row wrapping onto two lines on common phone
  widths, defeating the point of collapsing — replaced with an icon-only
  button (a small right/down caret swapping on click, no animation, same
  "no new motion" constraint as the rest of this banner), the accessible
  name moved to `aria-label`. Verified against a static render of the
  banner markup at 320-412px viewport widths: the collapsed row is a
  single line from ~375px up (most current phones), still wraps
  gracefully — never clips — below that. The banner's own gap/padding
  were also tightened one step down the existing spacing scale
  (`--space-2`/`--space-4` → `--space-1`/`--space-2`, no new tokens) to
  make that single-line fit possible.

**Test level:** Unit (`LastActiveAt` is set on account creation and
updated on login/guest-creation/claim/guess-submission and on no other
request; the 30-day-unclaimed and 7-day-inactive queries each select
exactly the rows the definitions above require, including the boundary
case of a claimed account with `IsGuest = false` regardless of age), API
(logging out an unclaimed guest deletes the account — a subsequent request
with that account's token is rejected; logging out a claimed account
deletes nothing), Integration (the scheduled cleanup job run end to end
against seeded unclaimed/inactive/claimed/active guest rows purges only
the accounts the rules above require, reusing `IAccountDeletionService` —
no second deletion code path), UI/E2E (a guest's "Log out" click shows a
confirmation prompt before anything else happens; cancelling leaves
session, local storage, and the current screen untouched and makes no
backend call; confirming triggers the existing best-effort `POST
/auth/logout` unchanged; a non-guest's logout shows no prompt at all; the
guest banner and Settings guest section render copy containing the actual
7-day and 30-day thresholds; neither renders that copy for a non-guest
account — added 2026-07-28, implemented and covered by Vitest as of
2026-08-01)

**REQ-719 – Unauthenticated splash/landing screen before login/signup**
> As a first-time or logged-out visitor, I want to see an introductory
> landing screen before the login/signup form, so I get a sense of what
> xG Arcade is and make a deliberate choice to proceed, rather than being
> dropped straight into a form.

**Context:** today, `frontend/src/App.tsx` renders `AuthScreen` directly the
moment there is no valid access token — there is no unauthenticated landing
page at all. This requirement adds one screen ahead of `AuthScreen`; it
changes nothing about `AuthScreen` itself, nothing about the account
creation/login mechanism (REQ-701 and friends), and nothing about
REQ-303/S-021's already-settled post-login routing to the game-selection
screen (see the explicit non-interaction criterion below). This is a
client-side routing addition only — no new endpoint, no data model change.

- Given a visitor's session is unauthenticated — a first-ever visit, a
  reload with no stored session, or any point at which the app has finished
  determining no valid session exists (including after REQ-715's silent
  refresh-token attempt, if a stored refresh token exists, has completed
  and failed or found none to try)
- When the app renders
- Then the visitor sees a splash/landing screen, not the login/signup form
  (`AuthScreen`) directly
- And this splash screen is shown every time the app reaches this
  unauthenticated state, not only on a literal first-ever visit — no
  persisted "already seen this" flag suppresses it on a later visit (a
  deliberate default, see §5; revisit if real use shows it's an annoying
  extra click for a frequent visitor)

- Given the splash screen is showing
- When the visitor wants to log in or create an account
- Then a single, explicit, unambiguous call-to-action (clearly the primary
  action on the screen — no competing primary action of equal visual
  weight) takes them to the existing login/signup form (`AuthScreen`) with
  no further step required
- And the platform name ("xG Arcade") is displayed with clear visual
  presence on this screen, styled using only color/typography tokens
  already defined in `docs/design-document.md` §2 — no new color, typeface,
  or animation introduced solely for this screen, and no image logo asset
  required (logo/brand-mark artwork is explicitly out of scope for this
  requirement and is being scoped separately — this screen must work
  correctly with typographic/token-based treatment alone)

- Given a signed-in player logs out (REQ-715), deletes their account
  (REQ-710/REQ-718's guest-cleanup logout path), or their session ends
  because a stored refresh token is invalid, expired, revoked, or absent
  (REQ-715)
- When they next reach an unauthenticated screen as a result
- Then they see the splash screen first, not the login/signup form
  directly — the same single unauthenticated entry point a first-time
  visitor sees, not a special-cased shortcut for a returning session
- **Judgement call, recorded here per the product owner's own request for
  a recommendation:** consistency was chosen over shortcutting straight to
  `AuthScreen` after logout, for two reasons — (1) it keeps exactly one
  unauthenticated entry point for the whole app rather than two slightly
  different ones depending on history, which is both simpler to reason
  about and to test; (2) logging out doesn't necessarily mean the visitor
  intends to sign back in immediately (they may simply be done playing),
  so landing on a login form presumes an intent the app doesn't actually
  know. This is a reasonable default, not a settled-forever product law —
  an implementer or reviewer could reasonably argue the opposite (less
  friction for someone logging out only to switch accounts) and revisit it.

- Given a visitor reaches `AuthScreen` from this splash screen and
  successfully logs in or signs up
- Then they land on the game-selection screen exactly as REQ-303/S-021's
  existing behavior already defines — this requirement governs only what
  is shown *before* authentication and never alters what happens
  immediately after a successful login/signup completes

**Test level:** UI (component test: the splash screen renders instead of
`AuthScreen` whenever there is no authenticated session; its call-to-action
navigates to `AuthScreen`; logout, account deletion, and a failed/absent
refresh-token check each route back to the splash screen, never directly to
`AuthScreen`), E2E (Playwright: a fresh, fully unauthenticated visit shows
the splash screen first and a visitor can still reach and complete login
from it; logging out returns to the splash screen, from which logging back
in remains reachable — never a dead end)

---

**REQ-720 – Header nav gains a "Games" entry listing available games
(supersedes S-029's nav simplification)**
> As a player, I want a "Games" entry in the header nav that lists every
> game xG Arcade currently hosts, so I can jump directly to a specific game
> from anywhere in the app, now that the platform is expected to host more
> than one.

- **Context — a deliberate reversal, not a silent contradiction:** S-029
  (`docs/backlog.md`) removed a "Games"/"Grid" nav pair specifically
  because, with exactly one game in existence, it duplicated the existing
  game-selection landing screen (`GameSelectScreen`, REQ-303/S-021)
  reachable via the "xG Arcade" header title — see REQ-303's own S-029
  bullet and its new status note. That removal's premise was "xG Arcade
  will only ever host one game." The product owner has since said more
  games are planned, so that premise no longer holds; this requirement
  reintroduces a "Games" nav entry on the corrected premise, not as a
  silent contradiction of S-029's earlier call.
- Given a logged-in player, when the header nav renders (REQ-712's
  collapsed mobile menu, once opened, or the flat row at/above its
  breakpoint)
- Then it contains one entry labeled "Games," alongside the existing
  "Leaderboard," "Leagues," "Settings," and "Log out" entries
- Given the "Games" entry, when a player activates it (click/tap, or
  Enter/Space while it has focus)
- Then it toggles open/closed a list containing one entry per game xG
  Arcade currently hosts (originally Tier 0's exactly one, "xG Grid"; as of
  S-085, two — "xG Grid" and "xG Path" — see status note below) — the same
  accessible-disclosure pattern REQ-712's own toggle already establishes (a
  real, focusable, keyboard-operable control exposing `aria-expanded`
  reflecting its open/closed state)
- And activating "Games" itself never navigates anywhere — it is a
  disclosure control only, not a link; it only shows/hides the per-game
  list
- Given the per-game list is open, when a player selects "xG Grid"
- Then they are taken to that game's current screen — the same
  destination and behavior `GameSelectScreen`'s own "xG Grid" tile already
  triggers, unchanged — and the per-game list closes (and, on a narrow
  viewport, REQ-712's outer nav menu closes with it, matching how every
  other nav entry already behaves)
- Given a game's own screen is currently showing, when the header nav
  renders
- Then that game's entry inside the "Games" list carries
  `aria-current="page"`, the same convention "Leaderboard," "Leagues," and
  "Settings" already use for their own current-screen state
- Given the "xG Arcade" header title, when a player clicks/taps it
- Then it continues to navigate to `GameSelectScreen` (REQ-303) exactly as
  before, unchanged by this requirement — **both affordances are kept
  deliberately, not left as an unexplained duplicate:** "Games" is a
  quick-jump shortcut reachable from anywhere in the app (including from
  inside another screen entirely, e.g. while looking at the leaderboard),
  while the title remains the route to the full landing/picker screen
  shown immediately after login (REQ-303/S-021) — a distinct screen with
  room to grow (e.g. richer per-game presentation later) that a flat nav
  list entry doesn't have room for
- Given the viewport is below REQ-712's mobile breakpoint and its outer nav
  menu is open, when "Games" is expanded inside that menu
- Then REQ-712's own toggle, breakpoint, and "the header nav row never
  wraps onto a second line or causes horizontal overflow" guarantee are
  unaffected by this nested disclosure
- Given the viewport is at or above REQ-712's mobile breakpoint, when
  "Games" is expanded as part of the flat row
- Then the row itself still does not wrap or overflow — the same guarantee
  REQ-712 already requires, now also holding for this expandable entry
- Given exactly one game exists (Tier 0's original state, at the time this
  requirement was written), when "Games" is expanded
- Then it lists exactly that one entry — this requirement shipped ahead of
  a second game actually existing, since anticipating growth was the
  entire point of the product owner's request; it was not deferred until a
  second game was added

- **Status note (2026-08-01, S-085):** xG Path is now a real, merged
  second game (S-082 onward). `GameSelectScreen.tsx`'s tile row and this
  requirement's "Games" nav list both gained a second entry ("xG Path"),
  in the same order, closing the "exactly one game" gap the two bullets
  above describe as this requirement's original, point-in-time state — no
  behavior change to the criteria themselves, since both were always
  written generically ("one entry per game xG Arcade currently hosts"),
  only their illustrative Tier-0 asides were time-bound.

**Test level:** UI (component: "Games" toggles independently of REQ-712's
outer toggle and never itself triggers navigation; `aria-expanded`/
`aria-current` correctness; selecting "xG Grid" navigates to the grid
screen and closes both the per-game list and, where applicable, the outer
menu), E2E (Playwright: nav → Games → xG Grid reaches the grid screen; the
"xG Arcade" title still reaches `GameSelectScreen` unchanged; a narrow
viewport check confirms the nested disclosure doesn't reintroduce
wrapping/overflow)

**Flag for `architecture-reviewer`:** whether "Games" as a non-navigating,
nested disclosure control (a toggle within REQ-712's own toggle, on mobile)
needs its own ADR or an amendment to ADR-0030 — ADR-0030 covered the outer
mobile collapse and the Settings consolidation, but not a second,
independently-expandable entry nested inside it. Not decided here; this is
a structural nav-pattern call, not a requirements-level detail.

---

**REQ-721 – Current screen reflected in the URL; a page reload restores it**
> As a player, I want the browser's URL to reflect whichever screen I'm
> currently on, so that reloading the page (or sharing/bookmarking a URL)
> returns me to that screen instead of always bouncing back to the
> game-selection landing screen.

- **Context:** today `frontend/src/App.tsx`'s `Screen` union
  (`'game-select' | 'grid' | 'leaderboard' | 'leagues' | 'settings' |
  'admin'`) is pure React state — there is no router, the browser URL never
  changes as a player navigates, and a reload always resets to
  `'game-select'` (or, if unauthenticated, the splash/auth screens,
  REQ-719). This requirement specifies observable behavior only; it
  deliberately does not mandate hash-based vs. path-based URLs, or any
  specific routing library — see the "needs an ADR" note below.
- Given a logged-in player moves between screens (game-select, grid,
  leaderboard, leagues, settings, and, for an admin, admin) using the
  header nav or any other in-app navigation control
- When a screen change occurs
- Then the browser's URL changes to a value distinct to that screen — no
  two of the screens above ever share the same URL, and returning to the
  same screen later always produces the same URL for it
- Given a player is on an authenticated screen whose URL reflects it, and
  their stored session is still valid at the time
- When they reload the page
- Then they are returned to the same screen the URL denotes, rather than
  being unconditionally reset to the game-selection screen
- Given a player's stored session is invalid, expired, or absent at the
  moment the app finishes determining this (REQ-719's own definition of
  "unauthenticated")
- When a page load or reload happens, regardless of what screen the URL in
  the address bar denoted
- Then the unauthenticated splash screen (REQ-719) is shown — a requested
  URL never bypasses the authentication gate or skips straight to an
  authenticated screen, or to `AuthScreen` itself
- Given a player actively completes login or signup (submits valid
  credentials — not merely reloading with an already-valid stored session)
- When authentication succeeds
- Then they land on the game-selection screen exactly as REQ-303/S-021
  already requires, regardless of whatever URL was present beforehand —
  this requirement changes what a page load/reload of an *already
  established* session restores; it does not change what a fresh
  login/signup action itself does
- Given this requirement is implemented
- Then browser back/forward button behavior is explicitly out of scope —
  no guarantee is made about which screen, if any, is shown after a
  back/forward navigation; that is left for a future requirement if real
  use shows it is needed, not assumed for free as a side effect of
  URL-per-screen support

**Judgement call, recorded here (how URL restoration interacts with
REQ-303 and REQ-719), per this document's own practice of resolving this
kind of question rather than leaving it open:** URL-restored state applies
only to a page load/reload of an *already-authenticated, already-valid*
session — it never bypasses REQ-719's splash-then-auth gate for a visitor
who isn't authenticated, and it never changes what happens the moment a
login/signup action itself succeeds (still always game-select, per
REQ-303/S-021, unchanged). Reasoning: REQ-303's "always lands on
game-select" rule is about the event of *just having authenticated*, not
about every subsequent render of an already-open session — a reload of a
session that was already past that point isn't a new login, so restoring
the actual screen the player was on is the more useful behavior and does
not contradict what REQ-303 actually requires. REQ-719's splash gate, by
contrast, is a security/consistency boundary on the *unauthenticated* side
and must not have a URL-shaped bypass — an authenticated-only screen must
never partially render, or be inferred as "intended," just because a URL
asked for it while no valid session exists.

**Test level:** E2E (Playwright: navigating through several screens
changes the URL each time; reloading on each authenticated screen with a
valid session restores that same screen; reloading while logged out shows
the splash screen regardless of what URL was requested; completing a fresh
login always lands on game-select regardless of the URL present
immediately before submitting the form); UI (unit: a reload with no valid
token renders the splash/auth flow, never an authenticated screen,
regardless of any stored/URL screen indicator)

**Needs an ADR:** this is the first router/URL-state mechanism in the
frontend (`frontend/src/App.tsx` currently has no router at all) — a
genuine "could reasonably have gone another way" structural decision (hash
vs. path-based URLs, which library if any, how it composes with the
existing `screen` state and REQ-719's splash gating — the product owner
explicitly asked whether `/` or `#` should be used) per `CLAUDE.md`'s ADR
guidance. Flagged for `architecture-reviewer`/the implementer to write
before or alongside implementation — not decided here, since a
requirements document specifies WHAT and HOW TO VERIFY, not HOW TO BUILD.

---

**REQ-722 – Upload a profile avatar, pending admin approval**
> As a player, I want to upload a picture for my profile, so other
> players see something more personal than just my display name — with an
> admin checking it first so nothing inappropriate goes live.

**Uploading:**
- Given a logged-in player with a claimed (non-guest) account —
  **corrected 2026-08-25, product decision** (this criterion originally
  read "Given a logged-in player (guest or claimed account)," deliberately
  inclusive of guests; see the dated status note below for the reversal
  and its reasoning)
- When they upload an image file within a reasonable size/type limit
  (exact limits left to implementation, matching this document's existing
  practice of leaving non-product thresholds to
  `implementation-document.md`)
- Then a new avatar submission is created in `Pending` status for that
  player, and it is not visible to any other player until an admin
  approves it (REQ-517)
- Given a guest account (`IsGuest = true`, REQ-717) attempts to upload an
  avatar
- Then the request is rejected with a `403`, enforced server-side
  regardless of what the client sends — same boundary rule REQ-215/
  REQ-903 already establish for their own guest-exclusion paths
- And the rejection tells the guest why, not a generic error — that they
  must claim their account first (`POST /auth/claim`, REQ-717) before
  they can upload an avatar — the same "server's own detail text shown
  inline" convention already used elsewhere in Settings (REQ-714's
  display-name conflict error, this REQ's own size/type-limit error)
- Given a player already has a submission in `Pending` status
- When they upload again
- Then the prior pending submission is replaced by the new one — never
  two pending submissions queued for the same player at once, and the
  queue an admin reviews (REQ-517) never shows more than one pending row
  per player

**Seeing your own status:**
- Given a player has an avatar submission in `Pending`, `Approved`, or
  `Rejected` status, or has never submitted one
- When they view their own avatar setting (in Settings, alongside
  REQ-714's display-name edit)
- Then they see which of those four states applies, and — for `Pending`
  or `Rejected` — a preview of the image in that state; a `Rejected`
  status does not remove or affect a separately-existing `Approved`
  avatar from an earlier, different submission

**Replacing an approved avatar:**
- Given a player already has an `Approved` avatar
- When they upload a new image
- Then the new submission enters the queue as `Pending` (REQ-517) and the
  previously-approved image continues to be shown to other players until
  the new one is itself approved — uploading never blanks a player's
  visible avatar while the replacement awaits review

**No avatar / rejected state, as seen by other players:**
- Given a player has no `Approved` avatar (never uploaded one, or their
  only submission is `Pending`/`Rejected`)
- When another player views their stats (REQ-411) or any other surface
  showing their avatar
- Then a placeholder is shown instead (initials or a generic icon, exact
  presentation left to `design-document.md`) — a `Pending` or `Rejected`
  image is never shown to anyone but the submitting player

**Test level:** Unit (uploading while a `Pending` submission exists
replaces it rather than creating a second one; approving a new submission
while an older `Approved` one exists supersedes it, never leaving two
`Approved` rows). API (`POST /users/me/avatar` returns 401 with no
session; rejects a file outside the configured size/type limit with a
clear error; returns 403 with claim-account guidance for a guest caller,
`IsGuest = true`, and is unaffected for a claimed/non-guest caller). UI
(Settings shows the correct one of the four states with a preview where
applicable; another player's view never renders a `Pending`/`Rejected`
image, only `Approved` or the placeholder; a guest sees the claim-first
guidance, not a generic error, if they attempt to upload).

**Needs an ADR:** the storage backend for uploaded images is a genuine
"could reasonably have gone another way" structural decision (Supabase
Storage vs. Azure Blob Storage, and the abstraction boundary that keeps
`XGArcade.Core`/`XGArcade.Api` hosting-agnostic per ADR-0004) — flagged
for `architecture-reviewer`/the implementer to write before or alongside
implementation, not decided here. Product direction from this planning
session (2026-08-24): Supabase Storage, to reuse the existing Supabase
dependency and avoid adding Azure-specific code to `Core`/`Api` — the ADR
should record this choice and its reasoning, not relitigate it from
scratch.

**Status note (2026-08-24, S-180 — backend built):** the ADR flagged above
is written — ADR-0087 (Supabase Storage; `IAvatarStorage` in
`XGArcade.Core`, its concrete client in a new project, `XGArcade.Storage`,
kept out of `Core`/`Api` per ADR-0004). The "Uploading" criteria above are
now built via `POST /users/me/avatar`: the "reasonable size/type limit"
left open there is 5 MB, and `image/jpeg`/`image/png`/`image/webp` only
(no `image/gif`/`image/svg+xml` — SVG deliberately excluded since it can
carry executable content) — recorded in `implementation-document.md` §5.
REQ-517's admin approve/reject (S-181) remains a separate, not-yet-built
story — today every submission stays `Pending` indefinitely with no path
to `Approved`/`Rejected`, so this REQ's "visible to other players once
approved" clause, the "Seeing your own status" and "No avatar / rejected
state" criteria above, and S-182/183's frontend consumers are all still
unbuilt. Both `architecture-reviewer` and `quality-architect` passed the
backend diff with no blocking findings.

**Status note (2026-08-24, S-181 — REQ-517's backend built, see that REQ's
own status note below for the full detail):** submissions now have a path
off `Pending` (`POST /admin/avatar-submissions/{id}/approve|reject`). This
REQ's "visible to other players once approved" clause, "Seeing your own
status," and "No avatar / rejected state" criteria remain unbuilt — none
of those are read paths, and none are built by S-181, a write-side-only
(admin review) story. No frontend (`/users/me/avatar` GET, Settings UI,
other-player avatar rendering) exists yet — S-182/183 remain separate,
not-yet-built stories. Built without a local `dotnet` SDK in-sandbox; CI
verification pending as of this note.

**Status note (2026-08-24, S-182 — the "Seeing your own status" and
"Replacing an approved avatar" criteria are now built end-to-end):** a "My
avatar" section in `SettingsScreen.tsx` (`frontend/src/settings/`, SCREEN-08
addendum) reads two new endpoints built alongside it — `GET
/users/me/avatar` (three independent Pending/Rejected/Approved summaries,
never one mutually-exclusive status, per this REQ's own "a `Rejected`
status does not remove or affect a separately-existing `Approved` avatar"
clause) and `GET /users/me/avatar/{id}/image` (owner-only byte stream, used
for the preview shown alongside each status). Uploading while already
`Approved` correctly leaves the prior approved image reported as-is while
the new submission shows as `Pending`, matching "Replacing an approved
avatar"'s acceptance criterion exactly. The one criterion in this REQ still
genuinely unbuilt: **"No avatar / rejected state, as seen by other
players."** No surface anywhere in the frontend renders *another* player's
avatar yet — REQ-411's stats view (`UserStatsScreen.tsx`, implemented
end-to-end as of S-179, see that REQ's own status note) does not display an
avatar at all, own or another player's, so this criterion has no assigned
story yet; flagged here rather than assumed covered by REQ-411's existing
"Implemented" status. `GET /users/me/avatar/{id}/image` streams bytes
through the backend rather than handing back a signed URL — a second,
narrower, owner-scoped mediation shape on `IAvatarStorage`
(`DownloadAsync`) alongside S-181's admin-facing `GetPreviewUrlAsync`
(signed URL); see ADR-0087's "Consequences" section (S-182 follow-up
paragraph) for the fuller reasoning on why these two shapes coexist
deliberately rather than one being reused for the other, and which one is
canonical for any future "another player's avatar" surface. Built without a
local `dotnet` SDK in-sandbox; confirmed via a real CI run (`ci.yml`,
`workflow_dispatch`) on the final commit — backend, frontend unit, and E2E
jobs all green.

**Status note (2026-08-24 — bug fix, "Failed to fetch" on upload):** a
player-reported "Failed to fetch" on `POST /users/me/avatar` traced to the
handler's `avatarStorage.UploadAsync` call being the only external-dependency
call in `AvatarEndpoints.cs` with no `try`/`catch` — any failure calling
Supabase Storage (unreachable, bucket misconfigured, timeout) threw
unhandled while `file`'s multipart body was still being read, which Kestrel
can turn into a bare connection reset instead of a clean HTTP response;
`fetch()` in the browser surfaces that as an undiagnosable generic
`TypeError: Failed to fetch` rather than a `throwApiError`-visible message.
Fixed by wrapping the call and returning `Results.Problem` (503, "Avatar
upload unavailable") on failure, matching this codebase's established
external-dependency convention (`GuessEndpoints.cs`'s `LiveLookupUnavailable`
case, `InternalRoundEndpoints.cs`'s round-generation catch blocks) — no
acceptance criterion above changes, this only makes an already-implied
failure mode ("the upload didn't succeed") fail with a message the player
and `describeError`/`SettingsScreen.tsx` can actually surface instead of an
opaque network error. Covered by
`REQ722_Avatar_Post_ReturnsServiceUnavailable_WhenStorageUploadFails` in
`AvatarEndpointTests.cs`.

**Status note (2026-08-24 — follow-up: diagnosability of the 503 above):**
after the fix above shipped, a real deployment (dev) still returned "Avatar
upload unavailable" — no longer a bare "Failed to fetch," but the container
logs only showed `HttpRequestException: Response status code does not
indicate success: 400 (Bad Request)` with no further detail, because
`SupabaseAvatarStorage`'s `EnsureSuccessStatusCode()` calls discard the
response body Supabase Storage actually explains the rejection in. Root
cause in this instance: the `avatars` bucket referenced by `SETUP.md` step 7
had never been created in that environment's Supabase project (a manual,
human dashboard step this backend has no code path to perform) — created
directly in the dashboard, not fixed in code. Separately, added a
`EnsureSuccessAsync` helper (`SupabaseAvatarStorage.cs`) that folds the
response body into the thrown exception's `Message` for every call in that
class (`UploadAsync`/`DownloadAsync`/`GetPreviewUrlAsync`), so a future
rejection (wrong MIME-type policy, bucket size limit, ...) is diagnosable
from `AvatarEndpoints.cs`'s/`AdminAvatarEndpoints.cs`'s existing
`logger.LogError(ex, ...)` calls without needing direct Supabase dashboard
access to guess why. Nothing here changes what the player sees (still the
same generic `Results.Problem` detail) or any acceptance criterion. Covered
by `REQ722_UploadAsync_ThrownExceptionIncludesSupabasesResponseBody_ForDiagnosability`
in `SupabaseAvatarStorageTests.cs`.

**Status note (2026-08-25, S-184 — the last open criterion, "No avatar /
rejected state, as seen by other players," is now built):** a new
`GET /users/{userId}/avatar/image` (`AvatarEndpoints.cs`) lets any
authenticated player fetch any other player's currently-`Approved` avatar —
the caller is verified as logged-in only, never compared against
`{userId}`, the deliberate opposite of the owner-only
`GET /users/me/avatar/{id}/image`. Reuses
`IAvatarSubmissionRepository.GetApprovedAsync`/`IAvatarStorage.DownloadAsync`
unchanged; "never uploaded," "only `Pending`," and "only `Rejected`" all
collapse into the same 404, matching this criterion's "a placeholder is
shown instead" framing regardless of which no-avatar cause applies.
Consumed by a new shared `PlayerAvatar.tsx` component
(`frontend/src/components/`), now rendered in `UserStatsScreen.tsx`'s
header (REQ-411, SCREEN-13) — the surface this REQ's own S-182 status note
identified as not yet showing any avatar — so a viewed player's stats page
now shows their avatar, own or another's, with the required placeholder
fallback whenever no `Approved` avatar exists. A new Settings profile
header (self-view only, SCREEN-08) was added in the same story but is not
itself part of this criterion, since it never renders another player's
avatar. See `docs/backlog.md` S-184 for the full build record. This closes
out REQ-722's last remaining open scope; no open scope remained under this
REQ as of that story — see the status note immediately below for scope
reopened since.

**Status note (2026-08-25 — product decision, reverses the "Uploading"
criterion's original guest inclusion; not yet implemented):** this REQ's
"Uploading" criterion originally read "Given a logged-in player (guest or
claimed account)," deliberately written to include guests — and was built
that way: `backend/src/XGArcade.Api/Avatars/AvatarEndpoints.cs`'s
top-of-file comment and its `POST /users/me/avatar` handler comment both
explicitly document "no guest exclusion here... re-verified against the
REQ text before writing this endpoint, not assumed by analogy." By
explicit product decision (johan.pearson, this Settings-redesign
session), that is reversed: a guest account (`IsGuest = true`) can no
longer upload an avatar until they claim their account (`POST
/auth/claim`, REQ-717). See the corrected "Uploading" criterion above for
the required enforcement shape (403, server-side, with claim-first
guidance shown inline, matching REQ-714's own new guest-exclusion
criterion). **Implementer note:** `AvatarEndpoints.cs`'s own doc-comment
reasoning quoted above is now superseded by this explicit product
decision — it should be updated to reflect the new rule when the code
changes, not left describing the old (now-incorrect) reasoning; that
comment's prior "no guest exclusion, not assumed by analogy" conclusion
was correct for its time (guests were deliberately included then, per a
close reading of this REQ's text as it then stood) but no longer
describes current product intent. This is a narrow business-rule change,
following the same plain `IsGuest` gate REQ-215/REQ-903 already use for
their own guest-exclusion paths elsewhere — not a new architectural
pattern, so no ADR is needed. Not yet implemented as of this note —
flagged for the next backend story touching `AvatarEndpoints.cs`.

**Status note (2026-08-25 — follow-up: now implemented, S-185):** the
reversal above is built. `AvatarEndpoints.cs`'s `POST /users/me/avatar`
handler now returns a `403` with claim-first guidance for any `IsGuest =
true` caller (checked after resolving the caller so a guest gets a 403,
not a 401, but before the storage upload call), and its top-of-file/
handler comments quoted above are rewritten to describe the new rule
rather than the superseded "no guest exclusion" reasoning. Shares its 403
plumbing with REQ-714's identical display-name check (and REQ-215/
REQ-903's pre-existing ones) via a new
`backend/src/XGArcade.Api/Auth/GuestRejectionProblem.cs` helper, extracted
once this pair of checks became the 4th/5th near-identical occurrence in
the API. Covered by `REQ722_PostAvatar_Returns403_WhenCallerIsGuest`
(`AvatarEndpointTests.cs`); the avatar suite's prior guest-allowed 201
test was updated to match. On the frontend, the profile-header edit
pencil (`SettingsScreen.tsx`, added by S-184) is never rendered while
`isGuest` is true, with a muted claim-first hint shown in its place — see
`docs/backlog.md` S-185 for the full build record, including a
quality-review fix pass for a stale-success-message bug found while
building this.

**Status note (2026-08-25, S-186 — Supabase free-tier egress incident:
`Cache-Control`/`ETag` added to both avatar image-streaming endpoints.)**
Direct follow-up to the same egress incident REQ-110's own 2026-08-25
status note describes. Both `GET /users/me/avatar/{submissionId}/image`
and `GET /users/{userId}/avatar/image` (`AvatarEndpoints.cs`) previously
streamed Supabase Storage bytes through the backend on every single
request with zero caching, so a page rendering the same avatar repeatedly
(e.g. a leaderboard with the same player appearing many times) paid a
fresh `IAvatarStorage.DownloadAsync` — and its own Supabase Storage
egress — every time. An avatar image is immutable once approved
(REQ-722/ADR-0087's replace-not-mutate model — a new upload creates a new
`AvatarSubmission` with a new Id rather than mutating an existing row), so
both endpoints now set `Cache-Control: private, max-age=86400` (1 day) plus
a correct per-endpoint `ETag`, via `Results.Stream`'s `entityTag` parameter
(which also gets conditional-GET/`If-None-Match` -> 304 handling for
free). Both stay `private` — **never** `public` — since both endpoints are
authorization-gated per request (the owner-only endpoint 404s for a
`submissionId` the caller doesn't own; the userId-keyed endpoint requires a
valid bearer token), so a shared/CDN cache serving either response to a
different caller would be an authorization bypass. The two endpoints'
`ETag` sources differ because their identity semantics differ: the
owner-only endpoint's `ETag` is the `submissionId` itself, a permanently
immutable identity, since a given submission's bytes can never change; the
userId-keyed endpoint's `ETag` is the *current* `Approved` submission's own
Id, recomputed per request, since a newer approval can replace which
submission that URL resolves to. No acceptance criterion above changes —
this is a caching/egress optimization on an already-built read path, not a
behavior change to what is shown or to whom. Covered by 2 new tests in
`backend/tests/XGArcade.Api.Tests/AvatarEndpointTests.cs`:
`REQ722_AvatarImage_Get_SetsPrivateCacheControlAndETag` and
`REQ722_GetUserAvatarImage_SetsPrivateCacheControlAndETag`. See
`docs/backlog.md` Epic 26/S-186 for the full build record (this was fix
#4a of that story; fix #1 is REQ-110's `PlayerCareerPrefetchService`
skip, ADR-0088).

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

**Status note (2026-08-17, `docs/backlog.md` S-130):** the requirement
itself is unchanged — this is still what's needed once prod exists. Its
automation (`backup-database.yml`) was deleted, not fixed: it had failed
all 40/40 scheduled runs because it targets `PROD_*` secrets that don't
exist yet (no real prod environment has been created — see
`MVP-SCOPE.md`'s Tier 1 section). Automation must be rebuilt when Tier 1
creates prod; see `infra/README.md`.

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

**REQ-903 – In-app incident reporting to GitHub Issues**
> As a registered (non-guest) player, I want to report a bug or problem I
> hit directly from the app, so the team sees it as a real, actionable
> GitHub issue without me needing a GitHub account of my own.

**Status: Built, 2026-08-10 (ADR-0064).** `POST /incidents`
(`XGArcade.Api.Incidents.IncidentEndpoints`), `Core.IncidentReporting`
(`IGitHubIssueClient`/`GitHubIssueClient`, `IIncidentReportService`/
`IncidentReportService`) implement every backend acceptance criterion
below. Frontend entry point: originally a section inside `SettingsScreen.tsx`;
moved the same day, directly requested, into an app-wide footer button
(`App.tsx`) opening `IncidentReportDialog.tsx` as a modal — reachable from
whatever screen a player is actually on, not just Settings. A third,
same-day pass added structured, mandatory Title/Screen fields (previously
folded into free-text Description) plus an auto-captured, read-only
Environment field, so `IncidentReportService` can format every created
issue into one consistent template rather than however a player happened
to phrase a single free-text box — see design-document.md's SCREEN-11 for
the concrete shape and the screenshot-attachment question this raised,
still deliberately deferred rather than folded into any of these passes.
See COMP-12's own status note (`architecture-document.md`) for the
backend's concrete shape, and this REQ's "Verification status" note at
the end of this section for what's still outstanding before this is
production-ready.

**Tier framing — pulled forward by deliberate product decision, 2026-08-10,
same pattern as REQ-108/REQ-214/REQ-402-403/REQ-717/REQ-215's own
precedent:** no trigger fired (no observed volume of reports going
out-of-band) — the product owner raised it directly. See `MVP-SCOPE.md`'s
Tier 1 section for the matching entry.

- Given a logged-in, non-guest player encounters a problem and opens the
  incident-report entry point
- When they submit a non-blank title, a non-blank description (each with a
  reasonable max length), and a screen selection (a mandatory dropdown
  over a fixed set of values, pre-filled from wherever the entry point was
  opened but changeable) — **updated 2026-08-10, same day as the original
  build, requested directly**: title and screen were folded into free-text
  description originally; splitting them into their own mandatory fields
  is what lets every created issue follow the same shape
- Then the backend creates a GitHub issue in this repository via a
  server-held credential, labeled for triage (e.g. `user-reported`) —
  never a credential exposed to, or accepted from, the client (ADR-0064)
- And the issue's title is the player's own submitted title, and its body
  is a fixed, consistently-formatted template built from the submitted
  fields plus non-PII triage context (the reporting user's internal
  `UserId`, the selected screen, the environment — the frontend's own
  origin URL, captured automatically, never typed by the player — and a
  timestamp), each under its own labeled heading — never the player's
  email and never the GitHub token itself
- And on success the player sees a confirmation (the created issue's URL
  is fine to return — it isn't secret)

- Given the caller is a guest account (`IsGuest == true`)
- Then the request is rejected with `403`, enforced server-side regardless
  of what the client sends — same boundary rule REQ-215 already
  established for a different write path. **Corrected 2026-08-10** (this
  criterion originally said "no incident-report entry point is shown to a
  guest in the UI," which was never actually built that way and directly
  contradicted REQ-215's own "advertised, not hidden" precedent this REQ
  otherwise follows): a guest sees the entry point and every field,
  present but disabled/inert, never hidden — the 403 above is what
  actually enforces the restriction, the disabled UI is advertising only

- Given the caller has no valid session
- Then the request is rejected with `401`

- Given a player has already filed more than a small number of reports in
  a short window (rate limit, exact numbers left to implementation)
- Then further submissions are rejected with a clear "try again later"
  response rather than silently creating more issues

- Given the GitHub API call itself fails (network error, invalid/expired
  token, GitHub-side rate limit)
- Then the player sees a clear failure message, no partial or duplicate
  issue is created, and the failure does not crash or block the rest of
  the app

**Out of scope for this REQ:** any in-app moderation/review queue before
an issue is created (unlike REQ-215/REQ-509-510's suggestion pipeline) —
every valid, rate-limit-respecting submission becomes a real issue
immediately, see ADR-0064's accepted trade-offs.

**Test level:** Unit (request validation, guest/anonymous rejection, rate
limiting), API/Integration (`POST /incidents` auth and status-code
behavior against a stubbed/mocked GitHub client — tests must never call
the real GitHub API), Manual (one real end-to-end submission against a
throwaway/test repo before relying on this in production)

**Verification status (2026-08-10):** Unit (`GitHubIssueClientTests.cs`,
`IncidentReportServiceTests.cs`) and API (`IncidentEndpointTests.cs`)
coverage is written, all against a fake/stubbed `IGitHubIssueClient` — none
of it calls the real GitHub API. **Not yet built/run in this sandbox**
(`dotnet` SDK unavailable here, same recurring sandbox constraint
`docs/CHANGELOG.md`'s 2026-08-10 REQ-509 entry already documents) — hand-
traced against this codebase's existing patterns (`SuggestionEndpoints`/
`SuggestionEndpointTests`, `SupabaseAuthClient`/`SupabaseAuthClientCaptchaTests`),
not compiler-verified; confirm with a real `dotnet test` run in CI. The
`INCIDENT_REPORT_PAT` secret (see `infra/README.md`'s secrets table and
`SETUP.md` step 6) has now been created (confirmed by the product owner,
2026-08-10) — until it existed, `POST /incidents` failed closed (a clear
503, per this REQ's own GitHub-failure acceptance criterion) rather than
doing nothing or crashing. The one real manual end-to-end submission
against a throwaway/test repo this REQ's "Test level" calls for is still
outstanding — do that check before relying on this against the real repo
in front of real players.

---

**REQ-904 – Admin notification for open in-app incident reports**
> As an admin, I want a clear notification in the admin UI when a new
> in-app incident report (REQ-903) has been filed, so I don't have to
> remember to check GitHub Issues manually.

**Count source and display:**
- Given at least one GitHub issue in this repository is open and labeled
  `user-reported` (REQ-903/ADR-0064's fixed, server-configured label — the
  same one `GitHubIssueClient`/`GitHubIncidentReportOptions` already write
  to)
- When an admin who satisfies the existing `"Admin"` authorization policy
  loads the admin area (`AdminScreen.tsx`, SCREEN-04, the same screen
  REQ-512's suggestion badge lives on)
- Then an "Incident reports" entry point shows a count equal to the number
  of currently-open, `user-reported`-labeled issues, sourced from a new
  `GET /admin/incident-reports` endpoint that calls GitHub's Issues API
  server-side (ADR-0066) — no client-supplied repo, label, or token is
  ever accepted; the target repo and label are the same fixed,
  server-configured values REQ-903 already uses
- And the response may also include each open issue's title, number, and
  URL (GitHub's list-issues response returns these at no extra cost), but
  the admin UI need only render the aggregate count plus a single
  "view on GitHub" link that opens this repo's filtered issue list
  (`is:issue is:open label:user-reported`) in a new tab — there is no
  in-app list or detail view of individual issues
- Given zero open, `user-reported`-labeled issues exist
- When an admin loads the admin area
- Then no badge/count is shown next to "Incident reports" — a zero count
  is represented by the count's absence, not a count displaying "0", the
  same convention REQ-512 uses

**Freshness — fetch on load, no polling:**
- Given an admin loads or reloads `AdminScreen.tsx`
- Then the count reflects the server's most recent successful poll of
  GitHub as of that load — the same "fetch on load, no polling/websocket"
  freshness model REQ-511/REQ-512 already use; no live-updating within a
  single page view is required, and no push/real-time mechanism is
  introduced

**Server-side caching of the GitHub read:**
- Given more than one admin loads or reloads the admin area within a short
  window
- When each of those page loads triggers a request to
  `GET /admin/incident-reports`
- Then the backend serves those requests from a short-lived, server-side
  cache shared across all admins (exact TTL left to implementation) rather
  than calling GitHub's Issues API once per page load — repeated admin
  page loads in quick succession do not multiply outbound GitHub API calls
  1:1 with page loads
- Given the cached result has expired
- When the next `GET /admin/incident-reports` request arrives
- Then the backend performs a fresh GitHub API call and repopulates the
  cache from that result

**Failure handling — never a false "zero incidents":**
- Given the GitHub API call fails (network error, GitHub-side rate limit,
  or the incident-reporting token is unconfigured/invalid)
- When an admin loads the admin area during that failure
- Then the admin sees a clear failure/unknown state for the "Incident
  reports" entry point, distinct from and never rendered as a zero count —
  a false "nothing to see" is worse than a visible error, the same
  principle REQ-512 already establishes for its sibling badge
- And a stale-but-still-valid cached count (per the caching criterion
  above) may continue to be served during a transient GitHub failure, but
  once the cache itself has nothing to serve, the failure state above is
  shown rather than defaulting to zero

**Authorization boundary:**
- Given a request to `GET /admin/incident-reports`
- When the caller has no valid session
- Then the request is rejected with `401`
- Given a request to `GET /admin/incident-reports`
- When the caller is authenticated but is not in the `Admin:UserIds`
  allowlist
- Then the request is rejected with `403`, using the same "Admin"
  authorization policy already enforced by REQ-509's
  `GET /admin/suggestions` and every other admin endpoint — no new
  authorization policy is introduced for this REQ
- Given a non-admin or guest is using the site
- Then no incident-report count or entry point is rendered anywhere in
  their UI — reachable only from within the already-gated `AdminScreen.tsx`

**Out of scope for this REQ:** any in-app list or detail view of
individual incident issues, or any ability to resolve/close/triage them
from the app — that is exactly the review-queue ADR-0064 already rejected
for REQ-903 itself, and this REQ does not reopen it; live/polling/
websocket updates (no push mechanism exists anywhere in this system);
notification for anything other than open, `user-reported`-labeled issues
in this repository (no other label or repo is read); any new persistence
table for incident reports — this REQ reads GitHub directly, on demand,
through the server-side cache described above, never a locally-stored
record of issues.

**Test level:** Unit (a positive open-issue count renders a count; a zero
count renders no badge; a GitHub failure renders the distinct
failure/unknown state, never a zero), API (`GET /admin/incident-reports`
against a stubbed/mocked GitHub client — tests must never call the real
GitHub API — rejects `401`/`403` per the Admin policy; repeated requests
within the cache TTL do not each trigger a new call to the stubbed GitHub
client; a request after the TTL expires does), UI ("Incident reports" on
`AdminScreen.tsx` shows the count when open issues exist, shows nothing
when none exist, shows a visible failure state on a GitHub-call failure,
and its "view on GitHub" link opens the correct filtered issue list; no
count or entry point is rendered for a non-admin or guest)

---

### 4.12 xG Path generation and gameplay

**xG Path** is the second game hosted on the xG Arcade (see `CLAUDE.md` and
`architecture-document.md` for the platform/game boundary this section
must not cross). A puzzle targets one specific real player; the player
guesses that target from a progressively-revealed career path, one clue at
a time. This section is design-only — no xG Path code exists yet. Every
REQ below is written to the same standard as §4.1's xG Grid requirements,
but describes intended behavior for a game that has not been built,
not a claim about current behavior.

**REQ-1201 – xG Path target player eligibility**
> As the system, I want every xG Path puzzle to target a player with a
> well-defined, orderable career path, so every generated puzzle has a
> valid, revealable sequence of clues rather than one that runs out of
> content partway through.

- **Status: Implemented (Tier 0, S-081, ADR-0045; appearance threshold
  added 2026-07-27, ADR-0047).** `XGPathGameModule.
  GenerateInstanceAsync` (`XGArcade.Games.XGPath`) reads every player's
  full `PlayerCareerStint` set in bulk
  (`IPlayerStoreRepository.GetAllCareerStintsByPlayerAsync`) and applies
  `IsEligible` per candidate. "At least 3 distinct documented career club
  stints" is implemented as **≥3 stint rows**, not 3 distinct clubs — see
  ADR-0045 for why (`PlayerCareerStint`'s own doc comment explicitly allows
  two rows at the same club, e.g. a loan then a later return). **(2026-08-17,
  S-138, ADR-0074: this ≥3-stint-row floor is RETAINED — it did not turn
  out to be removable — but its justification changed; see the status note
  below. The "not 3 distinct clubs" READING above, specifically, is what's
  superseded: that textual question no longer matters, since eligibility's
  club-quality signal is now a separate, explicit distinct-club-count
  condition rather than resting on how "3 distinct... stints" gets read.)**
  "Chronological order determinable from start/end dates" is implemented as: reject if any
  two stints share an identical `(StartYear, EndYear)` pair, including two
  simultaneously "ongoing" stints (`EndYear` both `null`) — see ADR-0045.
  The "at least one seeded-club stint" check compares `PlayerCareerStint.
  ClubName` against `ICategoryValueRepository.GetClubsAsync` (`ClubDefinition.
  Name`), the same reference table GridGameModule already reads (REQ-109) —
  never a second path to `ClubDefinition` — **and** that stint's
  `AppearanceCount` must be either unknown (`null`) or at least 20
  (`MinAppearancesAtSeededClub`, ADR-0047) — a known, sub-threshold count
  (e.g. a one-off loan appearance) does not count toward eligibility, but
  an unknown count still does, since Wikidata's P1350 qualifier being
  absent isn't evidence of a fringe career. **(This 1-club threshold was
  raised to 2 DISTINCT qualifying seeded clubs 2026-08-17, S-138, ADR-0074
  — see the status note below for the current rule; the 20-appearance-or-
  unknown per-club bar itself is unchanged, now applied per-club to 2 clubs
  instead of 1.)** The REQ-112 pool-membership
  criterion is met **by construction, not by a runtime check**: at the time
  this eligibility check was built, `Player` had no `BirthYear`/`Gender`
  field at all to check against — `Player.BirthYear` was added later
  (REQ-1207, S-082) for xG Path's own age clue, not for pool filtering, and
  this eligibility check still does not read it — the restriction is
  enforced entirely upstream at Wikidata-query time (ADR-0025), the same
  reasoning `GridGameModule` already relies on for not re-checking this at
  runtime either.
- **Status note (2026-08-02, bug-bundle fix): familiarity filter added
  (ADR-0056).** Real player feedback: a structurally eligible target can
  still be an obscure, unrecognizable career journeyman, since none of the
  three checks above say anything about fame. `PathEligibilityService.
  GetEligiblePlayerIdsAsync` now runs a familiarity filter
  (`IPlayerFamiliarityService`/`PlayerFamiliarityService`, Wikipedia sitelink
  count via the new `IWikidataClient.QuerySitelinkCountsByQidsAsync`) on top
  of the structural checks below, before target selection — see ADR-0056 for
  the full decision, the alternatives considered (total appearances, trophy
  won), and the fail-open contract on a Wikidata failure or data gap.
- **Status note (2026-08-08, bug fix, see REQ-1203's own dated status note
  for the full write-up): the "3 distinct documented career club stints"
  check below now excludes leftover pre-2026-08-02 youth/age-grade
  national-team `PlayerCareerStint` rows before counting.** Without this,
  a candidate with fewer than 3 REAL club stints could still pass this
  check purely because leftover junk rows (e.g. "Spain national under-16
  association football team") padded the row count past 3.
  `PathEligibilityService.GetEligiblePlayerIdsAsync` now filters via the new
  `PathCareerStintFilter.ExcludeNationalTeams` (named `ExcludeYouthNationalTeams`
  at the time of this note; renamed 2026-08-10 — see below) immediately
  before `IsEligible` runs. This REQ's own acceptance criteria below are
  unchanged in wording — "3 distinct documented career club stints" always
  meant real ones; this closes a gap where already-persisted junk data
  could make that check pass incorrectly, the same class of gap REQ-1203's
  2026-08-02 status note closed for the club-reveal display path.
  **Broadened 2026-08-10:** the filter this note describes now matches ANY
  national team, senior or youth, not just youth/age-grade rows — see
  REQ-1203's own 2026-08-10 status note for the full reasoning; this
  eligibility-check call site is otherwise unchanged.
- **Status note (2026-08-17, S-137, Epic 12 — ADR-0073, superseding ADR-0045
  on this one point): xG Path now additionally requires
  `Player.BirthYear >= 1975`, a second, xG-Path-only eligibility floor
  layered on top of (not replacing) REQ-112's own 1939 floor.**
  `PathEligibilityService.GetEligiblePlayerIdsAsync` checks `Player.BirthYear`
  directly, once per candidate — a player-level fact, evaluated alongside
  `IsEligible`, not inside `PathCareerStintFilter`, since it is not a
  stint-level fact. This is deliberately independent of REQ-112's 1939
  floor, which remains enforced entirely upstream at Wikidata SPARQL query
  time and shared with xG Grid's own pool (see the original bullet above);
  raising that shared floor to 1975 was out of scope, since it would also
  narrow xG Grid's pool — hence a second, additive, xG-Path-only check
  instead of a change to the shared one. **Fail-closed on
  `BirthYear == null`:** a candidate with no recorded birth year is
  excluded, not included — matching this codebase's established
  fail-closed convention (ADR-0070; REQ-211's own budget-exhausted and
  `Enabled = false` fail-closed branches) over silently admitting a player
  xG Path cannot actually verify meets the new bar. The boundary is
  inclusive: `BirthYear == 1975` is eligible, `BirthYear == 1974` is not.
  See S-141 for the planned follow-up to re-verify the eligible-pool size
  once this and Epic 12's other narrowing changes (S-138–S-140) have
  landed together.
- **Status note (2026-08-17, S-138, Epic 12 — ADR-0074, superseding ADR-0045's
  Decision §3 textual reasoning and ADR-0047 in full): eligibility now
  requires BOTH ≥3 total documented stint rows AND 2 DISTINCT qualifying
  seeded clubs, not 1 — the old ≥3-stint-row check is RETAINED, not
  dropped, but re-justified.** The original S-138 backlog text proposed
  dropping the ≥3-stint-row floor entirely as "redundant" once a 2-club
  check existed; architecture/quality review of the resulting diff found
  that reasoning incomplete — 2 distinct qualifying seeded clubs only
  implies ≥2 total rows, not ≥3, and a genuine 2-stint candidate (both
  qualifying seeded clubs, no third row) would pass eligibility and break
  REQ-1203's `PathClueSequenceBuilder`, which divides a target's stint
  count across exactly 3 fixed club-reveal turns and assumes ≥3 (for
  `N=2` it produces turn sizes `[0, 1, 1]` — an empty first clue turn).
  `PathEligibilityService.IsEligible` therefore keeps the row-count floor,
  renamed `MinDocumentedStintCount` (same value, 3; the old name
  `MinStintCount` and its original ADR-0045-textual-reading justification
  are gone, not the check itself) — see this REQ's own corrected bullet
  above — **and, as a separate, additional condition**, counts the number
  of DISTINCT seeded `ClubDefinition` club NAMES — not stint rows; two
  stints at the same seeded club (e.g. a loan then a later return) still
  count once, not twice — among the candidate's stints where each
  individual qualifying stint's `AppearanceCount` is either unknown
  (`null`) or at least `MinAppearancesAtSeededClub` (20), the same per-club
  bar ADR-0047 established, carried forward unchanged and now applied
  per-club to 2 clubs (`MinQualifyingSeededClubs`) instead of to 1. The
  chronological-order-determinable check (ADR-0045, unrelated to this note)
  and the `BirthYear >= 1975` floor (2026-08-17, S-137, ADR-0073, above) are
  both completely unchanged and evaluated independently of this note.
  `IPlayerCareerStintRepository.GetCareerStintCandidatePlayerIdsAsync`'s
  narrowing pre-filter gained a `minTotalStintCount` parameter alongside
  the renamed `minSeededClubCount` (from `minStintCount`), keeping both
  conditions as its over-inclusive superset: "≥3 total rows AND ≥2 distinct
  seeded club names among a player's stints," still ignoring the per-club
  appearance-count sub-condition for the same reason as before (that only
  narrows further, and the cheap projection this method reads doesn't
  carry `AppearanceCount`) — it remains a true superset of `IsEligible`'s
  real candidates. **This note supersedes, without editing them in place,
  the "at least one of those stints must be at a club present in...
  `ClubDefinition`" acceptance-criteria bullet below on the club-count
  point only** — the "at least 3 distinct documented career club stints"
  bullet remains an accurate description of current behavior (the row
  count is still 3, just no longer justified by a "3 distinct clubs"
  reading of that text) and is NOT superseded by this note. Treat this
  note, together with the still-accurate stint-count bullet, as the
  current rule. See ADR-0074 for the full reasoning, the alternatives
  considered, and why the old check was retained rather than dropped as
  originally proposed. See S-141 for the planned follow-up pool-size
  re-verification after Epic 12's S-138–S-140 narrowing changes land
  together.
- **Status note (2026-08-18, S-161, Epic 19 — ADR-0079, additive to
  ADR-0073 on this REQ, not a supersession of it): xG Path now additionally
  requires `Player.Position` to be non-null and non-empty, a second,
  independent field-level eligibility floor alongside the `BirthYear >=
  1975` floor above (2026-08-17, S-137, ADR-0073).** `PathEligibilityService.
  GetEligiblePlayerIdsAsync` checks `Player.Position` directly, once per
  candidate, at the same call site and in the same manner as the
  `BirthYear` check above — a player-level fact, evaluated alongside
  `IsEligible`, not inside `PathCareerStintFilter`, since it is not a
  stint-level fact. This closes a gap surfaced by a 2026-08-18 user QA pass
  over freshly-generated xG Path rounds (`docs/backlog.md` Epic 19): a
  puzzle for a structurally eligible target rendered "Position: not
  available" on the puzzle screen because `Player.Position` was `null` for
  that row. `Player.Position` staying `null` forever for a subset of rows
  is already-documented, deliberate REQ-1207 behavior (a data gap, not a
  code bug) — but nothing previously stopped a `Position == null`
  candidate from being SELECTED as a puzzle target in the first place,
  unlike `BirthYear`, which ADR-0073/S-137 already excludes on `null`.
  **Fail-closed on `Position == null` or `Position == ""`:** a candidate
  with no recorded position is excluded, not included — the same
  fail-closed convention this REQ's `BirthYear` check above already
  established (ADR-0070; ADR-0073), applied here to a second, independent
  field. This check is completely independent of the `BirthYear >= 1975`
  floor and of REQ-112's pool-membership check — a candidate can fail
  either, both, or neither, and evaluation order does not matter since all
  are simple boolean conditions. See ADR-0079 for the full reasoning and
  the alternatives considered.
- Given a candidate player is being considered as an xG Path puzzle target
- When the candidate is evaluated for eligibility
- Then the player must have at least 3 distinct documented career club
  stints, each with a chronological order determinable from start/end
  dates
- And at least one of those stints must be at a club present in the
  existing `ClubDefinition` reference table (REQ-109) — v1 needs no new
  club curation beyond the existing seeded set
- And that seeded-club stint's recorded appearance count, when known, must
  be at least 20 — a known count below that does not make the candidate
  eligible on the strength of that stint alone (ADR-0047); an unknown
  appearance count is treated as passing this check, not failing it
- And the player must already be a member of the existing player pool as
  restricted by REQ-112 (male, born 1939 or later) — xG Path reuses that
  population and defines no separate one of its own; this criterion is
  REQ-112's own population, unchanged by this REQ — the `BirthYear >= 1975`
  floor below is a separate, additional, xG-Path-only restriction layered
  on top of it, not a replacement for or a change to REQ-112 itself or to
  xG Grid's shared pool
- And (2026-08-17, S-137) the candidate's `Player.BirthYear` must also be
  1975 or later — a second, xG-Path-only floor, additive to and evaluated
  independently of the REQ-112 pool-membership check above; `BirthYear ==
  1975` is eligible (boundary, inclusive), `BirthYear == 1974` is not; a
  candidate whose `BirthYear` is `null` fails this check (fail-closed,
  excluded) rather than being treated as passing it — this is the opposite
  of the "unknown appearance count passes" treatment used for the
  seeded-club-stint check above, which does not apply here
- And (2026-08-18, S-161) the candidate's `Player.Position` must also be
  non-null and non-empty — a second, independent, xG-Path-only floor,
  additive to and evaluated independently of both the `BirthYear >= 1975`
  floor above and the REQ-112 pool-membership check; a candidate whose
  `Player.Position` is `null` or an empty string fails this check
  (fail-closed, excluded) rather than being treated as passing it, the
  same fail-closed treatment as the `BirthYear` check above, now applied
  to a second field
- And (ADR-0056, added 2026-08-02) the player must be judged "familiar
  enough" by the familiarity filter — a Wikipedia sitelink count that
  resolves to at least the configured threshold — UNLESS the filter itself
  could not run (a Wikidata failure, or no candidate in the pool having a
  usable `WikidataQid`), in which case this check is skipped for that
  generation rather than blocking it (REQ-103's established "never block
  round generation on a Wikidata failure" reasoning)
- And a candidate failing any of these checks is never selected as a
  puzzle target

**Test level:** Unit (eligibility check accepts/rejects fixtures covering
each rule independently — fewer than 3 stints, an undeterminable stint
order, no stint at a seeded club, a seeded-club stint below/at/unknown
appearance count, a player outside REQ-112's pool — the last of these
confirmed by inspection/schema absence rather than a runtime fixture,
since `Player` has no field that could represent "outside the pool"; see
`XGPathGameModuleTests`'s own class doc comment. `BirthYear >= 1975` floor
(2026-08-17, S-137): unlike the REQ-112 pool-membership case above,
`Player.BirthYear` is a real field this eligibility check reads directly,
so its boundary is covered by runtime fixtures, not inspection —
`BirthYear == 1975` (included, boundary), `BirthYear == 1974` (excluded),
and `BirthYear == null` (excluded, fail-closed) in `PathEligibilityServiceTests.cs`
only, per the backlog story's own acceptance criteria — this check lives in
`PathEligibilityService.GetEligiblePlayerIdsAsync`, not `PathCareerStintFilter`,
so `PathCareerStintFilterTests.cs` carries only an explanatory comment
noting why this rule has no stint-level surface to test, not a fixture
case. `Player.Position` eligibility floor (2026-08-18, S-161): same shape
as the `BirthYear` boundary immediately above — `Position == null`
(excluded, fail-closed) and a non-null `Position` (e.g. `"Forward"`,
included, positive control) are covered by fixtures in
`PathEligibilityServiceTests.cs` only, this check likewise living in
`PathEligibilityService.GetEligiblePlayerIdsAsync` rather than
`PathCareerStintFilter`, for the same reason. ADR-0056's familiarity
filter: `PathEligibilityServiceTests` covers the eligibility-pipeline-level wiring — below
threshold, at/above threshold, structural-ineligibility candidates never
even reaching the filter — via `FakePlayerFamiliarityService`;
`PlayerFamiliarityServiceTests` (`XGArcade.DataSync.Tests`) covers the real
implementation directly — threshold boundary, unresolved-QID exclusion,
fail-open on a Wikidata failure, fail-open when nobody in the pool can be
checked, and batching above `PlayerFamiliarityService.BatchSize`.
`WikidataClientTests` covers `QuerySitelinkCountsByQidsAsync`'s own query
shape and error contract. Youth-national-team junk-row exclusion
(2026-08-08 bug fix): `PathEligibilityServiceTests.
REQ1203_GetEligiblePlayerIdsAsync_CandidateWithTwoRealStintsPaddedByYouthNationalTeamJunkRows_NeverSelected`
and its positive-control sibling
`REQ1203_GetEligiblePlayerIdsAsync_CandidateWithTwoQualifyingSeededClubStints_StillEligible_DespiteYouthNationalTeamJunkRows`
(renamed 2026-08-17, S-138, from `...CandidateWithThreeRealStints_StillEligible...`
to match the current 2-club fixture shape; behavior covered is unchanged)
cover this eligibility-check-level fix directly; `PathCareerStintFilterTests`
covers the shared filter itself in isolation.)

**REQ-1202 – Round structure: a small, fixed set of puzzles**
> As a player, I want each xG Path round to contain a small, fixed number
> of puzzles, so a round is a bounded, comparable challenge every time.

- **Status: Implemented (Tier 0, S-081; round-scheduling wiring added
  2026-07-28, S-084, ADR-0051).** `PathTemplate.PuzzleCount` is
  `GenerateInstanceAsync`'s N (3-5) — still no admin-facing seeding surface,
  but round generation itself is now scheduled: a second
  `RoundSchedulingOptions` instance (`GameKey = "xg-path"`, its own
  configured `RoundDuration`) is resolved via the new
  `IRoundSchedulingOptionsResolver`, and `POST /internal/generate-round`
  (with `gameKey=xg-path`) produces a real `PathTemplate` via the new
  `PathTemplateResolver`'s find-or-create-by-`PuzzleCount` (defaulting to 4,
  `Games.XGPath.PathGenerationOptions`) — at the time this shipped, the same
  `generate-round.yml` daily cron xG Grid used, not a second scheduled job
  (ADR-0051). **As of S-136 (ADR-0072):** xG Path now has its own
  `generate-path-round.yml` daily cron, independent of xG Grid's
  `generate-grid-round.yml` — see ADR-0072 for why splitting is now safe.
  `PickDistinct` selects N
  eligible players uniformly at random, without replacement, persisting one
  `PathPuzzle` per selected target inside a new `PathInstance`; an eligible
  pool smaller than N throws `PathGenerationException` rather than
  generating fewer puzzles. `PathPuzzle.Id` is the cell id
  `GetCellIdsAsync` returns. `Round.GameKey`/`GameInstanceId` wiring is
  unchanged — no new Core-side reference (ADR-0003 unaffected).
  `ScoreSubmissionAsync`/`GetMaxAttemptsForCellAsync` (REQ-1204/1205) are now
  implemented too (S-082) — see those REQs' own status notes.
- Given an xG Path round is generated with a configured puzzle count N
  (3-5, configurable — the same spirit as REQ-102's configurable grid size)
- When the round instance is created
- Then exactly N puzzles are generated, each targeting a distinct eligible
  player (REQ-1201) — no two puzzles in the same round instance target the
  same player
- And each puzzle is represented as one cell in the existing generic
  `IGameModule`/`Round` model (ADR-0003) — `Round` references the xG Path
  instance via the existing opaque `GameKey` (`"xg-path"`)/`GameInstanceId`
  pair, unchanged from how xG Grid does this today; this REQ does not
  change how `Round` references any game instance

**Test level:** Unit (built S-081 via `XGPathGameModuleTests`; this REQ is
about round *structure*, not the read endpoint — REQ-1203's own `GET
/path/current` (S-082) now exposes puzzle/clue data over the API). As of
S-084, round-structure-level API test coverage exists too:
`RoundGenerationServiceTests.cs` proves REQ-301/REQ-302 hold for
`"xg-path"` resolved through the same service instance with its own
configured `RoundDuration`, and `RoundEndpointTests.cs` covers real
end-to-end `POST /internal/generate-round?gameKey=xg-path` generation
(own `RoundDuration`, `PathTemplateResolver` find-or-create, correct
puzzle count), an omitted-`gameKey` regression, and the unrecognized-
`gameKey` 400.

**REQ-1203 – Clue reveal order and content**
> As a player, I want clues about the target player's career revealed in a
> fixed, least-narrowing-first order — every documented club, then
> progressively more identifying information — so solving the puzzle is a
> genuine progressive challenge rather than trivially easy or unfairly hard
> from the start.

- **Status: Implemented (Tier 0, S-082, 2026-07-27).** `PathClueSequenceBuilder`/
  `PathClueTurn` (`XGArcade.Games.XGPath`) build the full 7-turn sequence
  described below; `GET /path/current` (`XGArcade.Api.Path.PathEndpoints`)
  is the new client-facing read path, returning only the turns the
  requesting player's own attempt count has unlocked so far — see
  `docs/architecture-document.md` COMP-11/§6.2b for the endpoint's shape and
  ADR-0016/ADR-0048 for why it reads `PathInstance`/`PathPuzzle` directly
  rather than through `IGameModule`.
- **Status note (2026-07-27): position/nationality/age data prerequisite —
  now resolved.** This REQ's position, nationality, and age clues assumed
  `Player.Position`/`Player.BirthYear` (and, for nationality, an existing
  `PlayerAttribute` "nationality" row) would be populated — REQ-1207
  (folded into S-082) built the Position/BirthYear sourcing from Wikidata,
  their set-once persistence rule, and the "null renders as 'not
  available,' never skips a turn" contract this REQ's implementation
  honors, so a data gap never shrinks a puzzle's clue count below the fixed
  7 (REQ-1205/1206). The pre-existing nationality-row gap REQ-1207's own
  scope note flags (a player who only ever entered via Club×Club sync has
  no `PlayerAttribute` "nationality" row) is unchanged by S-082 — that
  clue still renders as "not available" for such a player, per the same
  contract.
- **Status note (2026-08-02, bug-bundle fix): target-player reveal on
  attempt-cap exhaustion.** `GET /path/current`'s per-puzzle
  `CurrentPathGuessResponse.ResolvedPlayerName`/`ResolvedPlayerPhotoUrl`
  were originally gated on the guess's `IsCorrect` flag alone, so a puzzle
  that locked via REQ-1205's 7-attempt cap without ever being solved never
  revealed who the target player was — the player had no way to find out.
  `PathEndpoints.cs`'s own code comment described the intended boundary as
  "never leak the answer for an unsolved puzzle," which conflated
  "unsolved" with "still live"; those stopped being the same thing the
  moment an exhausted-attempts puzzle needed its answer revealed too. Fixed
  to gate on `Locked` (solved OR attempt cap exhausted) instead of
  `IsCorrect` — the correct boundary is "never leak the answer for a
  puzzle the player can still guess on," which `Locked` already expresses
  exactly. No DTO shape change: `CurrentPathGuessResponse` already carried
  both `Locked` and `IsCorrect` separately, so the frontend can still
  distinguish "solved" from "revealed but failed" from the same response.
- **Status note (2026-08-02, bug-bundle fix): national team caps were
  leaking into the club-reveal clues, violating this REQ's own "national
  team caps/appearances are never revealed as a clue" acceptance criterion
  below.** Wikidata models national-team caps under the same P54 ("member of
  sports team") property as club membership — `WikidataClient.
  QueryPlayerCareerStintsByQidsAsync` (ADR-0054, xG Path's full-career fetch)
  had no exclusion for this, so a target's national team (e.g. "Switzerland
  men's national football team") could appear as a "club" alongside their
  real clubs, both in `PathClueSequenceBuilder`'s club-reveal turns and in
  REQ-1201's own stint-count eligibility check. Fixed by excluding any
  `?club` that is (transitively, via P279 subclass) an instance of
  Wikidata's Q6979593 "national association football team" class — see the
  query builder's own code comment in `WikidataClient.cs` for the exact
  SPARQL clause.
- **Status note (2026-08-03, bug fix): duplicate club-reveal nodes for the
  same real stint — fixed.** Reported directly by a player (screenshot):
  one real career stint surfaced as two separate club-reveal entries,
  "Liverpool" and "Liverpool F.C.," identical in every other field (start
  year, end year, appearance count). `WikidataClient.
  ParseCareerStintBindings` dedups career stints by exact `?clubLabel`
  string (there is no `?club` QID selected to key on instead — see that
  method's own code comment), so Wikidata's own statements attesting two
  label variants for what is the same real club produced two distinct,
  non-equal `WikidataCareerStintEntry` records instead of deduping into
  one. Fixed with a new `NormalizeClubName` step, run before the dedup
  HashSet sees each label, that strips a small, explicit set of trailing
  football-club legal-suffix tokens (`FC`/`F.C.`/`AFC`/`A.F.C.`) when they
  appear as a distinct trailing word — never a substring inside another
  word, and never a leading token (so "AFC Bournemouth" is untouched, since
  that's a different, legitimate club-naming convention, not a suffix
  variant of "Bournemouth"). Deliberately narrow rather than a general
  fuzzy-name matcher, to avoid conflating two different clubs that happen
  to share a name prefix. Tests in `WikidataClientTests.cs`.
- **Known, accepted limitation (2026-08-03, quality-gate finding):** the
  dedup HashSet above is still keyed on the full (`ClubName`, `StartYear`,
  `EndYear`, `AppearanceCount`) tuple — normalizing `ClubName` alone only
  collapses two rows that also agree on every other field. Two rows for
  what could plausibly be the same real stint (same normalized club, same
  start/end year) but that disagree on `AppearanceCount` — e.g. one row's
  P1350 qualifier absent (`null`), the other's present (`25`) — still do
  **not** merge and both survive as separate entries; this variant of the
  duplicate-node symptom is not fixed by this REQ's 2026-08-03 status note
  above. Deliberately not widened: treating a `null` `AppearanceCount` as
  "matches anything" risks merging two genuinely different stints at the
  same club with matching dates but different, both-known appearance
  counts — a correctness regression, not just a display one, and strictly
  worse than the display duplicate the fix above targets. If this variant
  is observed in practice it needs its own deliberate merge rule (and
  test), not a silent loosening of this tuple. Locked in by
  `WikidataClientTests.REQ1203_QueryPlayerCareerStintsByQidsAsync_DoesNotMergeSameClubAndDates_WhenAppearanceCountDiffers`.
- **Status note (2026-08-10, bug fix, ADR-0063): the limitation above is now
  partially fixed — a `null`-vs-populated `AppearanceCount` DOES merge; two
  different, both-populated `AppearanceCount` values still do not.** A real
  duplicate-node bug report showed exactly the null-vs-populated shape this
  REQ's 2026-08-03 note above flagged as a known gap (e.g. "AC Milan 25
  apps" / "AC Milan 95 apps," "Real Sociedad 2 apps" / bare "Real
  Sociedad"). `WikidataClient.ParseCareerStintBindings` (via a new
  `MergeCareerStintEntries` helper) and `DuplicateCareerStintCleaner` (the
  retroactive cleanup for the ~608K-row table, both its existing Step 1 and
  a new same-`ClubName` Step 2) now treat a `null` `AppearanceCount` on one
  side and a populated value on the other as provably the same stint —
  `null` means "unknown," not "a different number" — and merge to the
  populated value. The correctness-risk carve-out this REQ's 2026-08-03
  note established is deliberately **unchanged**: two rows with DIFFERENT,
  both-populated `AppearanceCount` values are still never merged (a loan-
  and-return spell, for example, could genuinely be two different stints)
  — this is not a full fix for the 2026-08-03 limitation, only the
  null-vs-populated slice of it. This widening required (and is documented
  in) ADR-0063, since ADR-0059's own "For AI agents" section required a
  fresh ADR before `DuplicateCareerStintCleaner`'s provable-only matching
  was widened at all.
- **Status note (2026-08-04, bug fix, ADR-0059): duplicate club-reveal nodes
  from a cross-writer label mismatch — fixed.** A second, distinct cause of
  the same duplicate-node symptom the 2026-08-03 fix above only partly
  addressed: two independent writers of `PlayerCareerStint.ClubName` used
  different naming conventions with no QID-based cross-check —
  `WikidataLookupService.PersistCareerStintsAsync` wrote the canonical,
  hand-seeded `ClubDefinition.Name`, while `PlayerCareerStintRefreshService`/
  `PlayerCareerPrefetchService` wrote Wikidata's raw `?clubLabel` (only ever
  suffix-normalized, per the 2026-08-03 fix), so a genuine alternate-name
  variant more than a legal-suffix token apart (e.g. "Lyon" vs. "Olympique
  Lyonnais," the same real club, same Wikidata QID `Q704`) still produced
  two separate rows for one real stint — the 2026-08-03 fix's own
  `NormalizeClubName` step never caught this, since it only strips
  `FC`/`F.C.`/`AFC`/`A.F.C.`-style suffixes. Fixed by threading the
  underlying Wikidata `?club` QID through `WikidataClient`'s career-stint
  query (`WikidataCareerStintEntry.ClubQid`) and having
  `PlayerCareerStintRefreshService`/`PlayerCareerPrefetchService`
  canonicalize each fetched stint's `ClubName` to the matching seeded
  `ClubDefinition.Name` when the QID resolves, falling back to the
  suffix-normalized label otherwise. This also fixes, for free, a related
  correctness gap in `GetCareerStintCandidatePlayerIdsAsync` (REQ-1201's
  own eligibility check): a stint persisted under a non-canonical label
  previously never counted toward a player's eligibility even when it was
  genuinely at a seeded club. A narrow, provable-only cleanup CLI verb
  (`dotnet run -- clean-duplicate-career-stints`, `DuplicateCareerStintCleaner`)
  retroactively removes already-persisted duplicate rows where a
  canonical-named counterpart for the exact same stint already exists —
  deliberately not a full purge-and-reseed of the ~608K-row table; see
  ADR-0059 for the full reasoning, including why that would be
  disproportionate for what is presently a cosmetic-only bug (xG Grid never
  reads this table, so scoring is unaffected).
- **Status note (2026-08-08, bug fix): leftover pre-2026-08-02 youth/
  age-grade national-team rows were still leaking into club-reveal clues —
  fixed with a read-time filter.** Reported directly by a player in user
  testing (screenshots): clue nodes like "Spain national under-16
  association football team," "Spain national under-17 association
  football team," "Italy national under-20 football team," and "Italy
  national under-21 football team" appeared before the target's real club
  career, violating this REQ's own "national team caps/appearances are
  never revealed as a clue" acceptance criterion below — the same
  criterion the 2026-08-02 fix above already exists to protect. Root
  cause: that 2026-08-02 fix changed `WikidataClient.
  QueryPlayerCareerStintsByQidsAsync`'s query so no NEW national-team row
  (senior or youth) is ever fetched again, but it could not retroactively
  remove rows already sitting in the ~608K-row `PlayerCareerStint` table —
  `PlayerCareerStintRefreshService.BuildNewStintsByPlayerId` is documented
  "additive only, never a wipe-and-replace" (its own doc comment), so any
  national-team row fetched before that date is still there today, and
  nothing deletes it. Fixed with a new `PathCareerStintFilter`
  (`XGArcade.Games.XGPath`), a pure, read-time filter applied at both
  places `PlayerCareerStint` rows are read for xG Path: `GET /path/current`
  (`PathEndpoints.cs`, immediately before the stint list reaches
  `PathClueSequenceBuilder.BuildSequence`) and `PathEligibilityService.
  GetEligiblePlayerIdsAsync`'s REQ-1201 eligibility check (immediately
  before `IsEligible` counts a candidate's stints) — without the latter, a
  player with fewer than 3 REAL documented club stints could still pass
  REQ-1201's `MinStintCount` check purely on the strength of leftover junk
  rows padding the row count. Deliberately a read-time filter, not a
  DELETE/cleanup script in the style of ADR-0059's
  `DuplicateCareerStintCleaner`: unlike that cleanup, there is no QID
  stored on an already-persisted row to prove a match against, so a
  name-based DELETE against 608K rows would not be "provable" the way
  ADR-0059's canonical-name-exists check was — a name-based filter is safe
  for read-time exclusion (a false positive only skips a clue) but not for
  an irreversible row deletion. Scoped narrowly to match only what was
  actually reported: `PathCareerStintFilter.IsYouthNationalTeam` (renamed
  `IsNationalTeam` 2026-08-10 — see the superseding status note below)
  matches "national" followed by an age-grade "under-`\d+`" marker
  (`national\s.*\bunder-\d+\b`, case-insensitive) — deliberately NOT
  "national ... team" alone, which would also have wrongly stripped the
  valid senior-team clue ("Italy men's national association football
  team") the same reviewed screenshots showed rendering correctly in the
  same timeline. A "Basque Country regional football team" stint present
  in one screenshot was not flagged as a problem and is deliberately left
  alone — this fix does not extend to non-FIFA regional representative
  teams. The regex was not verified against a live Wikidata query from
  this sandbox (no `wikidata.org` access here); flagged for manual
  confirmation against real production rows if it's found to under- or
  over-match in practice.
- **Status note (2026-08-10, bug fix — supersedes the youth-only scoping in
  the 2026-08-08 note above, which is not deleted but is no longer current
  reasoning): senior national teams were still leaking into club-reveal
  clues — the youth-only scope was reopened and the filter broadened to
  match any national team.** A new bug report (screenshot) showed "Italy
  men's national association football team" rendering WITH an appearance
  count ("30 apps") as a club-reveal clue — the exact senior-team case the
  2026-08-08 note above says was reviewed and confirmed rendering
  correctly; that judgment call is now known to have been wrong, or at
  least not durable. This REQ's own acceptance criterion below ("national
  team caps/appearances are never revealed as a clue... this clue type does
  not exist for xG Path") has no senior/youth carve-out in its wording —
  the youth-only scoping was a narrower reading than the REQ's own text
  supports, not something the REQ ever asked for. Fixed by renaming
  `PathCareerStintFilter.IsYouthNationalTeam`/`ExcludeYouthNationalTeams` to
  `IsNationalTeam`/`ExcludeNationalTeams` and broadening the pattern from
  `\bnational\s.*\bunder-\d+\b` (youth/age-grade only) to
  `\bnational\b.*\bteam\b` (any national team, senior or youth) — matching
  "national" and "team" as independent word-bounded tokens covers every
  observed label shape (with or without an age-grade marker, with or
  without "men's"/"women's", with or without "association") without a
  combinatorial list of exact phrasings. The non-FIFA-regional-side
  carve-out ("Basque Country regional football team" stays a valid clue) is
  preserved, but is now understood to be **incidental, not a deliberate
  policy exemption**: this filter has no FIFA-affiliation signal at all and
  matches purely on label wording — "Basque Country regional football
  team" is untouched only because its label never contains the word
  "national," not because of any non-FIFA-side rule. A non-FIFA side whose
  Wikidata label nonetheless says "national team" (e.g. hypothetically
  "Catalonia national football team") IS excluded, the same as any FIFA
  member national team — this is intentional under the REQ's own unqualified
  acceptance criterion, not an oversight. See `docs/architecture-document.md`
  COMP-11's matching 2026-08-10 status note.
- **Status note (2026-08-18, S-139, Epic 12 — ADR-0075): B-team/reserve-team
  rows were also leaking into club-reveal clues — a new, separate read-time
  filter now closes the same class of violation for that category too.**
  `PathCareerStintFilter.IsBTeam`/`ExcludeBTeams`, parallel in shape to the
  existing `IsNationalTeam`/`ExcludeNationalTeams` above, excludes a
  reserve/development-side stint (e.g. "Real Madrid Castilla," "Barcelona
  B," "Bayern Munich II") from ever surfacing as a raw clue-reveal club
  name — no B-team/tier concept exists anywhere in this schema
  (`ClubDefinition` has no type/tier field, no B-team club is seeded), so
  such a stint previously passed every check unfiltered. This REQ's own
  acceptance criterion below ("national team caps/appearances are never
  revealed as a clue") is worded specifically around national teams and is
  **not being reinterpreted here** — B-teams were never textually covered
  by it, and this note does not claim otherwise. What this closes is the
  same underlying class of violation the national-team fixes above address
  (a non-answer-worthy "club" name leaking as a clue), via the same
  mechanism (a conservative, hand-verified, read-time label-matching
  regex), not an expansion of this REQ's own wording. `ExcludeBTeams` is
  chained alongside (never instead of) `ExcludeNationalTeams` at both of
  that filter's existing call sites — `PathEligibilityService.
  GetEligiblePlayerIdsAsync`'s REQ-1201 eligibility check and `GET
  /path/current`'s (`PathEndpoints.cs`) clue-reveal path — since the two
  filters exclude disjoint categories and both must run. See ADR-0075 for
  the full pattern, its alternatives considered, and its explicitly
  acknowledged false-positive risk (a bare `B`/`II` token against a
  not-currently-seeded club whose real name happens to use one, e.g.
  Faroese "B36 Tórshavn"-style names) — not verified against live Wikidata
  or the production `PlayerCareerStint` table, hand-verified only against
  the 33 currently-seeded clubs. Covered by `PathCareerStintFilterTests.cs`
  (including a parametrized `REQ1203_IsBTeam_CurrentSeededClubNames_
  ReturnsFalse` false-positive check against all 33 seeded clubs),
  `PathEligibilityServiceTests.cs`, and `PathEndpointTests.cs`.
- **Status note (2026-08-18, S-139 fast-follow): confirmed "always
  `PuzzleCount` puzzles per round, never an empty club-reveal turn" already
  holds as a structural guarantee — no runtime code change needed.**
  Product concern raised: could a player ever be shown a puzzle whose
  club-reveal turns are empty (`PathClueSequenceBuilder.SplitIntoTurns`
  produces a zero-sized turn only when the sanitized stint count is
  `< 3`)? Traced end to end: `PathEligibilityService.
  GetEligiblePlayerIdsAsync` only ever selects a target after checking
  `IsEligible` against the **sanitized** stint list (fetch raw stints →
  `ExcludeBTeams(ExcludeNationalTeams(...))` → `IsEligible`, never the
  reverse), and `MinDocumentedStintCount` (>= 3) is checked on that same
  sanitized list — so every puzzle generated by this pipeline structurally
  has >= 3 real stints before it ever exists. `GET /path/current`
  (`PathEndpoints.cs`) applies the identical filter chain to the same
  persisted stints, so its view can never diverge from what generation
  already verified. This ordering is now locked down with an explicit
  "must never change" invariant comment on `GetEligiblePlayerIdsAsync`'s
  `structurallyEligibleIds` computation, rather than left as an emergent
  property of the current code shape.
  **A read-time defensive assertion (log-and-continue on violation) was
  drafted and then deliberately reverted**: it did not actually satisfy
  "never show an empty clue" (the player would still see the degraded
  turn, just with a log line alongside it), and the only way to fully
  guarantee that — omitting the anomalous puzzle from the response — was
  rejected because it would break "always `PuzzleCount` puzzles per
  round." Since the guarantee already holds structurally for every puzzle
  generated by the current pipeline, and round durations are
  `>= 24h` (`RoundSchedulingOptions.RoundDuration`) while the underlying
  sanitize-before-eligibility fix has been in place since 2026-08-08, no
  currently-active round can be affected in practice — the only residual
  risk is a future filter change landing mid-round, which is theoretical
  and self-resolves within one round duration. No test changes; the
  existing `REQ1203_PathCurrent_Get_OnlyBTeamJunkRows_...`/
  `..._OnlyYouthNationalTeamJunkRows_...` tests (`PathEndpointTests.cs`)
  continue to document the (now-unreachable-via-normal-generation) degrade
  shape for a puzzle whose stints are seeded directly at the DB level,
  bypassing eligibility — not a live code path.
- **Status note (2026-08-18, S-140, bug fix): the "Basque Country regional
  football team" carve-out described in the 2026-08-08 and 2026-08-10 notes
  above is now closed — that carve-out is superseded, not current
  behavior.** Those two notes are left unedited as history, per this
  section's own convention, but their claim that a "Basque Country regional
  football team"-style stint "stays a valid clue" / "is preserved" no
  longer holds. Both notes already correctly identified this as
  **incidental, not a deliberate policy exemption** — the filter has no
  FIFA-affiliation signal and matches purely on label wording — and that
  reasoning is exactly why excluding "Catalonia national football team" but
  not "Basque Country regional football team" was an inconsistency to
  close, not a distinction to keep: both are non-club representative sides,
  and this REQ's acceptance criterion below draws no line between them.
  `PathCareerStintFilter.NationalTeamPattern` now also matches the
  word-bounded token "regional" paired with a trailing "team" or
  "representative" (alongside the existing "national" + "team" match), so
  both label shapes are excluded on the same principle. This REQ's own
  acceptance criteria wording (below) is unchanged — it already had no
  FIFA-affiliation qualifier, so this fix is an internal regex refinement,
  not a reinterpretation of the REQ. See `PathCareerStintFilter.cs`'s own
  2026-08-18 doc-comment correction and
  `PathCareerStintFilterTests.REQ1203_IsNationalTeam_
  NonFifaRegionalRepresentativeTeam_ReturnsTrue` (previously
  `..._NonFifaRegionalTeam_ReturnsFalse`, which pinned the inconsistency as
  correct behavior). No new ADR — confirmed a bug fix to ADR-0075's own
  Catalonia/Basque follow-up note, not a new eligibility-model decision.
- **Status note (2026-08-19, S-163, Epic 19 — ADR-0080): club-reveal clues
  can now carry an additional "inferred loan" annotation — a new
  `PathCareerStintFilter` heuristic, but unlike the two notes above this is
  not an exclusion, it's a display-only per-clue flag.** Reported directly
  (a puzzle matching David Beckham's real career): "Manchester United" and
  "Preston North End" rendered together in the same club-reveal turn with
  no indication that the Preston stint (1994-95) was a loan chronologically
  NESTED inside the Man Utd stint (1992-2003), not a sequential next club —
  a player reasoning about the sequence had no way to tell the two apart.
  `PathCareerStintFilter.IsInferredLoan(stint, allStints)` flags a stint as
  a probable loan when its `[StartYear, EndYear]` range is fully contained
  within a DIFFERENT club's concurrent stint range, called from
  `PathClueSequenceBuilder.BuildSequence` for every stint in a target's
  chronological list; the resulting `bool` flows through `PathClubClue.
  IsLoan` → `PathClubClueResponse.IsLoan` (`PathEndpoints.cs`) →
  `PathClubClue.isLoan` (frontend `lib/types.ts`), rendered by
  `PathTimeline.tsx` as a "(loan)" text qualifier next to the club name.
  **This is presentation-only and does NOT affect `PathEligibilityService`'s
  eligibility logic in any way** — unlike the `BirthYear >= 1975`/`Position`
  floors documented under REQ-1201, which gate whether a player can be
  SELECTED as a target at all, `IsInferredLoan` only annotates a clue that
  is already going to be revealed; `GetEligiblePlayerIdsAsync` never calls
  it, and every stint still surfaces as its own club-reveal clue regardless
  of the flag's value — an "eligibility floor" and a "clue annotation" are
  not the same thing and should not be conflated, since they act at
  entirely different points in the pipeline (target selection vs.
  clue-content presentation). Two edge cases resolved (see ADR-0080 for the
  full reasoning): an ongoing candidate stint (`EndYear == null`) is never
  itself flagged as a loan, but an ongoing stint CAN be the containing one
  for an already-ended candidate. **Like `NationalTeamPattern`/`BTeamPattern`
  above, this heuristic is NOT verified against live Wikidata or real
  production `PlayerCareerStint` data** (no `wikidata.org` or database
  access from this sandbox) — it is a pure date-range inference, explicitly
  framed as a deliberate experiment ("test out," S-163's own wording)
  rather than a load-bearing correctness claim, and is expected to need the
  same kind of iterative correction those two filters needed after landing
  (see their own dated notes above) once real false positives/negatives
  surface against production data.
- **Status note (2026-08-19, S-162, Epic 19 — ADR-0081): club-reveal clues
  no longer render chronologically-adjacent, identically-named
  `PlayerCareerStint` rows as separate entries.** A 2026-08-18 QA report
  showed a target whose real career included three consecutive Wikidata
  statements for the same club (e.g. a squad-list renewal or a
  sell-then-loan-back split across statements) rendered as three
  back-to-back club-reveal entries for the identical club name — reading as
  broken/duplicated data. `DuplicateCareerStintCleaner`/ADR-0063 cannot fix
  this: that class only ever DELETES a persisted row after proving it's the
  literal same real-world stint, and explicitly refuses to merge two rows
  with different, both-populated `AppearanceCount` values (they could be a
  genuine loan-and-return). `PathCareerStintFilter.CollapseAdjacentSameClub`
  is a different, narrower, read-time-only mechanism: it merges
  chronologically ADJACENT (nothing else in between) rows sharing an
  identical `ClubName` into one displayed entry — earliest `StartYear`,
  latest `EndYear`, and `AppearanceCount` summed only if every merged row's
  count is known (a `null` on any merged row makes the whole merged total
  `null`, deliberately NOT `DuplicateCareerStintCleaner`'s null-tolerant
  single-value-propagation rule, since appearance counts are additive
  across a continuous chapter and silently treating an unknown segment as
  contributing zero would understate a real total). No `PlayerCareerStint`
  row is ever deleted or mutated — this only changes what a already-fetched
  list looks like at the two places that turn it into eligibility/clue
  content. Applied identically, in the identical chain position (after
  `ExcludeNationalTeams`/`ExcludeBTeams`), at BOTH
  `PathEligibilityService.GetEligiblePlayerIdsAsync` (so
  REQ-1201's `MinDocumentedStintCount >= 3` floor is judged against the
  POST-collapse count, not the raw row count — a candidate whose real
  stints collapse to fewer than 3 chapters is correctly excluded, the same
  "never diverge between eligibility and display" invariant
  `ExcludeNationalTeams`/`ExcludeBTeams` already established) and
  `GET /path/current`'s clue-building path. A documented, intentional side
  effect: a player whose true single-club appearance total was split
  across two adjacent sub-threshold rows now correctly counts toward
  REQ-1201's seeded-club appearance-count bar once merged — see ADR-0081.
- Given a puzzle targeting a specific eligible player (REQ-1201), whose
  documented career has `N` club stints (`N >= 3`, guaranteed by REQ-1201's
  eligibility check, with no upper cap)
- When clues are revealed for that puzzle, with the player able to guess
  after each reveal
- Then every one of the player's `N` documented club stints is revealed —
  none are ever omitted for having "too many" clubs — spread across
  exactly 3 club-reveal turns, in chronological order (earliest first)
  overall and within each turn
- And the 3 turns' club counts are `N` divided into 3 as evenly as
  possible, smallest first: let `base = N div 3` and `remainder = N mod 3`;
  the first `3 - remainder` turns each reveal `base` clubs, and the last
  `remainder` turns each reveal `base + 1` clubs (e.g. `N=3` → 1-1-1;
  `N=4` → 1-1-2; `N=5` → 1-2-2; `N=10` → 3-3-4; `N=11` → 3-4-4) — the
  turn sizes are non-decreasing, so the first turn is never larger than
  the last
- And each club revealed in a turn includes the player's appearance count
  (games played) at that club when that data is known, bundled into the
  same turn, per club; when the appearance count is not known for a given
  club, that club is still revealed, without an appearance count, rather
  than being delayed or skipped
- And (2026-08-19, S-163) a club-reveal clue MAY additionally carry an
  inferred-loan indicator when that stint's date range is fully contained
  within a different club's concurrent stint range — this is an advisory,
  heuristic annotation only, not a guaranteed-correct employment-status
  claim, and its presence or absence never changes which clubs are
  revealed, their order, or their appearance counts
- And (2026-08-19, S-162) chronologically adjacent documented stints at the
  identical club are revealed as ONE club-reveal entry, not one per
  underlying `PlayerCareerStint` row — this collapsing changes what `N`
  (the total club count driving the 3-way turn split above) counts as one
  chapter, but never reorders or drops a real club, and never applies
  across a gap where a different club's stint sits in between
- And once all 3 club-reveal turns have happened and the player has not
  yet guessed correctly, exactly one further clue is revealed showing the
  start-end year range for every club stint already revealed (e.g.
  "2012-15, 2015-19, 2019-present") — one bundled clue covering all clubs
  at once, never one clue per club
- And if the player still hasn't guessed correctly, the following clues
  are then revealed one at a time, in this exact order and no other:
  position, then nationality, then age (or birth year)
- And national team caps/appearances are never revealed as a clue for
  this game — this clue type does not exist for xG Path
- And a correct guess submitted at any point stops the reveal sequence
  immediately — no further clue is ever revealed once the puzzle is
  solved (mirrors xG Grid's immediate lock on a correct guess, REQ-210)
- And a given puzzle's total clue count is therefore always **7** (3
  club-reveal turns + 1 bundled year-range clue + 3 fixed clues) —
  unlike the earlier design, this is now a fixed constant for every xG
  Path puzzle regardless of `N`, not a value that varies by target player
- **Status note (2026-08-04) — second consumer, no requirement change:**
  a product owner asked whether SCREEN-10 had the same round-end-time
  affordance SCREEN-01 has (REQ-303's 2026-07-21 addition); it didn't —
  `PathScreen.tsx` never rendered `CurrentPathResponse.endTime`, even
  though this endpoint has returned it since S-081/S-082, mirroring
  `CurrentRoundResponse.endTime` exactly (see this REQ's own Status note
  above on `GET /path/current` mirroring the grid read endpoint's shape).
  `PathScreen.tsx` now renders that field using the exact same shared
  formatter `GridScreen.tsx` already uses (`frontend/src/lib/roundTime.ts`'s
  `formatRoundEndTime`/`formatRoundEndTimeAccessibleLabel`) — same
  relative-duration bucket text and thresholds, same `"Ending soon"`
  fallback, same "computed once at fetch time, never a live tick"
  behavior, and the same accessible-name/keyboard-focus treatment, as a
  new `.path-screen__end-time` element next to the "xG Path" heading. This
  REQ's own acceptance criteria above (clue reveal order/content) are
  unaffected, and `GET /path/current`'s response contract is unchanged
  (`endTime` already present) — this is purely a second frontend consumer
  of REQ-303's already-specified indicator, applied to SCREEN-10; the
  bucket-format/threshold/accessible-name rules themselves are REQ-303's
  and are not restated here. Verified with a live dev-stack session
  (Postgres + dotnet API in local-e2e auth mode + Vite frontend), showing
  "Ends in 59m" etc. rendered correctly. Covered by a new
  `describe('REQ-303: round end-time indicator', ...)` block in
  `PathScreen.test.tsx`, mirroring `GridScreen.test.tsx`'s own block for
  the same three checks (bucketed relative text renders, accessible name
  exposes the absolute end time, indicator is keyboard-focusable) — the
  bucket-format logic itself remains exhaustively covered only by
  `lib/roundTime.test.ts`, not duplicated here.
- **Status note (2026-08-29, S-187, ADR-0091): a stored ongoing stint whose
  end date Wikidata later fills in no longer produces a duplicate club-reveal
  entry — fixed at all three career-stint reconciliation call sites, not
  just the two originally touched.** A fetched stint matching an existing
  row on `(ClubName, StartYear)` with a differing `EndYear`/`AppearanceCount`
  now completes that row in place rather than inserting a second one, via a
  new shared `CareerStintReconciler.Reconcile` primitive used by
  `PlayerCareerStintRefreshService.BuildNewStintsByPlayerId` (xG Path's own
  per-target refresh, ADR-0054), `PlayerCareerPrefetchService.FetchAndPersistBatchAsync`
  (the bulk sweep, including this story's own ADR-0090 rotation), and —
  closed in a follow-up commit after `architecture-reviewer` flagged it as an
  undocumented gap in the first review pass — `WikidataLookupService.PersistCareerStintsAsync`
  (xG Grid's own REQ-103 generation-time / REQ-211 guess-time byproduct
  writer). `StartYear`/`ClubName` are never corrected by this path at any
  call site — a wrong start year or club name remains explicitly out of
  scope, governed unchanged by ADR-0054's original "additive only, a
  previously wrong stint is not this method's concern" trade-off; this is a
  narrow, scoped carve-out from that trade-off (completing a previously-
  unknown, now-known end date on an otherwise-correct row), not a reversal
  of it. `UpdateCareerStintCompletionsAsync` (`IPlayerCareerStintRepository`)
  never touches `SequenceOrder` — a completed row's own `StartYear` never
  moves, so no re-sequencing pass is needed the way a genuinely new row
  insertion requires. `DuplicateCareerStintCleaner` (ADR-0059/ADR-0063) is
  unaffected and unchanged — its own full-tuple matching is now documented
  as strictly more conservative than this narrower live-write-path key, not
  an inconsistency to reconcile. See ADR-0091 for the full decision, why the
  three call sites' differing input shapes meant only the per-candidate
  decision (not the whole reconciliation loop) could be shared, and the
  explicit reconciliation with ADR-0054's Consequences section.

**Test level:** Unit, API (Unit: the 3-way club-count split for `N` at the
minimum (3), a non-multiple-of-3 value below 10, and a value at or above
10, per the worked examples above; appearance count present vs. unknown
within a multi-club turn; chronological order preserved both across and
within turns; the bundled year-range clue's content; the fixed
position/nationality/age order; the sequence halting immediately on a
correct guess at every possible point, including after each of the 3 club
turns individually — `PathClueSequenceBuilderTests`. API: `GET
/path/current` end to end, including auth, no-active-round 404, and the
"only the requesting player's own unlocked turns are returned" contract —
`PathEndpointTests`, S-082. National-team exclusion (2026-08-02 bug-bundle
fix): `WikidataClientTests.REQ1203_QueryPlayerCareerStintsByQidsAsync_
SentQuery_ExcludesNationalTeams` covers the query-text assertion — a real
national-team caps row is server-side excluded by WDQS itself, not
something this codebase's own parsing can independently verify from a
mocked response. Leftover-junk-row filtering (2026-08-08 bug fix):
`PathCareerStintFilterTests` covers `PathCareerStintFilter` directly and
purely (reported youth-national-team labels excluded; the senior team and
a non-FIFA regional side NOT excluded; a mixed real+junk stint list
filtered correctly; an all-junk list returns empty). `PathEligibilityServiceTests`
adds `REQ1203_GetEligiblePlayerIdsAsync_CandidateWithTwoRealStintsPaddedByYouthNationalTeamJunkRows_NeverSelected`
(a candidate with only 2 real stints must not become eligible just because
junk rows pad the row count past `MinDocumentedStintCount`, renamed
2026-08-17/S-138 from `MinStintCount`, same value) and
`REQ1203_GetEligiblePlayerIdsAsync_CandidateWithTwoQualifyingSeededClubStints_StillEligible_DespiteYouthNationalTeamJunkRows`
(renamed 2026-08-17/S-138 from `...CandidateWithThreeRealStints_StillEligible...`;
a genuinely eligible candidate must not be wrongly rejected just because
junk rows are also present). `PathEndpointTests` adds
`REQ1203_PathCurrent_Get_MixOfRealClubsAndYouthNationalTeamJunkRows_OnlyRealClubsRevealedAsClues`
(interspersed junk rows are filtered from both the club-reveal clues and
the bundled year-range clue, real clubs still shown in chronological
order) and `REQ1203_PathCurrent_Get_OnlyYouthNationalTeamJunkRows_
NoRealClubStints_HandledSensibly_NeverCrashes` (an already-generated
puzzle whose target has zero real stints after filtering still returns
the fixed 7-turn sequence with empty club-reveal/year-range turns, rather
than erroring — proving `PathClueSequenceBuilder`'s `SplitIntoTurns(0)`
degrades gracefully and this scenario can't arise for a NEWLY generated
puzzle now that the same filter also guards REQ-1201's eligibility check).
B-team/reserve-team exclusion (2026-08-18, S-139, ADR-0075):
`PathCareerStintFilterTests` adds `REQ1203_IsBTeam_CurrentSeededClubNames_
ReturnsFalse`, a parametrized case per one of the 33 currently-seeded clubs
in `ReferenceDataSeeder.cs` proving none false-positive-match `BTeamPattern`
(including the two closest near-misses, "RB Leipzig" and "Atletico
Madrid"), alongside direct positive-match coverage for the known
reserve-side label shapes. `PathEligibilityServiceTests` and `PathEndpointTests`
mirror the same "chained alongside `ExcludeNationalTeams`, at both call
sites" shape the 2026-08-08/2026-08-10 national-team tests above already
established, adapted to B-team rows.
Inferred-loan annotation (2026-08-19, S-163, ADR-0080):
`PathCareerStintFilterTests` adds `REQ1203_IsInferredLoan_*` covering the
full-containment positive case (a Beckham/Preston-shaped fixture), a
partial-overlap negative case, a no-overlap negative case, the
identical-date-range-different-club case (both stints flagged `true`), both
ongoing-stint edge cases (`REQ1203_IsInferredLoan_
CandidateStintItselfOngoing_ReturnsFalse` and `REQ1203_IsInferredLoan_
ContainingStintIsOngoing_EarlierEndedCandidate_ReturnsTrue`), and same-club
stints never self-flagging
(`REQ1203_IsInferredLoan_SameClubDifferentStintRecords_NeverSelfFlagged`).
`PathClueSequenceBuilderTests` adds
`REQ1203_BuildSequence_LoanShapedFixture_WiresIsLoanThroughForContainedStintOnly`,
proving the flag is wired end-to-end through `BuildSequence`'s output for
exactly the contained stint, never its container or an unrelated club.
Frontend: `PathTimeline.test.tsx` covers the "(loan)" text qualifier
rendering when a club clue is flagged `isLoan: true` and its absence when
`isLoan` is `false` or omitted.
Adjacent-same-club collapse (2026-08-19, S-162, ADR-0081):
`PathCareerStintFilterTests` adds `REQ1203_CollapseAdjacentSameClub_*`
covering a 2-row merge (summed count), a 3-row merge (one result, not
two), a same-club pair with a different club in between (does NOT merge),
one known + one unknown `AppearanceCount` in a run (merged result is
`null`, not the known value alone), an all-unknown run (`null`), an
ongoing last stint in a run (`EndYear` stays `null`, count still sums), a
single-stint passthrough, and an empty-input no-op. `PathEligibilityServiceTests`
adds `REQ1203_GetEligiblePlayerIdsAsync_CandidateWithThreeRawStintsButTwoPostCollapse_NeverSelected`
(a candidate whose raw row count meets `MinDocumentedStintCount` but whose
post-collapse chapter count does not must still be rejected) and its
positive-control sibling
`REQ1203_GenerateInstanceAsync_CandidateWithAdjacentSameClubPair_StillEligible_PoolDoesNotShrinkBelowPuzzleCount`
(a genuinely eligible candidate with an adjacent same-club pair is still
selected). `PathClueSequenceBuilderTests` adds a composition-level test
confirming `CollapseAdjacentSameClub` output feeds correctly into
`BuildSequence` (the builder itself has no collapse-awareness — collapse
is applied only by its two callers).
UI: **(2026-08-04 addition)** the round end-time
indicator's presence/wiring on SCREEN-10 is covered by
`PathScreen.test.tsx`'s `REQ-303: round end-time indicator` block, per
the status note above — the underlying format/bucket logic remains
`lib/roundTime.test.ts`'s alone.)
Career-stint completion on end-date fill-in (2026-08-29, S-187, ADR-0091):
`PlayerCareerStintRefreshServiceTests.cs` —
`REQ1203_S187_RefreshCareerStintsAsync_FetchedStintCompletesStoredOngoingStint_UpdatesInPlace_NotDuplicated`,
`REQ1203_S187_RefreshCareerStintsAsync_FetchedStintAtGenuinelyDifferentClub_StillInsertsNewRow`,
`REQ1203_S187_RefreshCareerStintsAsync_FetchedStintIdenticalToStored_RemainsANoOp`, and
`REQ1203_S187_RefreshCareerStintsAsync_FetchedStintCompletesAppearanceCountOnly_UpdatesInPlace`
cover the narrowed `(ClubName, StartYear)` key's three outcomes directly;
`REQ1203_BuildNewStintsByPlayerId_IdenticalRefetchInput_ReturnsTrueNoOp`
(added in the follow-up commit after a `quality-architect` finding) proves
an identical re-fetch queues zero writes, not just an unchanged end state.
`WikidataLookupServiceTests.cs` —
`REQ1203_LookupAndPersistAsync_LaterFetchFillsInEndYear_CompletesExistingRowInPlace_NoDuplicate`
and `REQ1203_TwoWriterPathsForSameRealStint_ConvergeOnIdenticalClubName_NoCrossWriterDuplicate`
close the third reconciliation call site `architecture-reviewer` flagged as
an undocumented gap in the first review pass. `PlayerCareerStintRepositoryTests.cs`
covers `UpdateCareerStintCompletionsAsync` directly:
`UpdateCareerStintCompletionsAsync_UpdatesEndYearAndAppearanceCount_InPlace`,
`UpdateCareerStintCompletionsAsync_DoesNotChangeSequenceOrder`,
`UpdateCareerStintCompletionsAsync_StintIdWithNoMatchingRow_IsSilentlySkipped`,
and `UpdateCareerStintCompletionsAsync_EmptyDictionary_DoesNotThrow`.

**REQ-1204 – Guess correctness resolution**
> As a player, I want my guess for an xG Path puzzle checked against that
> puzzle's one specific target player, so I know unambiguously whether
> I've solved it.

- **Status: Implemented (Tier 0, S-082, 2026-07-27).**
  `XGPathGameModule.ScoreSubmissionAsync` (`XGArcade.Games.XGPath`)
  implements this via `Player.NormalizedFullName`/`PlayerAlias
  .NormalizedAlias` — the same exact/alias matching order
  `GridGameModule.FindMatchAsync` uses, minus its fuzzy-matching stage and
  REQ-209-style disambiguation prompt, both deliberately omitted here (no
  category concept to bound a fuzzy search by, and disambiguation is moot
  when only one target player is ever correct) — reviewed and confirmed
  "fine as-is" by `architecture-reviewer` during S-082's quality gate; see
  `docs/architecture-document.md` COMP-11 for the full reasoning. A guess
  that doesn't resolve to a real cell/puzzle throws `PathScoringException`,
  which derives from the shared `XGArcade.Core.Games
  .GameEntityNotFoundException` base (also used by xG Grid's
  `GuessScoringException`) so `GuessEndpoints` — game-agnostic by design —
  needs no compile-time knowledge of either game's own exception type.
- Given a submitted guess for an xG Path puzzle
- When the guess is resolved to a candidate player, using the same
  name-matching/autocomplete pipeline (`PlayerNameIndex`, ADR-0007) xG
  Grid guesses already use — no new matching infrastructure for this game
- Then the guess is correct if and only if the resolved candidate's
  `PlayerId` is this puzzle's target `PlayerId` — there is no
  category-membership check here, unlike xG Grid's correctness check
  (REQ-203), since exactly one player is ever correct for a given puzzle
- And a submitted name that does not resolve to any `PlayerNameIndex`
  candidate is incorrect
- And correctness is determined and shown to the player immediately upon
  submission, not deferred to round close (the same principle as REQ-201)

**Test level:** Unit, API

**REQ-1205 – Per-puzzle attempt cap**
> As a player, I want the number of guesses I'm allowed on an xG Path
> puzzle to match how many clues that specific puzzle actually has, so I'm
> never denied a guess for a clue I've already been shown, and never
> granted guesses beyond the puzzle's own content.

- **Status: Implemented (Tier 0, S-082, 2026-07-27).**
  `XGPathGameModule.GetMaxAttemptsForCellAsync` returns the fixed constant
  7 unconditionally for every puzzle — no repository lookup, no branching
  on `instanceId`/`cellId`, the same "pure extraction" shape
  `GridGameModule.GetMaxAttemptsForCellAsync` already established for its
  own fixed `2` (ADR-0041).
- Given an xG Path puzzle whose total clue count is a fixed **7**
  (REQ-1203) for every puzzle, regardless of its target player's stint
  count `N`
- When a player submits guesses for that puzzle
- Then the maximum number of attempts allowed for that puzzle equals its
  own total clue count (7) — not xG Grid's fixed value of 2
  (`GuessRules.MaxAttemptsPerCell`); see ADR-0041 for the architectural
  change (the attempt cap resolved per-cell through `IGameModule`, rather
  than one shared global constant) this depends on — the value resolved
  through that mechanism is now the same 7 for every xG Path puzzle, but
  the per-cell resolution mechanism is unchanged and still the right shape
  (a different game module could still return a genuinely variable value)
- And the "at most one active guess per cell per round, subject to
  attempt cap and lock rules" shape of REQ-201/202/210 still applies
  conceptually: a correct guess locks the puzzle immediately regardless of
  how many attempts remain, and exhausting the puzzle's own attempt cap
  without a correct guess locks it as unsolved

**Test level:** Unit (the resolved attempt cap is 7 for puzzles with
different stint counts `N`; locks immediately on a correct guess; locks as
unsolved only after the 7-attempt cap is reached, never after a fixed
count of 2)

**REQ-1206 – Clue-efficiency scoring**
> As a player, I want my xG Path score to reflect how few clues I needed
> before guessing correctly, so guessing early with less information is
> rewarded.

- **Status: Implemented (Tier 0, S-083, 2026-07-28).**
  `ClueEfficiencyScoringStrategy` (`XGArcade.Core.Scoring`) implements the
  formula below, registered against `GameKey = XGPathGameModule.XGPathGameKey`
  ("xg-path") in `Program.cs`, mirroring `UniquenessScoringStrategy`'s own
  `"xg-grid"` registration (ADR-0040). `cluesUsed` is not a new field —
  it's read directly off the winning `Guess.AttemptCount`, since
  `XGPathGameModule`/`GuessSubmissionService` already increment
  `AttemptCount` by exactly 1 per submission for a cell, so a correct
  guess's `AttemptCount` at the moment it's submitted already equals the
  number of clues that had been revealed. `maxCluesForThisPuzzle` is
  `maxAttemptsForCell`, resolved once per cell (not once per guess) by
  `ScoreLockingService` via the existing `IGameModule
  .GetMaxAttemptsForCellAsync` (ADR-0041/REQ-1205) and passed into
  whichever `IScoringStrategy` is resolved for the round's `GameKey` — this
  also resolved ADR-0040's own deferred "what parameter shape does a
  strategy receive" follow-up; see the new ADR-0049 for the reasoning
  (`IScoringStrategy.ScoreCorrectGuess` now takes the whole `Guess` plus a
  plain `int maxAttemptsForCell`, never a direct `IGameModule` dependency).
  A puzzle never solved before its attempt cap is exhausted scores
  `MaxPointsPerCell` via `ScoreLockingService`'s existing
  unanswered/incorrect branch (ADR-0021) — `ClueEfficiencyScoringStrategy`
  is only ever invoked for a correct guess, so that case isn't
  special-cased inside the strategy itself. REQ1206-named tests
  (`ClueEfficiencyScoringStrategyTests`, `ScoringStrategyResolverTests`,
  `PathScoreLockingServiceTests`) cover the rounded points formula across a
  range of `cluesUsed`/`maxAttemptsForCell` combinations, `FinalUniquenessScore`
  always being null, `correctGuessesForCell` being ignored, resolver
  selection of this strategy (not `UniquenessScoringStrategy`) for
  `"xg-path"`, and the worst-case/never-solved score end to end through
  `ScoreLockingService.LockRoundScoresAsync`.
- Given a puzzle with a maximum clue count of 7 (REQ-1203/1205, fixed for
  every xG Path puzzle) and a correct guess submitted after `cluesUsed`
  clues have been revealed
- When the round closes and this puzzle's score is locked
- Then the awarded points equal `round(cluesUsed / 7 * MaxPointsPerCell)`
  — golf-style, lower is better, consistent with ADR-0021; the formula
  keeps a `maxCluesForThisPuzzle` term (rather than inlining the literal
  7) so `ClueEfficiencyScoringStrategy` still reads the cap through the
  same `IGameModule` mechanism as REQ-1205, not a hardcoded literal
- And a puzzle never solved before its attempt cap is exhausted (REQ-1205)
  scores the worst case, `MaxPointsPerCell` — the same
  "unanswered/incorrect scores worst" convention ADR-0021 already
  establishes for xG Grid
- And this is not a uniqueness-based score: every player who solves a
  given puzzle names the same target player, so there is no "how unique
  was your correct answer" signal for this game at all — see ADR-0040 for
  how `Core.Scoring` supports this second, different scoring model
  per-game without special-casing xG Path inline

**Status note (2026-08-08 — gap identified via code review, not yet
implemented): score is never shown to the player.** The acceptance
criteria above specify when and how a puzzle's score is *computed and
locked* at round close, but never that it is ever *shown*. Verified
against the current implementation: `GET /path/current`'s response DTOs
(`CurrentPathGuessResponse` in `XGArcade.Api.Path.PathEndpoints`) carry
`IsCorrect`/`AttemptCount`/`Locked`/`SubmittedName`/`ResolvedPlayerName`/
`ResolvedPlayerPhotoUrl` but no points field of any kind, and
`PathScreen.tsx` (SCREEN-10) renders no score anywhere — a solved or
locked-unsolved puzzle shows only "Next puzzle" or the round-complete
message. This is the same live/provisional-estimate gap REQ-204's "S-018
addition" already closed for xG Grid's `LivePoints`, applied here to xG
Path's own scoring strategy (`ClueEfficiencyScoringStrategy`) — exposing
an existing formula, not adding a new scoring rule, so no new ADR is
needed (ADR-0040/ADR-0049 already cover the formula and its inputs). This
status note and the criteria below do **not** touch, duplicate, or change
the xG-Path-scoped leaderboard tab (REQ-410/S-087), which already works
once rounds close and enough qualifying rounds accumulate — the gap here
is specifically the absence of any per-puzzle score on the play screen
itself, live or locked.

**Important asymmetry from REQ-204's `LivePoints` — deliberately not the
same wording.** xG Grid's `LivePoints` is genuinely provisional: it
depends on `UniquenessCalculator`'s denominator (how many *other* players
have also correctly guessed the cell so far), which can keep growing
until the round closes, so the same cell's live estimate really can
change between two page loads. `ClueEfficiencyScoringStrategy`'s formula
has no such dependency — both `cluesUsed` (`Guess.AttemptCount` at the
moment the puzzle locked) and `maxCluesForThisPuzzle` (the fixed 7,
REQ-1205) are fully determined the instant a puzzle locks, and never
change afterward. A value shown for a locked xG Path puzzle before round
close is therefore not an estimate that can still change — it is
arithmetically identical to what `ScoreLockingService` will persist as
`FinalPoints` once the round closes, just not yet written to that column.
The criteria below deliberately avoid REQ-204's "~N pts estimated"/
"provisional" framing for this reason: applying that wording here would
be inaccurate, and a criterion asserting "this value can change before
close" would be untestable in the sense that it would always fail — it
can't.

- Given a locked xG Path puzzle (solved correctly, or its 7-attempt cap
  exhausted unsolved — REQ-1205)
- When the player views that puzzle via `GET /path/current`, at any point
  before or after the round closes
- Then the response includes the point value `ClueEfficiencyScoringStrategy`
  computes for that puzzle (this REQ's formula above) — the same value
  `ScoreLockingService` will persist as `FinalPoints` once the round
  closes, computed and returned live rather than withheld until then
- And no point value is returned for a puzzle that is not yet locked
  (still guessable) — the formula has no meaning until the puzzle's
  outcome (solved, and with how many clues; or exhausted unsolved) is
  fixed
- And the value shown before round close and the value shown after round
  close (once `FinalPoints` exists) are always numerically identical for
  a given puzzle — unlike REQ-204's `LivePoints`, this is never an
  estimate that can change, and the frontend must not use wording implying
  otherwise ("~", "estimated", "provisional") for it
- And this governs only the xG Path play screen's (SCREEN-10) per-puzzle
  display — it does not add, change, or duplicate any leaderboard
  behavior; REQ-410's existing xG-Path-scoped leaderboard tab is
  unaffected and remains the only place aggregate/total xG Path standings
  are shown

**Status note (2026-08-08, backend piece implemented — same-day follow-up to
the gap above):** `GET /path/current`'s `CurrentPathGuessResponse` now
carries a `Points` field (`int?`, `XGArcade.Api.Path.PathEndpoints`),
non-null only when `Locked` is true. It is computed by resolving
`IScoringStrategyResolver` (already DI-registered) and calling
`ClueEfficiencyScoringStrategy.ScoreCorrectGuess` directly for a correct
guess (`correctGuessesForCell` passed empty, since that strategy ignores
it) — never a reimplementation of its rounding formula — and, for a puzzle
locked via exhausted attempts (never solved), the same
`ScoringRules.MaxPointsPerCell` worst case `ScoreLockingService`'s own
`!guess.IsCorrect` branch assigns, since `ClueEfficiencyScoringStrategy`
itself is only ever invoked for a correct guess. Named `Points`, not
`LivePoints`/`EstimatedPoints`, and documented on the DTO as never
provisional, per this REQ's own "Important asymmetry from REQ-204's
`LivePoints`" note above.

**Status note (2026-08-08, frontend piece implemented — closes the gap
above):** `PathTimeline.tsx`'s `SolvedNode`/`FailedRevealNode` (wired from
`PathScreen.tsx`, alongside the resolved player name/photo they already
render once a puzzle is `locked`) now render this value as plain "N pts"
text — `mono-figure`, matching every other numeric score/count in this
app — never "~"/"estimated"/"provisional" wording, and never shown for a
still-unlocked puzzle. `lib/types.ts`'s `CurrentPathGuess.points` mirrors
`CurrentPathGuessResponse.Points` exactly. No new SCREEN-10 element beyond
what this REQ's own acceptance criteria already called for (the timeline's
solved/failed reveal nodes already existed for the resolved player name/
photo; this only adds a line to each) — `docs/design-document.md`'s
SCREEN-10 section is updated with a matching status note.

**Test level:** Unit (points formula across a range of `cluesUsed`/
`maxCluesForThisPuzzle` combinations; worst-case score when the puzzle is
never solved; no uniqueness score of any kind is computed by this game's
scoring strategy) — covered by `ClueEfficiencyScoringStrategyTests`/
`ScoringStrategyResolverTests`/`PathScoreLockingServiceTests`. API (`GET
/path/current` includes the points value for a locked puzzle — solved via
`ClueEfficiencyScoringStrategy`'s own formula, or exhausted-unsolved via
the worst-case value — and omits it for a still-guessable, unlocked
puzzle) — covered by `PathEndpointTests`
(`REQ1206_PathCurrent_Get_LockedViaCorrectGuess_ReturnsPointsMatchingClueEfficiencyFormula`,
`REQ1206_PathCurrent_Get_LockedViaExhaustedAttempts_ReturnsWorstCasePoints`,
`REQ1206_PathCurrent_Get_UnlockedPuzzleWithAnExistingGuess_ReturnsNoPoints`).
**UI (2026-08-08, now covered):** SCREEN-10 (`PathTimeline.tsx`'s
`SolvedNode`/`FailedRevealNode`, wired from `PathScreen.tsx`) renders the
locked point value with plain "N pts" wording — never "~"/"estimated"/
"provisional" — for both the solved and the exhausted-unsolved case, and
renders nothing for a still-unlocked puzzle. Covered by
`PathTimeline.test.tsx`'s `describe('REQ-1206: locked point value', ...)`
block (solved reveal shows the value with no provisional wording;
locked-but-unsolved reveal shows the value too; a still-unlocked puzzle
shows no points even if one were somehow passed; a null `points` on an
otherwise-locked reveal renders no points line rather than "null pts") and
`PathScreen.test.tsx`'s three `REQ-1206:` tests (end-to-end plumbing from
`GET /path/current`'s `points` field through to the rendered text, for the
solved, exhausted-unsolved, and still-unlocked cases).

**REQ-1207 – Player position and birth year sourced from Wikidata**
> As a player, I want the position, nationality, and age clues at the end
> of an xG Path puzzle's reveal sequence (REQ-1203) to be backed by real
> Wikidata data about the target player, not fields with no way to ever be
> populated, so those clues are as trustworthy as the club and appearance-
> count clues already are.

- **Status: Implemented (S-082).** `Player.Position`/`Player.BirthYear`
  (nullable, migration `20260727140000_AddPlayerPositionAndBirthYear`) are
  populated by `WikidataClient.BuildIntersectionQuery`'s new OPTIONAL P413
  binding and the existing P569 binding, threaded through
  `WikidataPlayerMatch`/`PlayerCreationRequest` into
  `PlayerStoreRepository.GetOrCreatePlayersByWikidataQidAsync`, which
  already only ever sets fields at row creation — the set-once contract
  below falls out of that method's existing shape, no new logic needed.
  REQ1207-named tests cover the OPTIONAL binding's presence across all five
  intersection query builders, `ParseBindings`' Position/BirthYear
  extraction, and the set-once persistence contract (including the
  "existing row's null is never backfilled" and "existing row's value is
  never overwritten by a disagreeing later sync" cases) at both the
  `WikidataLookupService` and `PlayerStoreRepository` layers.
- **Scope note:** this REQ covers `Player.Position` and `Player.BirthYear`
  only — two new nullable scalar columns on `Player` (COMP-06, the same
  table `PhotoUrl`/`WikidataQid`/`FullName` already live on), not new
  `PlayerAttribute` rows, since neither value has club-style multiplicity
  (a player has at most one position and one birth year, unlike club/
  nationality/trophy membership, which is inherently one-row-per-value).
  It is not a new external data source — still Wikidata, the already-
  approved provider (ADR-0008's terms-of-service review does not need
  repeating for a new property on an already-approved source). Separately:
  REQ-1203's nationality clue depends on a `PlayerAttribute` "nationality"
  row, which this REQ does not add — that row only exists for a player who
  entered the system via a query shape that queries a country side
  (Country×Club, National-team×Club, Trophy×Country); a player who only
  ever entered via Club×Club sync has no such row today. This is a
  pre-existing gap this REQ did not create and does not fix — flagged here
  for visibility, since REQ-1203's nationality clue depends on it, but
  resolving it is out of this REQ's scope.
- **Status note (2026-08-02, bug-bundle fix): the dedicated backfill this
  REQ anticipated now exists.** The "unless a future dedicated backfill ...
  is built and run" caveat below is resolved: real xG Path user testing
  showed "Position: not available"/"Age: not available" on essentially
  every puzzle, because the overwhelming majority of `Player` rows predate
  this REQ's migration and this REQ's own set-once contract never
  backfills them. `PlayerPositionBirthYearBackfillService`
  (`XGArcade.DataSync.Wikidata`) — the exact mirror this REQ's own text
  named in advance — backfills them via a new `dotnet run --
  backfill-player-position-birthyear` CLI verb. Its
  `.github/workflows/backfill-player-position-birthyear.yml`
  `workflow_dispatch`-only wrapper was deleted in S-132 (2026-08-17) as a
  one-off incident tool with no runs since 2026-08-10 — the verb itself is
  unchanged and still runnable via `dotnet run --
  backfill-player-position-birthyear` locally, or a throwaway manual
  `workflow_dispatch` re-add if ever needed. This REQ's set-once-at-creation contract above
  is unchanged going forward — the backfill only ever writes a `Player`
  row's currently-null field(s), never overwrites an already-set value.
- **Status note (2026-08-02, bug-bundle fix): Position was persisted as a raw
  Wikidata QID URI, not a label.** Every query that fetches P413 (the five
  intersection query builders AND the backfill's own
  `QueryPlayerPositionsAndBirthYearsByQidsAsync`) projected `?position`
  straight into `Player.Position` — the bare entity URI object of the P413
  triple (e.g. `"http://www.wikidata.org/entity/Q336286"`), never resolved
  to a human-readable string. Real xG Path play surfaced this directly: the
  position clue rendered the literal QID URI. Fixed by requesting
  `?positionLabel` (auto-resolved by the existing `SERVICE wikibase:label`
  block already used for `?playerLabel`/`?clubLabel`) instead of `?position`
  — the backfill query additionally needed the `SERVICE wikibase:label`
  block added at all, since it had none. This REQ's set-once persistence
  contract and null-handling are otherwise unchanged; only what gets
  captured as the non-null value changed, from a QID to a label.
- **Status note (2026-08-10, bug fix): rows written before the 2026-08-02
  fix above stayed broken forever — the backfill's candidate query is now
  widened to catch them.** The 2026-08-02 fix directly above stopped any
  NEW `Player` row from getting a raw QID URI in `Position`, but it did
  nothing for rows already written with the bad value before that fix
  shipped — `PlayerStoreRepository.GetPlayersMissingPositionOrBirthYearAsync`
  only ever selected rows where `Position IS NULL`, and a raw-URI `Position`
  is NOT NULL, so those pre-2026-08-02 rows were silently and permanently
  invisible to `PlayerPositionBirthYearBackfillService` — every future
  backfill run re-selected only genuinely-empty rows and never touched the
  already-bad ones. This is exactly what a bug report showed: a raw
  `http://www.wikidata.org/entity/Q...` URI still rendering as the position
  clue on rows that predate 2026-08-02. Fixed by widening the candidate
  query to also select a `Position` that starts with the raw Wikidata
  entity URI prefix (`http://www.wikidata.org/entity/`), and by making
  `UpdatePlayerPositionsAndBirthYearsAsync` overwrite a raw-URI `Position`
  — the one deliberate exception to this REQ's "set once, never
  overwritten" contract above, since a raw-URI value was never a genuine
  value in the first place, just the pre-2026-08-02 write-path bug frozen
  in place. No equivalent bad-sentinel state exists for `BirthYear` (it's
  parsed from an `xsd:dateTime` binding straight into an `int`, never
  carried through as a raw URI or other placeholder), so `BirthYear`'s half
  of the candidate query and the set-once contract are unchanged.
- Given the existing Wikidata intersection queries that create or enrich
  `Player` rows during xG Grid/xG Path player sync (Country×Club,
  National-team×Club, Club×Club, Trophy×Country, Trophy×Club — every query
  built on `WikidataClient`'s shared query-building predicates, including
  REQ-211's guess-time live lookup, which routes through the same query
  builders)
- When a player match is fetched from one of those queries
- Then the query additionally requests Wikidata's P413 ("position played
  on team / speciality") as an OPTIONAL binding alongside the existing
  SELECT — no new query, no new round-trip, no new HTTP request — mirroring
  how `Player.PhotoUrl`/P18 already rides along the same existing SELECT
  (see `Player.PhotoUrl`'s own doc comment) and how `PlayerCareerStint`'s
  P580/P582/P1350 qualifiers ride along the existing P54 statement fetch
  (ADR-0042)
- And `Player.BirthYear` is derived from the P569 ("date of birth")
  binding those same queries already require for every matched player
  (ADR-0025's male/born-1939-or-later pool filter) — extracting just the
  year, with no new binding added to the query for this field at all
- And both values are persisted onto `Player.Position`/`Player.BirthYear`
  only at the moment a `Player` row is first created — never written or
  overwritten on a `Player` row that already exists, regardless of whether
  that row's current value is null or already set, mirroring `PhotoUrl`'s
  existing "set once, at creation, never re-synced" rule
  (`PlayerStoreRepository.GetOrCreatePlayersByWikidataQidAsync`)
- And a player with no P413 statement on Wikidata has a permanently null
  `Position` unless a future dedicated backfill (mirroring
  `PlayerPhotoBackfillService`, REQ-214's addendum) is built and run — this
  REQ does not itself define or require that backfill, the same way
  REQ-214's original scope didn't either
- And null is a valid, expected value for both columns, never an error
  condition — REQ-1203's position/nationality/age clues are expected to
  treat a null `Position`/`BirthYear` the same way REQ-1203 already treats
  an unknown club appearance count: the clue is still revealed, rendered as
  "not available," never delayed, skipped, or silently dropped from the
  fixed 7-clue sequence, so a data gap here never changes a puzzle's total
  clue count away from the fixed 7 that REQ-1205's attempt cap and
  REQ-1206's scoring formula depend on

**Test level:** Unit (`WikidataClient` query construction — the OPTIONAL
P413 binding is present in the generated SPARQL for every one of the five
intersection query builders, with no additional query or HTTP call added;
`WikidataLookupService`/`PlayerStoreRepository` persistence — Position/
BirthYear are set from the query response when a `Player` row is first
created, and left completely untouched on a `Player` row that already
exists on a later sync, whether or not its current value is null; both
columns are correctly null when their source Wikidata data is absent)

**REQ-1208 – xG Path target selection does not repeat until the eligible
pool has cycled**
> As a player, I want xG Path targets not to repeat noticeably across
> rounds, so I don't keep seeing the same familiar players over and over
> before the pool of eligible, recognizable players has actually been used
> up once.

**Status: Implemented (backend, 2026-08-03, S-093); tests written
2026-08-03.** Unit coverage (`XGPathGameModuleTests.cs`, new
`ManualTimeProvider.cs`): usage recorded per selection, exclusion within a
cycle, rollover once remaining-unused drops below N (including
reselecting a just-used player), a stale usage row from a player who
drops out of the live pool never blocking rollover, and the pre-existing
REQ-1202 insufficient-pool abort left untouched by cycle state. API
coverage (`RoundEndpointTests.cs`): round generation across a rollover
boundary. `dotnet` was unavailable in the implementation sandbox — these
tests are written and hand-traced against the actual implementation but
not compiled or run; still need a real `dotnet test` pass in CI before
merge. Two new xG Path-scoped entities (`XGArcade.Data`, migration
`20260803140000_AddPathTargetCycle`): `PathTargetCycle` (a singleton row —
`CycleNumber`, `ObservedPoolSize`, `UsedInCycleCount`,
`LastCycleCompletedAt`) and `PathCycleTargetUsage` (one row per
player-used-in-a-cycle-number selection), exactly per ADR-0058's
persistence decision — never a field on `Player`. New
`IPathInstanceRepository` methods: `GetCycleStateAsync` (pure read, null
until the first generation ever runs), `GetOrCreateCycleStateAsync`
(idempotent singleton lookup, mirrors
`ILeagueRepository.GetOrCreateGlobalLeagueAsync`),
`GetUsedPlayerIdsInCycleAsync`, and `AddInstanceWithCycleUsageAsync` (the
`PathInstance`/`PathPuzzle` write and the cycle-state/usage write in one
`SaveChangesAsync` call, per this REQ's "at the same time" wording).
`XGPathGameModule.GenerateInstanceAsync` now excludes players already
recorded as used in the current cycle from `PickDistinct`'s candidate set,
rolls the cycle over (new `CycleNumber`, `LastCycleCompletedAt` stamped,
every eligible player selectable again) when the remaining-unused count
drops below the template's `PuzzleCount`, and records the newly-selected
targets as used in the (possibly just-rolled-over) cycle — all before
REQ-1202's existing "no two puzzles in one instance share a target" and
"insufficient total eligible pool" checks, both untouched. A player who
drops out of the live eligible pool between generations is handled with no
special-case code: their stale usage row is simply never read again, since
lookups are always scoped to the current cycle number and filtered against
the live eligible set. See REQ-1209 immediately below for the new
admin-read endpoint this persisted state now supports.

**Design note — which pool a cycle is scored against (explicit decision,
not a default):** a cycle is scored against the same pool
`GetEligiblePlayerIdsAsync` already computes and `PickDistinct` already
samples from at generation time — REQ-1201's three structural checks
**narrowed by ADR-0056's familiarity filter** — not the larger,
structurally-eligible-only pool. This is deliberate, not an oversight:
targets are only ever actually selected from the familiarity-filtered
pool, so scoring a cycle against the larger structural pool would include
players who can structurally never be picked at all (anyone permanently
below ADR-0056's sitelink threshold) — a cycle scored that way could
never complete, since it would always be waiting on players selection can
never reach. ADR-0056 itself documents that this pool is live and
somewhat unstable (re-queried every generation, can shrink or grow,
fails open on a Wikidata outage) — this REQ's cycle-completion rule
below (a cycle completes once the *remaining unused* portion of the
current live pool drops below what a generation needs, not once it hits
exactly zero) is deliberately tolerant of that instability: it does not
require the pool to ever stabilize or hit an exact empty state, only
that it run low relative to how many targets one generation needs.

**Persistence boundary (explicit decision, not a default):** "already
used this cycle," the cycle counter, and the pool/usage figures REQ-1209
displays are xG Path-specific state, not shared game data. This must be
persisted as xG Path's own data (`XGArcade.Data`, ADR-0014's existing
"every game module's entities live in the shared `DbContext`, scoped to
that module" precedent — the same pattern `PathInstance`/`PathPuzzle`/
`PathTemplate` already follow) — **never** a new field on the shared
`Player` entity (COMP-06), which xG Grid also reads. Adding xG Path's own
cycling concern to a row xG Grid depends on would be the same kind of
cross-game leakage ADR-0042 already rejected for a different reason
(`PlayerCareerStint` kept separate from `PlayerAttribute` rather than
widening a shared table for one consumer's needs) — see that ADR's own
"For AI agents" note.

**Status note (2026-08-18, S-141, Epic 12 follow-up): new
`reset-path-target-cycle` operational CLI verb wipes stale target-cycle
bookkeeping after S-137–S-140 narrowed the eligible pool this REQ cycles
against.** S-137 (birth-year floor), S-138 (two-seeded-club requirement),
S-139 (B-team exclusion), and S-140 (regional/national regex fix) —
together, REQ-1201's own 2026-08-17/08-18 status notes — substantially
narrow the same live pool this REQ's target-cycle tracking is scored
against (see "Design note" above). `PathTargetCycle.ObservedPoolSize`
self-corrects for free on the next generation, but `UsedInCycleCount` and
the `PathCycleTargetUsage` rows it derives from do not — they were
accumulated by counting distinct targets against the OLD, larger
pre-S-137–S-140 pool, and left in place would understate how much of the
NEW, narrower pool remains available, risking this REQ's own rollover
condition below firing later than it should (repeats becoming visible to
players before a rollover the stale count should have triggered). Added
`PathTargetCycleResetter` (`XGArcade.Data.Seeding`) and its
`reset-path-target-cycle` CLI verb — same "narrow, pair-scoped/table-scoped
tool, not a re-run of the full purge/reseed pipeline" shape as REQ-110's
`PairLookupFailureCleaner`/`clear-pair-lookup-failures` and REQ-1203's
`DuplicateCareerStintCleaner`/`clean-duplicate-career-stints`: it wipes the
`PathTargetCycle` singleton row and every `PathCycleTargetUsage` row (not
just the current cycle's — see the class's own doc comment for why a
leftover row from a previous "cycle 1" would otherwise collide with the
fresh `CycleNumber` 1 this reset restarts at), so the next generation
starts a clean baseline scored purely against the new pool. Idempotent and
a no-op, not an error, when xG Path has never generated a round yet (no
`PathTargetCycle` row). This does not change this REQ's rollover logic,
selection logic, or persisted schema in any way — it is a one-time
operational correction for state that predates S-137–S-140, run manually
via `dotnet run -- reset-path-target-cycle`, not on any schedule. **S-141's
other half — an actual before/after eligible-pool count against real (dev)
data — could not be produced in this pass; no live Wikidata or real dev
Postgres access was available. See `NOTES.md`'s 2026-08-18 entry for the
full reasoning and the handoff steps for whoever next has real dev
access.** REQ-1201 itself already flagged S-141 as its own planned
follow-up (see that REQ's 2026-08-17 S-137/S-138 status notes) — this note
records what S-141 actually delivered against REQ-1208 specifically.

- Given the live xG Path target-selection pool for a generation (REQ-1201's
  structural checks narrowed by ADR-0056's familiarity filter — the exact
  pool `GetEligiblePlayerIdsAsync` already returns today)
- And a record of which players in that pool have already been selected as
  a target since the current cycle began
- When a new xG Path round instance is generated and needs N distinct
  targets (REQ-1202)
- Then targets are selected only from among eligible players not yet used
  in the current cycle
- And each selected target is recorded as used in the current cycle at the
  same time it is persisted as a puzzle's target, so no later generation in
  the same cycle can reselect them
- Given the eligible players not yet used in the current cycle number fewer
  than N (the count this generation needs)
- When round generation runs
- Then the current cycle is treated as complete: a new cycle begins (every
  eligible player, including one used moments ago in the just-completed
  cycle, becomes selectable again), the completion moment is recorded, and
  this generation's N targets are then selected from the newly-available
  full pool
- And REQ-1202's existing "no two puzzles in the same round instance target
  the same player" guarantee is unaffected — a cycle rollover changes which
  players are eligible for selection, never the distinctness guarantee
  within one instance
- And a player who drops out of the live eligible pool between generations
  (e.g., no longer meets ADR-0056's familiarity threshold, or a fail-open
  event ends) is simply no longer considered — their earlier "used this
  cycle" record is inert, never blocks anyone else's eligibility, and never
  causes a generation failure
- And this REQ does not change `GenerateInstanceAsync`'s existing
  insufficient-total-pool abort (REQ-1202: fewer than N eligible players
  overall) — that check is about total pool size and is independent of
  cycle state

**Test level:** Unit (a selected target is recorded as used in the current
cycle; a player already used in the current cycle is excluded from
selection on a later generation within the same cycle; a cycle rolls over
when the unused-in-cycle count drops below N, making every eligible player
selectable again; a player who leaves the live eligible pool between
generations never blocks rollover detection or causes an error), API/
Integration (round generation still produces exactly N distinct-target
puzzles across a cycle-rollover boundary; the pre-existing insufficient-
total-pool `PathGenerationException` still fires and is unaffected by
cycle state).

**REQ-1209 – Admin visibility into xG Path target cycling**
> As an admin, I want to see xG Path's current target-selection cycle
> status on the admin screen, so I can notice when the eligible pool is
> running low and consider widening the seeded club/country pool or
> revisiting ADR-0056's familiarity threshold.

**Status: Backend and frontend implemented (2026-08-03, S-093); tests
written 2026-08-03.** New `GET /admin/xg-path/cycle` (`XGArcade.Api.Admin.
AdminXGPathEndpoints`), gated on the same `"Admin"` policy every other
admin endpoint uses (403 for a non-admin token, mirroring
`AdminAccountsEndpoints`'s existing endpoints), registered
unconditionally (including Production — this is real operational state,
not seeded/test data). Calls only
`IPathInstanceRepository.GetCycleStateAsync` — a pure read of REQ-1208's
persisted `PathTargetCycle` row, never `IPlayerFamiliarityService` and
never anything that could trigger round generation, satisfying this REQ's
"never itself triggers a new eligible-pool computation or a live Wikidata
familiarity check" requirement by construction (the endpoint has no route
into `XGPathGameModule.GenerateInstanceAsync` at all). Response shape
(`AdminXGPathCycleResponse`): `HasData` (false with every other field null
when no xG Path round has ever generated — REQ-1209's "no data yet" case,
returned as a normal 200, never a 404/error), `CycleNumber`,
`ObservedPoolSize`, `UsedInCycleCount`, `RemainingInCycleCount` (derived
as `ObservedPoolSize - UsedInCycleCount`, not a persisted column, to avoid
a value that could drift out of sync with the two it's computed from), and
`LastCycleCompletedAt`. **Frontend implemented 2026-08-03** by
`ui-implementer`: a new `XGPathCycleSection` in `frontend/src/admin/
AdminScreen.tsx`, rendered unconditionally alongside `AccountMetricsSection`
(same "own fetch, own `useEffect`, 401-escalates via `onAuthError`,
403-hides via a local `hidden` flag, other-error-shows-message-inline"
pattern that section already establishes) and a new `fetchAdminXGPathCycle`
helper in `frontend/src/lib/api.ts` (typed against the new
`AdminXGPathCycleState` in `frontend/src/lib/types.ts`). Displays the
current cycle number, the eligible pool size as of the most recent
generation, used/remaining counts, and the last-cycle-completion timestamp
(or "No cycle has completed yet") using the existing `admin-screen__metrics`
display pattern — no new CSS/tokens introduced. The `HasData: false` case
renders a plain "No xG Path round has generated yet — no cycle data to
show." message via the existing `admin-screen__empty` class, never an
error and never a blank section. Test coverage: API (`AdminXGPathEndpointTests.cs`,
new) — persisted-state, no-data-yet, 403, and 401 cases, plus the
endpoint's unconditional Production registration; frontend
(`AdminScreen.test.tsx`) — full-field render, no-data-yet empty state, and
the 401/403/other-error handling pattern for `XGPathCycleSection`.
Frontend: 459/459 Vitest tests pass, verified in this sandbox. Backend:
`dotnet` was unavailable in this sandbox — these tests are written and
hand-traced against the actual implementation but not compiled or run;
still need a real `dotnet test` pass in CI before merge. `docs/backlog.md`
S-093's own entry tracks this.

- Given REQ-1208's persisted cycle state (the current cycle number, the
  eligible pool size as most recently observed at generation time, how many
  of that pool have been used so far in the current cycle, and when the
  most recently completed cycle finished, if any)
- When an admin opens the existing admin screen (`AdminScreen.tsx`,
  REQ-503/509/510's surface — no new screen)
- Then a new, self-contained section (same pattern as
  `UnverifiedDataSection`/`RoundControlSection`/`AccountMetricsSection` —
  its own fetch, gated on backend availability) displays: the current cycle
  number, the eligible pool size as of the most recent xG Path round
  generation, how many targets have been used so far in the current cycle
  and how many remain, and the completion time of the most recently
  completed cycle
- And this section's fetch/render never blocks, and is never blocked by,
  any other admin section's state
- And opening this section reads only already-persisted cycle state — it
  never itself triggers a new eligible-pool computation or a live Wikidata
  familiarity check; ADR-0056's per-generation query stays scoped to round
  generation only
- Given no xG Path round has ever been generated yet (no cycle state exists)
- When an admin opens the admin screen
- Then this section shows a clear "no data yet" state, never an error and
  never a blank section
- Given a non-admin token
- When the underlying endpoint for this section is called
- Then it responds 403, the same policy-gating every other admin endpoint
  in `AdminScreen.tsx` already enforces

**Test level:** API (a new admin-authenticated read endpoint returns the
persisted cycle state; 403s a non-admin token, mirroring every existing
admin endpoint's own test coverage), UI (the section renders each field
from a successful fetch, renders the pre-first-generation empty state,
and follows the same 401-escalates/403-hides/other-error-shows-message
pattern `AccountMetricsSection` already establishes).

---

### 4.13 Cross-game player experience

Requirements in this section apply uniformly to every game xG Arcade
hosts (currently xG Grid and xG Path, and any game added later) — they
are written in terms of the shared `Round`/cell model (ADR-0003), never
in terms of one game's own internals, so a new game does not need its own
copy of the requirement.

**REQ-1210 – Round-completion animation with current points and a
leaderboard link**
> As a player, I want a completion animation when I finish a round of any
> game, showing my current points for that round and a link straight to
> that round's leaderboard for that specific game, so I get immediate
> feedback and can immediately see how I compare to others on this round.

- **Context — no generic completion signal exists today.** Neither game's
  current-round response DTO (`CurrentRoundResponse`, `CurrentPathResponse`
  in `frontend/src/lib/types.ts`) carries an `isComplete`/equivalent field.
  `GridScreen.tsx` never branches on completion at all (it always shows
  "X/Y answered"); `PathScreen.tsx` has only an inline, ad hoc `locked &&
  isLastPuzzle` check with no hoisted signal, generic signal, or shared
  component. This requirement specifies the observable trigger, the
  points value, and the link's destination only — it deliberately does
  not mandate whether "round complete" is computed backend-side (e.g. a
  new response field) or frontend-side (e.g. derived from the existing
  per-cell data both games' current-round responses already return), or
  what mechanism carries a round-scoped, game-scoped leaderboard link
  given the frontend's hash-based, flat-lookup-table routing (ADR-0039)
  has no per-round/per-game parameterized route today — see the "Needs an
  ADR" note below.
- **Context — reuses existing scoring, introduces no new formula.** Today
  neither game exposes a single "my current total for this round" value
  from the backend; `GridScreen.tsx`'s `totalKnownPoints` sums each
  cell's live/locked value client-side ad hoc, and xG Path's `GET
  /path/current` only returns each puzzle's own locked `Points` (REQ-1206)
  with no round-level sum anywhere. This requirement does not introduce a
  second scoring path — the value it requires is the sum of exactly the
  values each game's own existing live/locked per-cell scoring already
  treats as authoritative (REQ-204/205/206 for xG Grid; REQ-1206 for xG
  Path), computed however the game already computes them.
- Given a round instance's fixed set of cells for a specific player (xG
  Grid's grid cells, REQ-101/102; xG Path's puzzles, REQ-1202 — both
  represented as cells in the shared `Round`/`IGameModule` model, ADR-0003)
- When that player's own guessing activity resolves the last cell
  available to them — i.e., every cell in the round now has a locked
  outcome for that player, each either correctly guessed (REQ-210/1204)
  or incorrect with attempts exhausted (REQ-210/1205)
- Then a completion animation is shown to that player, distinguishing this
  moment from ordinary in-progress play
- Given that trigger, when the completion animation is displayed
- Then it shows a current-points value for that round that is numerically
  identical, at that instant, to whatever value that same game already
  treats as this player's authoritative current total for that round — for
  xG Grid, the sum of each cell's live/locked value exactly as already
  computed for in-progress play (REQ-204/206); for xG Path, the sum of
  each locked puzzle's own points value (REQ-1206) — never a second,
  independently-computed total
- And the value's presentation follows whichever wording convention that
  game's own live-scoring requirement already established — xG Grid's
  "~N pts estimated"/provisional framing (REQ-204/213), since another
  player's still-open guess on a shared cell can still change this
  player's own completed total until the round actually closes (REQ-205);
  xG Path's plain, non-provisional "N pts" wording (REQ-1206), since a
  locked xG Path puzzle's points never change afterward — this requirement
  does not introduce a third wording convention
- Given the round this player just completed has not yet closed (REQ-302)
  at the moment the animation is shown
- When a player activates the leaderboard link inside the animation
- Then they are taken directly to that specific round's live leaderboard
  for that specific game (REQ-407), already scoped to it — not the
  generic all-time leaderboard landing view, and without the player
  needing to separately select the round or the game themselves
- Given the round this player just completed has already closed (REQ-205)
  by the time the animation is shown or the link is activated
- When a player activates the leaderboard link
- Then they are taken instead to that specific round's closed, final
  leaderboard for that specific game (REQ-408) — the link never 404s,
  errors, or silently falls back to the generic all-time leaderboard
  merely because the round closed in the interim
- Given a player has `prefers-reduced-motion` enabled
- When the completion trigger above occurs
- Then the current-points value and the leaderboard link are both still
  shown immediately, without either being gated behind an animation
  actually playing — matching this document's established pattern for
  every other animation (`docs/design-document.md` §2's badge-dock and
  rejected-guess cues, REQ-212/S-020), which each keep their functional
  content/cue while only removing the motion itself

**Test level:** Unit (whichever component computes the completion trigger
returns true only once every cell for that player is locked, for both an
xG Grid fixture and an xG Path fixture, and false for a partially-answered
round), Unit/API (the current-points value surfaced at completion matches
exactly what that game's own existing live/locked scoring path already
returns for the same round/player, with no divergent calculation), UI
(component: the animation renders the current-points value with that
game's own established wording convention and a leaderboard link; the
link's destination resolves to REQ-407's live view while the round is
still active and to REQ-408's closed view once it has closed;
`prefers-reduced-motion` still renders both the points value and the link
without requiring the animation to play), E2E (Playwright: answering a
round's last cell in xG Grid, and separately in xG Path, each show the
completion animation with the correct current points; activating the
leaderboard link lands on that exact round+game's leaderboard, pre-scoped,
not the all-time view; the closed-round case is exercised via REQ-806's
force-close test-data endpoint).

**Needs an ADR:** two structural, "could reasonably have gone another
way" decisions are deliberately left open here, not decided in this
requirement:
1. **How "round complete" is signaled generically across games.** Options
   include a new boolean/field on each game's current-round response DTO,
   a shared derivation computed frontend-side from data both DTOs already
   return, or some other shared mechanism — any of these must resolve
   through each game's existing `IGameModule` contract or its response
   shape, never a game-specific special case hard-coded into a
   cross-game component, per ADR-0003's boundary.
2. **How a round-scoped, game-scoped leaderboard link is reached without a
   router.** ADR-0039 deliberately chose a flat, hand-rolled hash lookup
   table for exactly six fixed screens and explicitly named "a per-round or
   per-league detail URL" as the trigger for revisiting that decision (see
   ADR-0039's own "Follow-up" note) — this requirement's leaderboard link
   is precisely that trigger. Whether this is solved by extending the
   existing lookup table with parameters, introducing a real router
   (superseding ADR-0039), or an in-memory navigation mechanism that
   doesn't touch the URL at all is not decided here.

Flagged for `architecture-reviewer`/the implementer to resolve, via a new
ADR (or an amendment/supersession of ADR-0039 for point 2), before or
alongside implementation — a requirements document specifies WHAT and HOW
TO VERIFY, not HOW TO BUILD.

---

### 4.14 xG Predict generation and gameplay

**xG Predict** is the third game hosted on the xG Arcade (see `CLAUDE.md`
and `architecture-document.md` for the platform/game boundary this section
must not cross), alongside xG Grid (COMP-05) and xG Path (COMP-11) — a
match-outcome prediction game living behind the same `IGameModule`
interface as its own new component, **COMP-15 (Games.XGPredict)**. A round
targets five real Premier League matches; the player predicts each match's
final score before the round locks, and each prediction is graded once its
match actually finishes. This section is design-only — no xG Predict code
exists yet. Every REQ below is written to the same standard as §4.1/§4.12's
requirements for xG Grid/xG Path, but describes intended behavior for a
game that has not been built, not a claim about current behavior.

**Note on §4.13's cross-game requirements:** REQ-1210 (round-completion
animation with a leaderboard link) is written for a game whose cells
resolve synchronously, the instant the player's own guessing activity
locks the last one (true for xG Grid and xG Path — see REQ-1210's own
"Given a round instance's fixed set of cells..." criterion). xG Predict's
matches instead resolve asynchronously, sometime after the round has
already locked (REQ-1305) — a player can submit all five predictions and
then have nothing left to resolve in-app until matches are graded,
possibly days later, while not using the product at all. REQ-1210's
trigger condition, as written, does not straightforwardly extend to this
shape. This is flagged as a genuine open question in §7, not resolved here
and not addressed by silently reinterpreting REQ-1210's existing
acceptance criteria — REQ-1210 itself is unchanged by this note.

**REQ-1301 – Round structure: five matches from one gameweek, tightest
kickoff clustering**
> As a player, I want each xG Predict round to contain five matches drawn
> from a single upcoming Premier League gameweek, clustered as tightly as
> possible in kickoff time, so a round feels like one coherent slate
> rather than an arbitrary spread of fixtures across a whole weekend.

- Given an upcoming Premier League gameweek's full fixture list, fetched
  from API-Football's fixtures endpoint (see ADR-0094 — this is the first
  use of live match schedule/result data anywhere in this codebase,
  distinct from every other game's Wikidata career/bio data)
- When a new xG Predict round is generated
- Then exactly 5 of that gameweek's matches are selected as the round's
  matches, each represented as one cell in the existing generic
  `IGameModule`/`Round` model (ADR-0003) — `Round` references the xG
  Predict instance via the existing opaque `GameKey` (`"xg-predict"`)/
  `GameInstanceId` pair, unchanged from how xG Grid/xG Path already do this
- And, among every possible 5-match subset of that gameweek's fixtures, the
  subset selected is the one that minimizes the span between the earliest
  and latest kickoff time among its 5 matches — i.e. the tightest
  kickoff-time clustering available that gameweek (e.g. the Saturday-3pm
  block, when a gameweek has one), never an arbitrary spread chosen for any
  other reason
- And selection is deterministic: given the same fixture list, round
  generation always selects the same 5 matches
- Given an upcoming gameweek with fewer than 5 total fixtures at generation
  time (e.g. because several matches are already postponed)
- When round generation runs for that gameweek
- Then generation aborts for that gameweek and logs an error, rather than
  producing a round with fewer than 5 matches — the same "abort rather
  than generate a degraded round" pattern REQ-101/103 already establish
  for xG Grid

**Test level:** Unit (subset-selection logic returns the minimum-span
5-match subset across a range of fixture-list fixtures, including a tie
case and a fewer-than-5-fixtures abort case), API/Integration (round
generation produces a `Round` with `GameKey="xg-predict"` and exactly 5
matches wired as cells).

**REQ-1302 – Score prediction submission**
> As a player, I want to predict the final score of each match in an xG
> Predict round, so I have a stored prediction to be graded once that
> match finishes.

- Given an xG Predict round that has not yet locked (REQ-1303) and one of
  its 5 matches
- When the player submits a prediction for that match
- Then the prediction consists of exactly two values — predicted
  home-team goals and predicted away-team goals — each a non-negative
  integer
- And a submission with a missing, negative, non-integer, or otherwise
  non-numeric value for either goal count is rejected, with any
  previously stored prediction for that match left unchanged
- And a player may submit or resubmit (replace) a prediction for a given
  match any number of times before the round locks — there is no
  per-match attempt cap of the kind REQ-210 imposes on xG Grid/xG Path,
  since predicting a score is not a bounded-guesses-at-a-hidden-answer
  interaction, only a value the player remains free to reconsider until it
  locks
- Given an xG Predict round that has already locked (REQ-1303)
- When a player attempts to submit or resubmit a prediction for any of its
  5 matches — including one whose own individual kickoff has not yet
  occurred
- Then the submission is rejected, matching this document's existing "no
  guesses/predictions accepted once locked" convention (REQ-201, REQ-302)

**Test level:** Unit (validation of the two-non-negative-integer shape),
API (submit/resubmit before lock succeeds and overwrites the prior value;
submit after lock is rejected for every match in the round, not only ones
that have individually kicked off).

**REQ-1303 – Round lock at the first match's kickoff (exploit prevention)**
> As xG Arcade, I want an entire xG Predict round to lock the instant the
> first of its five matches kicks off, so a player can never see one
> match's real result before locking in predictions for the other four.

- Given an xG Predict round with 5 selected matches, each carrying its own
  scheduled kickoff time (REQ-1301)
- When the earliest of those 5 kickoff times arrives
- Then the entire round locks at that instant — no further prediction
  submission or resubmission (REQ-1302) is accepted for any of the round's
  5 matches from that point on, including matches whose own individual
  kickoff has not yet occurred
- And this is a deliberate exploit-prevention rule, not an incidental side
  effect of one match's own kickoff: without it, a player could submit a
  prediction for the earliest-kicking-off match, observe its real result
  once available, and only then predict the remaining matches with that
  result already known — locking the whole round at the first kickoff
  removes that window entirely, since every other match's own kickoff is,
  by construction (REQ-1301's tightest-clustering selection), no earlier
  than this instant
- Given a specific match in the round whose own kickoff is later than the
  round's lock instant (true for every match except whichever one kicks
  off earliest)
- When a player attempts to submit a prediction for that specific match
  after the round has locked but before that match's own individual
  kickoff
- Then the submission is still rejected — that match not having kicked
  off yet does not, on its own, make a late prediction acceptable once the
  round-level lock above has occurred
- And this lock is a distinct concept from REQ-302's round `Closed`
  status: an xG Predict round can be simultaneously `Active` per REQ-302
  (its own `StartTime`/`EndTime` window has not yet ended) and locked per
  this requirement (no further predictions accepted) — REQ-302's
  `Upcoming`/`Active`/`Closed` derivation is unchanged by this requirement
  and continues to govern only whether the round exists/has ended, not
  whether predictions are currently being accepted

**Test level:** Unit (lock instant computed as the minimum kickoff time
across the round's 5 matches), API (a prediction submitted for a
not-yet-kicked-off match is rejected once the round's lock instant has
passed; a prediction submitted for any match before the lock instant
succeeds), E2E (submitting predictions for matches 2-5 after match 1 has
kicked off, but before match 2 individually kicks off, is rejected end to
end).

**REQ-1304 – Independent, partial-credit scoring per match**
> As a player, I want each match prediction scored on three independent
> components, so a close-but-not-exact prediction still earns partial
> credit instead of an all-or-nothing result against the exact scoreline.

- **Scoring direction — deliberate, product-owner-confirmed exception to
  ADR-0021 (2026-08-30):** unlike every other game on this platform, xG
  Predict uses conventional higher-is-better scoring, not this platform's
  golf-style convention. A correct component **awards** points; an
  incorrect component awards none; points accumulate normally across a
  round; and a player's (and the leaderboard's) goal is to **maximize**
  their total for `GameKey="xg-predict"`, not minimize it. This was asked
  directly and confirmed explicitly by the product owner as a deliberate
  exception, not an oversight or a drift from ADR-0021 — every other game
  (xG Grid, xG Path) is completely unaffected and remains golf-style
  exactly as ADR-0021 and §2's "Points/Score" definition already require.
  See ADR-0095 for the full rationale and the resulting structural changes (e.g. how a
  per-`GameKey` leaderboard sort direction is represented) — this REQ
  records only the acceptance criteria that follow from the decision, not
  how it's implemented.
- Given a graded match (REQ-1305) with a confirmed real final score and a
  player's stored prediction for that match
- When that prediction is scored
- Then three independent point components are computed for it:
  1. **Outcome component** — awards points if the predicted 1X2 outcome
     (home win / draw / away win, derived by comparing predicted home
     goals to predicted away goals) matches the actual match's 1X2
     outcome (derived the same way from the real final score); awards
     nothing if it does not match
  2. **Home-goals component** — awards points if the predicted home-team
     goal count exactly matches the actual home-team goal count; awards
     nothing if it does not match
  3. **Away-goals component** — awards points if the predicted away-team
     goal count exactly matches the actual away-team goal count; awards
     nothing if it does not match
- And these three components are scored independently of one another — a
  prediction can earn the outcome component's points without earning
  either goal-count component's points, or vice versa (e.g. predicting
  2-1 for an actual 3-1 result earns the outcome and away-goals
  components' points but not the home-goals component's)
- And each component's award value (points for a match, 0 for a miss) is a
  `ScoringRules`-owned constant — following the same "exact point values
  are an implementation detail, not specified by the REQ text" precedent
  as `MaxPointsPerCell`/`ScoringRules.PointsFromUniqueScore` (REQ-204/205)
  and `ClueEfficiencyScoringStrategy` (REQ-1206); only that naming/
  ownership convention carries over from those precedents — their
  golf-style direction does not, per the scoring-direction bullet above
- And a player's total score for a round is the sum of all components
  across all 5 matches (up to 15 components total, each contributing
  either 0 or its award value), following REQ-206's existing "total score
  per round" pattern in structure only — REQ-206's own total is itself
  golf-style (xG Grid); this REQ's total is higher-is-better, per the
  scoring-direction bullet above
- And xG Predict's own leaderboard ranking (REQ-401/410, extended to this
  third `GameKey` — see §4.14's "Leaderboard participation" note) ranks
  `GameKey="xg-predict"` by **highest** total first — rank #1 is the
  highest total, the reverse of REQ-404's existing ascending (lowest-wins)
  sort, which is unaffected and continues to apply exactly as written to
  every other `GameKey`. The mechanism by which the leaderboard's sort
  direction becomes per-`GameKey` rather than a single platform-wide
  direction is not decided by this REQ — see ADR-0095
- And this requirement does not itself decide the mechanism — a new
  per-`GameKey` `IScoringStrategy` implementation for
  `GameKey="xg-predict"`, following ADR-0040's existing per-game
  scoring-strategy resolution, is the expected shape, not fixed here

**Test level:** Unit (each of the 8 match/no-match combinations across the
3 components, plus an exact-scoreline case, computes the correct
component-level and summed points, higher-is-better; a not-yet-graded
match contributes no components to any total).

**REQ-1305 – Asynchronous, per-match grading after the round has locked**
> As xG Arcade, I want each match in a locked xG Predict round graded
> sometime after that match actually finishes, once its real final score
> is confirmed, so a round can be scored even though the correct answers
> don't exist yet when the round opens or locks.

- **Context — this is a new lifecycle shape, distinct from every other
  game's round-close scoring flow.** REQ-205 (xG Grid) and its xG Path
  equivalent both compute and lock a round's scores at round close,
  because the correct answer — a cell's population of correct guesses, or
  a puzzle's fixed target player — already exists by the time the round
  closes. An xG Predict round's correct answers (each match's real final
  score) do not exist at round-open or round-lock (REQ-1303) time at all —
  they only exist after each individual match finishes, which happens
  hours to days after the round locks, and not necessarily by the round's
  own scheduled `EndTime`/close (REQ-302). Grading an xG Predict round is
  therefore a genuinely separate, asynchronous concern from "the round is
  locked," and this requirement deliberately does not assume it is
  triggered by, or reuses, `ScoreLockingService.LockRoundScoresAsync`'s
  existing round-close trigger point (REQ-205) — a distinct trigger is
  required. What that trigger is (a new scheduled job, an event, or
  something else) is an architecture/implementation decision, not made
  here — see "Needs an ADR" below.
- Given a match in a locked xG Predict round whose scheduled kickoff plus
  its typical duration has already passed
- When the grading process next runs and checks that match
- Then it fetches that match's real final score from API-Football's
  fixtures endpoint (see ADR-0094) and, if that match's fixture status is reported as
  confirmed/finished, grades every player's stored prediction for that
  match per REQ-1304 and persists the resulting components
- Given a match's fixture status is checked but is not yet reported as
  confirmed/finished — accounting for API-Football's own documented
  allowance of up to 48 hours for some competitions to fully confirm a
  result
- When the grading process runs for that match
- Then that match is left ungraded, not scored with any placeholder or
  default value, and is retried on a subsequent run — the grading process
  never assumes a single fetch attempt is sufficient
- Given a match that has already been graded (its real final score was
  confirmed and every stored prediction for it scored)
- When the grading process runs again and reaches that match
- Then it does not re-fetch or re-grade it — grading is idempotent,
  matching this document's existing idempotency convention (REQ-205's
  "safe to call again on an already-closed round")
- And a round's total-score contribution to the leaderboard (REQ-401/
  REQ-410) reflects only matches that have actually been graded — an
  ungraded match contributes no components (not a placeholder worst-case
  value) to a player's total until it is graded
- **Confirmed by the product owner (2026-08-30): a postponed or abandoned
  match is voided, not penalized.** Given a match in a locked round that
  is postponed or abandoned (never played to a confirmed final result) —
  when the grading process determines this from API-Football's fixture
  status — then that match's three point components are voided for every
  player: none of the three components is computed or contributes
  anything to any player's round total, as if that match were not part of
  the round for scoring purposes — while the round's other 4 matches
  continue to grade normally and independently, per the criteria above.
  This was originally logged as a proposed, unconfirmed default (see §7's
  matching entry for the resolution) — now settled, not open.

**Test level:** Unit (grading a confirmed match applies REQ-1304's formula
and persists results; a not-yet-confirmed match is left ungraded and is
retried, not scored; a match already graded is not re-fetched or
re-scored on a second run — idempotency; a postponed/abandoned match's
components are voided, contributing nothing rather than being computed,
per the confirmed voiding rule above), API/Integration (round total-score reads reflect only
graded matches, growing as further matches are graded over time).

**Needs an ADR:** two structural questions are deliberately left open
here, not decided in this requirement:
1. **What triggers grading.** Whether a new scheduled job (analogous to
   `generate-grid-round.yml`/`generate-path-round.yml`'s cron pattern,
   ADR-0072), an event-driven mechanism, or something else checks
   locked-but-ungraded matches and how often, is left to
   architecture/implementation.
2. **How REQ-302's round `Closed` status and REQ-401/404/410's
   leaderboard participation interact with a round that is locked
   (REQ-1303) but not yet fully graded.** Whether a round's `EndTime` is
   scheduled generously enough that grading is always complete by the
   time it closes in practice, whether a round can be `Closed` while some
   of its matches remain ungraded, and whether the leaderboard shows a
   partial/growing total for such a round or withholds it until every
   match is graded, are not decided here — this requirement governs only
   how and when an individual match is graded.

Flagged for `architecture-reviewer`/the implementer to resolve via a new
ADR before or alongside implementation — a requirements document specifies
WHAT and HOW TO VERIFY, not HOW TO BUILD.

**Leaderboard participation:** xG Predict needs no new leaderboard
requirement of its own. REQ-401 (Global League membership) and REQ-410
(Global League's all-time ranking scoped per `GameKey`) are both already
written in fully game-generic terms — REQ-401 never names a specific game,
and REQ-410's acceptance criteria are phrased as "the platform hosts more
than one game, each with its own `GameKey`," not "exactly two games." A
third `GameKey` (`"xg-predict"`) is covered by their existing text without
modification, the same way REQ-410 already required no edit when xG Path
was added as the second game. See this document's own accompanying
summary for confirmation that REQ-401/410 were checked and found to
generalize cleanly, rather than assumed.

**REQ-1306 – Explicit "confirm and lock" action for a round's predictions**
> As a player, I want to explicitly confirm that my 5 predictions are
> final, so I have a clear personal sense of closure even though xG
> Predict has no completion celebration and I won't know the outcome for
> hours or days.

- **Context — this replaces, rather than reproduces, REQ-1210's
  completion celebration for this game.** REQ-1210 (round-completion
  animation) triggers immediately when a player finishes their last cell,
  because xG Grid/xG Path both reveal correctness synchronously — the
  player knows right then whether they did well. xG Predict cannot offer
  that: predictions are gradable only after each real match finishes
  (REQ-1305), which can be hours to days later, often while the player
  isn't using the product at all. The product owner confirmed directly
  (2026-08-30) that xG Predict gets **no** completion celebration of any
  kind — not on submission, not on full grading — closing the open
  question this document previously logged in §7. Instead, submission
  itself gets an explicit confirmation step, giving the player a clear
  "I'm done" moment without pretending to know a result that doesn't
  exist yet.
- Given a player has entered a score prediction for all 5 matches in the
  active xG Predict round (REQ-1302), and the round has not yet locked
  (REQ-1303)
- When the player chooses to confirm and lock their predictions (a
  distinct, explicit action — not merely having filled in all 5 fields)
- Then the UI presents a confirmation prompt stating the predictions
  cannot be edited after this point, requiring an explicit second
  affirmation (e.g. "Are you sure? You can't change your predictions
  after confirming.") before proceeding
- Given the player affirms the confirmation prompt
- When their predictions are locked
- Then further edits to any of that round's 5 predictions are rejected
  from that point on for that player specifically, even though the round
  itself (REQ-1303) has not yet locked for other players and even though
  the round's own automatic lock (first match's kickoff) has not yet
  occurred
- Given the player dismisses or cancels the confirmation prompt instead of
  affirming it
- When they return to the round
- Then their 5 predictions remain freely editable exactly as REQ-1302
  already specifies, unaffected by having opened (and backed out of) the
  confirmation prompt
- And this per-player early lock is independent of, and does not
  substitute for, the round-wide automatic lock at the first match's
  kickoff (REQ-1303) — a player who never uses this action still has
  their predictions locked automatically at that point, exactly as
  REQ-1303 already specifies
- And confirming and locking is entirely optional — REQ-1302's existing
  "freely resubmittable before lock" behavior remains the default for any
  player who never uses this action

**Test level:** Unit (predictions are rejected after this per-player lock
even though the round's own automatic lock time hasn't arrived; canceling
the confirmation prompt leaves predictions editable; a player who never
confirms is unaffected and still locks automatically at REQ-1303's round
lock). UI (the confirmation prompt requires an explicit second
affirmation, not a single click — REQ-718's own guest-logout confirmation
prompt is the closest existing precedent for a player-facing, irreversible
action warranting one).

---

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
- **Splash screen shown every unauthenticated load, not just once
  (REQ-719):** no persisted "already seen it" flag — every time the app
  determines there's no valid session, the splash screen is shown before
  `AuthScreen`, whether that's a true first-ever visit, a later reload, or
  a return from logout. Simpler (a single unauthenticated entry point, no
  extra persisted state to manage or get out of sync) and consistent with
  how the rest of the frontend already resets to a starting screen on
  every fresh load (e.g. `screen` defaults to `'game-select'` on mount
  rather than restoring the last-viewed screen). Revisit if real use shows
  a frequent visitor finds the extra click annoying.
  **Status note (2026-07-25, REQ-721):** the parenthetical rationale above
  — "screen defaults to `'game-select'` on mount rather than restoring the
  last-viewed screen" — describes the pre-REQ-721 app only. Once REQ-721
  ships, a reload of an *authenticated* session restores the last-viewed
  screen via the URL instead. This bullet's own subject (the splash screen
  always showing for an *unauthenticated* load, no persisted flag) is
  unchanged and still accurate — only the supporting analogy is now
  out of date.

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

REQ-717's 2026-07-21 "Bot-check (captcha) for guest creation" addition
raised no open product question of its own — provider choice (Cloudflare
Turnstile), scope (guest creation only), widget mode (invisible/managed,
recommended), and the failure-mode/error-distinguishability requirement
were all decided directly by the product owner or follow established
precedent (ADR-0013's mediation boundary), and are recorded in REQ-717's
acceptance criteria and ADR-0037, not left open here. **Note the "scope
(guest creation only)" line above is superseded** — see the 2026-07-25
entry immediately below. **Note the "widget mode (invisible/managed,
recommended)" line above is also superseded** — a later 2026-07-25
sign-in-latency investigation reversed this to an always-visible
checkbox (ADR-0037's third amendment; see this REQ's own corrected
Widget UX recommendation above).

REQ-717's 2026-07-25 scope-correction addition ("captcha now applies to
`POST /auth/guest`, `POST /auth/signup`, and `POST /auth/login`") likewise
raised no open product question of its own. It was a correction of a
confirmed-wrong technical assumption (Supabase's captcha-protection toggle
is project-wide, not per-endpoint — see `NOTES.md`'s 2026-07-25 entry and
ADR-0037's matching amendment), not a fresh product choice among live
alternatives — extending the existing captcha mechanism to signup/login
was the only option that keeps the platform's captcha protection actually
functional once Supabase's real behavior is accounted for; the alternative
(turning the dashboard toggle back off) would remove guest-creation bot
protection entirely, undoing REQ-717's original 2026-07-21 decision rather
than fixing the bug. Recorded in REQ-717's and REQ-701's acceptance
criteria and ADR-0037's amendment, not left open here.

Both items from the terms-of-service/privacy-policy drafting were
resolved 2026-07-06:

- **Minimum age:** 16, enforced via a self-declared checkbox at signup
  ("I am at least 16 years old") — no age verification performed, but
  signup cannot proceed unchecked. See REQ-701.
- **Governing law / entity:** Swedish law; operated as a personal project
  (not under SyVe or a separate registered entity) unless that changes
  later. See `docs/legal/terms-of-service-draft.md`.

REQ-215/509/510's 2026-07-28 draft (player-submitted answer suggestions +
admin Wikidata search/commit) raised one genuine open product question,
resolved 2026-08-01: whether an admin-approved suggestion (REQ-509) should
retroactively correct the specific guess(es) it was submitted against —
the original submitter's own now-confirmed-correct guess, and/or any other
player's identical guess against the same cell during the same round — or
whether the suggestion exists purely to fix the underlying data for future
guesses, leaving every already-scored guess (correct or not) untouched.
**Decided (2026-08-01):** no retroactive rescoring, confirmed by the
product owner. REQ-215's own default (the only option that didn't require
inventing a scoring-adjustment mechanism found nowhere else in this
document) is confirmed correct and final. See REQ-215's own "No
retroactive rescoring" acceptance criteria.

Two related items were flagged inline in REQ-215/509 rather than here,
since they're build-order/architecture questions, not product decisions:
(1) whether this Tier 1/2-sized new pipeline should be pulled forward ahead
of `MVP-SCOPE.md`'s own ordering (REQ-215's "Tier framing" note) —
**resolved 2026-08-01:** pulled forward by deliberate product decision
(the feature was requested directly, by name, the same basis
REQ-108/REQ-214/REQ-402-403/REQ-717 were each pulled forward on), recorded
in `MVP-SCOPE.md`'s Tier 1 section; REQ-215's submission half was built
the same session (S-089), REQ-509/REQ-510's admin half was queued as
S-090 at the time and has since been built (2026-08-08 — see REQ-509's own
status note); and (2) whether REQ-509's
admin-reviewable suggestions should surface through REQ-503's existing
(currently empty) review queue or a new, separate view, and whether a new
ADR should record that choice (REQ-509's own status note) — **resolved
2026-08-01:** a new, separate admin view, not merged into REQ-503's queue,
recorded in ADR-0053 (`docs/decisions/0053-player-suggestions-separate-admin-view.md`),
which also reconfirms ADR-0007's autocomplete/correctness boundary applies
to the new commit path.

**New (2026-08-08), unresolved:** a tester reported xG Path targets are
"too hard to identify from the clues" and suggested arbitrary-sounding
fixes (e.g. requiring a birth year after 1970, or a stint at a "top-4 club
in a top-5 league"). Investigation (not implementation) found this is
plausibly not primarily a target-familiarity problem — ADR-0056's
Wikipedia-sitelink filter already screens the *target player* for
recognizability, and there is only one data point (this report, plus the
original "Austrian guy" complaint that motivated ADR-0056 itself) — too
little evidence to justify retuning `PlayerFamiliarityService
.MinSitelinkCount` in either direction; ADR-0056's own Follow-up note
already anticipates revisiting that constant once more play data exists,
and doing so needs no new ADR (see that ADR's "For AI agents" note) when
it happens. A more structurally plausible cause was identified instead:
REQ-1203 reveals club stints in strict chronological (earliest-first)
order, and REQ-1201/ADR-0047 only require *one* stint anywhere in a
target's career to be at a seeded club (`MVP-SCOPE.md`'s hand-curated
~15-club list) above the appearance threshold — nothing about eligibility
or the familiarity filter requires that stint, or any recognizable club,
to appear early. So even a genuinely familiar, familiarity-filter-passing
target can have their *first* revealed clue turn be an obscure youth-team
or lower-league stint from early in their career, before any seeded/
recognizable club ever appears — making the opening clues feel
unfairly obscure independent of how famous the target ultimately is or
how permissive `MinSitelinkCount` is set. This is a genuine open product
question, not a technical default: **should xG Path's clue-reveal order
(REQ-1203) continue to be strictly chronological, or should it weight
toward showing a recognizable (e.g. seeded-club) stint earlier, and if so,
does that trade away the "genuine progressive challenge" intent REQ-1203's
own user story states (chronological order was the deliberate "least-
narrowing-first" choice, not an oversight — see REQ-1203's user story and
its `N`-way club-split acceptance criteria) for a different kind of
fairness?** This needs a product decision, not a default, because
reordering clues by recognizability rather than chronology changes what
"a genuine progressive challenge" means for this game and could make
puzzles trivially easy for a well-known target instead of appropriately
hard — the opposite failure mode from the one reported. No REQ or ADR
changed for this yet; recorded here pending that decision. See the
`requirements-writer` review of 2026-08-08 (this entry) for the full
investigation, including why the tester's own two suggested fixes were
not adopted as-is: a fixed birth-year cutoff is an arbitrary, undocumented
proxy with no basis (would also exclude many genuinely famous
pre-1970-born targets) and a "top-4 club/top-5 league" requirement would
need a real league-tier data model — explicitly out of scope for the
problem actually diagnosed here, and already rejected on the same
"disproportionate to the problem" grounds by ADR-0047's own alternatives
table for a closely related eligibility question.

**New (2026-08-22), unresolved:** REQ-1210 (round-completion animation)
specifies the trigger as the moment a player's own guessing activity locks
the last cell available to them in a round. It deliberately does not say
whether that animation should play only the first time this happens, or
every time the player subsequently views that round after it is already
complete (e.g. reloading the page, or navigating back into the
game screen after finishing). This is a genuine product/UX decision, not
a technical default: there is no directly applicable existing precedent
either way — `docs/design-document.md` §2's badge-dock cue deliberately
replays on every reveal, but that is a small, user-initiated, per-cell
interaction, not an automatic, full-round celebration, so extending that
precedent by analogy would be guessing, not following an established
pattern. Playing it every time risks feeling repetitive/annoying on a
revisit; playing it only once requires persisting "has this player already
seen the completion animation for this round" somewhere (a genuine new
piece of state, not currently modeled anywhere), which is itself a
build-order/scope question this document shouldn't answer by default.
Recorded here pending a product decision; REQ-1210's own acceptance
criteria describe only the trigger condition and content, not replay
frequency, until this is resolved.

REQ-1305's postponed/abandoned-match voiding question was resolved
2026-08-30: the product owner confirmed the proposed default as written
— a postponed or abandoned match's three point components are voided for
every player (none contributes anything to any player's total for that
match), while the round's other 4 matches still grade normally. No
alternative (redistributing that match's weight, scoring it as a fixed
neutral value) was requested. REQ-1305 itself now states this as
confirmed, not proposed — see its own text.

REQ-1210's applicability to xG Predict was resolved 2026-08-30: the
product owner confirmed xG Predict gets **no** completion celebration at
all — not on prediction submission, not once all 5 matches are graded.
REQ-1210's existing wording (triggered by synchronous cell resolution) is
therefore correctly scoped to xG Grid/xG Path only and needs no edit; it
simply doesn't extend to xG Predict, which has its own closure mechanism
instead — REQ-1306 (new), an explicit, player-initiated "confirm and lock
my predictions" action with a destructive-action-style confirmation
prompt, giving the player a clear sense of finishing without any
celebration implying a result that isn't known yet.
