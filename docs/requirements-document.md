---
doc_id: requirements-document
title: Requirements Document
version: "0.53"
status: draft
last_updated: 2026-07-14
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

Version 0.53 · 2026-07-14

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

- **Status note (2026-07-12):** Club × Club is implemented, `docs/backlog.md`
  S-030 — `GridGameModule.GenerateInstanceAsync` now picks between
  Country × Club and Club × Club per instance (`SelectPairing`): randomly,
  whenever the seeded reference data can support either (enough countries
  and clubs for Country × Club, and at least `2 × Size` distinct clubs for
  Club × Club, since REQ-102 forbids a value appearing on both axes),
  falling back deterministically to whichever single pairing is feasible
  when only one is. This was a scope restriction in `MVP-SCOPE.md`, not a
  limit this REQ ever imposed — `CategoryPairingRules.IsAllowedPairing`
  already permitted Club × Club before S-030. Trophy pairings are queued as
  S-031, scoped to Trophy × Country/Trophy × Club only for v1 (a
  single-trophy pool can't satisfy REQ-102's N-unique-headers rule for
  Trophy × Trophy, so that
  pairing is structurally unreachable until more trophies exist, not
  separately banned).
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

- **Status: Proposed, queued as `docs/backlog.md` S-031 (pulled forward
  from Tier 1, `MVP-SCOPE.md`, 2026-07-12).** v1 is deliberately narrower
  than the acceptance criteria below: exactly one trophy, **Ballon d'Or**,
  an individual award resolvable via Wikidata's `P166` ("award received") —
  the same simple query shape as the existing Country/Club intersection
  query. Team-competition trophies (World Cup, Champions League, the rest
  of the example list below) need a structurally different query (squad
  membership + tournament result, no single property linking a player to
  "won this tournament") and are explicitly deferred to a follow-up story,
  not part of S-031.
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

**Test level:** Unit (`StaleClubAttributeCleanerTests.cs` — removes stale
rows and leaves zero cached matches; scopes strictly to the named clubs and
to `AttributeType == "club"`; safe to re-run)

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
  `CellState.tsx`'s state-3 branch now renders "no attempts left ·
  {MaxPointsPerCell} pts", matching `design-document.md` SCREEN-01a's mock
  (which already showed this, corrected during S-028/ADR-0021 — only the
  component was never updated to match). The value is a known constant
  (a new frontend-side `MAX_POINTS_PER_CELL` in `lib/scoringRules.ts`,
  mirroring `ScoringRules.MaxPointsPerCell` the same way
  `MAX_ATTEMPTS_PER_CELL` already mirrors its backend counterpart — display
  only, never enforcement), so this is a pure rendering fix, not a new
  calculation. State 4's incorrect outcome is intentionally unchanged —
  round-closed data still isn't reachable via `GET /rounds/current` (S-011
  scope gap), so there's no live path to exercise a "final" points value
  there yet either. See REQ-206 for the matching running-total fix.
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

**Test level:** Unit (calculation logic), API, UI (state 1 and state 4 at
rest render identically in structure — checkmark + points, no live
indicator of any kind, no percent)

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

- **Status: Proposed, queued as `docs/backlog.md` S-032 (pulled forward
  from Tier 1, `MVP-SCOPE.md`, 2026-07-12, by deliberate choice rather than
  the stated trigger having strictly fired).** Builds exactly what ADR-0007
  already specifies — `PlayerNameIndex` populated via a one-time bulk
  Wikidata query for `P106` (association football player) — no new
  architectural decision. This story covers the suggestion-list UX only;
  REQ-208's alias/fuzzy-typo-tolerance clauses for guess *scoring* remain
  separately deferred.
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

- **Status: Partially implemented (Tier 0's "simple half" only, S-009, per
  `MVP-SCOPE.md`).** Built: `PlayerNameNormalizer.Normalize`
  (`XGArcade.Data`) lowercases, strips diacritics, strips punctuation
  (added in S-009 — this closes a real pre-existing gap left over from
  S-006, which stripped diacritics but not punctuation), and collapses
  whitespace; `Player.NormalizedFullName` is kept in lockstep with
  `FullName` via its setter and backfilled for pre-existing rows
  (`PlayerNormalizedFullNameBackfiller`); `GridGameModule
  .ScoreSubmissionAsync` compares the normalized guess directly against
  `Player.NormalizedFullName` (`IPlayerStoreRepository
  .GetPlayersByNormalizedFullNameAsync`). **Not built** (deliberately
  deferred per `MVP-SCOPE.md`'s Tier 0 scoping, not an oversight): matching
  via a maintained alias/stage-name list (`PlayerAlias` exists as an entity
  since S-006 but is not queried for guess-time matching at all — only
  exact `NormalizedFullName` equality is checked), and edit-distance/fuzzy
  typo tolerance. A guess with a typo or an alias name (e.g. "Kaká" typed
  as a nickname rather than a spelling variant of the same string) that
  isn't an exact normalized match to `FullName` is scored incorrect today.
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
- Given a submitted guess resolves to a specific candidate in
  `PlayerNameIndex` (REQ-207/208 — a real, known player)
- When `PlayerAttribute`/`PlayerOverride` has no record at all — neither
  confirming nor denying — for that player against the cell's category types
- Then the system performs a live lookup for that specific player's
  attributes, using the same Wikidata-first, API-Football-fallback
  waterfall as REQ-103 (ADR-0011)
- And the result is persisted immediately as unverified data, in the same
  request — never deferred to a later batch sync (ADR-0010)
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
- And the explainer is reachable from the grid screen at any time an active
  round is shown — not gated behind having attempted any particular cell,
  and not a one-time first-visit-only prompt
- And the explainer's content is general to the scoring/live-update
  mechanic — it never includes cell-specific numbers, since it must remain
  valid regardless of which cells, or how many, the player has attempted

**Test level:** UI (explainer opens from the header entry point and closes
without losing in-progress state; contains text covering all three required
content points — presence checks against required concepts, not exact
wording)

---

### 4.3 Rounds

**REQ-301 – Configurable round frequency**
> As an admin, I want to configure how often new rounds are created (e.g.
> twice per week), so play frequency can be adjusted without a code change.

- **Status: Partially implemented (Tier 0, S-008).** The "one round ahead"
  rule itself is fully built: `RoundGenerationService`
  (`XGArcade.Core.Rounds`) skips generation if an upcoming/not-yet-started
  round already exists for the `GameKey`, otherwise resolves the owning
  `IGameModule` (via the new `IGameModuleResolver`), generates its instance,
  and chains the new round's `StartTime` from the previous round's
  `EndTime` — exactly the acceptance criteria below. `generate-round.yml`'s
  cron (Tue+Fri 06:00 UTC) triggers this via the bearer-token-protected
  `POST /internal/generate-round` (`XGArcade.Api.Rounds.InternalRoundEndpoints`),
  registered in every environment since this is a legitimate scheduled job
  (CONT-05), not a test-data endpoint. What's not built: "configured...so
  play frequency can be adjusted without a code change" — the schedule
  lives in `generate-round.yml`'s cron expression, and `RoundSchedulingOptions`
  (`GameKey`/`RoundDuration`/`AllowGuessChange`/`GridSize`) is a plain C#
  object with hardcoded defaults registered in `Program.cs` (same
  non-appsettings-bound pattern as `Games.XGGrid`'s `GridGenerationOptions`),
  not an admin-facing configuration surface — changing frequency today means
  editing both files together (the cron and `RoundDuration`, which must stay
  coupled — see `RoundSchedulingOptions`' own doc comment and NOTES.md), a
  code change either way. `GridSize`'s find-or-create-a-`GridTemplate`-by-size
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
- Given a new user registers
- Then the user is automatically added to `League(type="global")`
- And this requires no action from the user

**Test level:** Unit, API

**REQ-402 – Create a custom league**
> As a player, I want to create my own league and invite friends, so we can
> compete in a smaller group.

- Given a logged-in player
- When the player creates a league with a name
- Then a `League(type="custom")` is created with a unique `invite_code`
- And the creator is automatically added as a member

**Test level:** Unit, API

**REQ-403 – Join a league via code**
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
  S-028/ADR-0021).** `GET
  /leagues/global/leaderboard` (`XGArcade.Api.Leagues.LeaderboardEndpoints`)
  → `ILeaderboardService`/`LeaderboardService` (`XGArcade.Core.Leagues`)
  implements exactly this ranking (members' `SUM(FinalPoints ?? 0)`,
  **sorted ascending** — ADR-0021: xG Arcade is scored like golf, lowest
  total wins, so rank #1 is the lowest total, not the highest — ties broken
  by display name) for the global league only — custom leagues (REQ-402/403)
  don't exist yet, so there is exactly one leaderboard to read today;
  SCREEN-03's frontend (`LeaderboardScreen.tsx`) shows only the Global list,
  no tab switcher.
  **Known gap:** the response is unbounded (every member in one payload) —
  see REQ-607's own status note for why this is an acknowledged, deliberate
  Tier 0 gap rather than an oversight.
- Given a player is a member of at least one league
- When the player opens a league's leaderboard
- Then the ranking is based on the same underlying score data (no separate
  score calculation per league), filtered by league membership
- And the list is correctly sorted ascending by total score — lowest wins
  (ADR-0021)

**Test level:** Unit, API, UI

**REQ-405 – Leaderboard time-window resolutions** *(Status: Proposed, not yet
implemented — drafted 2026-07-12, see `docs/backlog.md` S-027. Open design
questions resolved 2026-07-12 — see below; implementation-ready.)*
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
- Performance: REQ-607's existing pagination gap (leaderboard responses are
  currently unbounded) gets worse with four more query shapes to index for —
  **not resolved as a product decision, still an implementation-time
  requirement**: S-027's acceptance criteria requires a REQ-607-aligned
  indexing plan as part of implementing this REQ, not just "add a `WHERE`
  clause"

**Test level:** Unit, API, UI

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

- **Status: Partially implemented (Tier 0, S-012).** `source` and
  `confidence` are visible via `GET /admin/player-data/unverified`, but only
  for rows with `Confidence == "unverified"` — there is no admin endpoint or
  view over verified `PlayerData`, so "any player data point" (below) is not
  yet true; only the unverified subset is browsable. No admin UI exists
  (API only) — the "UI (admin)" test level below is not yet met.
- Given any player data point
- Then `source` (e.g. `wikidata`, `api_football`, `live_lookup`, `manual_override`)
  and `confidence` (`verified` / `unverified`) are always visible in the admin view

**Test level:** API, UI (admin)

**REQ-503 – Admin review of unverified data**
> As an admin, I want to quickly review and approve/correct auto-fetched
> data, so the cache is quality-assured over time.

- **Status: Partially implemented (Tier 0, S-012).** Only the "review list"
  half is built: `GET /admin/player-data/unverified`
  (`XGArcade.Api.Admin.AdminEndpoints`) returns every unverified
  `PlayerData` row with `Source`/`Confidence`/`PlayerFullName`. The
  "correct" action exists only indirectly, as a separate call to
  `POST /admin/player-overrides` (by `PlayerId`/`Field`, not by the
  `PlayerData` row's own id) — there is no "approve → verified" action and
  no "remove the data point" action; a `PlayerData` row's `Confidence`
  cannot currently be flipped to `verified`, nor can a row be deleted, via
  any endpoint. "The action is logged with `admin_id` and a timestamp" is
  satisfied for the override-creation path by `PlayerOverride
  .LockedByAdminId`/`LockedAt` on the override row itself (no separate
  audit-log table) — there is no equivalent log for approve/remove since
  those actions don't exist yet. No admin UI exists (API only).
- Given data with `confidence = "unverified"`
- When an admin opens the review view
- Then the admin can approve (→ `verified`), correct (creates a `PlayerOverride`),
  or remove the data point
- And the action is logged with `admin_id` and a timestamp

**Test level:** API, UI

**REQ-504 – Admin UI page** *(Status: Proposed, not yet implemented — drafted
2026-07-12, see `docs/backlog.md` S-026)*
> As an admin, I want an actual page (not just API calls) to perform admin
> actions, so I don't need to script HTTP requests to correct data, manage
> rounds, or manage users.

- Given the S-012 admin API (REQ-501/502/503) and REQ-505/506's new endpoints
  (this REQ adds no endpoints of its own — it is the UI surface over all of
  them) already require the existing "Admin" authorization policy
  (`Admin__UserIds`)
- When a user whose id is in `Admin__UserIds` logs in
- Then they can reach a protected admin screen (not linked from the normal
  player nav) exposing: the REQ-503 unverified-data review list and
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

**REQ-505 – Admin round control (non-Production only)** *(Status: Proposed,
not yet implemented — drafted 2026-07-12, see `docs/backlog.md` S-026)*
> As an admin testing the game, I want to end the active round or adjust its
> schedule on demand, so I don't have to wait for real time to pass to test
> round-close behavior outside of the existing E2E harness.

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

**REQ-506 – Admin user deletion (non-Production only)** *(Status: Proposed,
not yet implemented — drafted 2026-07-12, see `docs/backlog.md` S-026)*
> As an admin testing the game, I want to delete a test user's account, so I
> can clean up seeded/test accounts without touching the database directly.

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

- **Status: Partially implemented (Tier 0, S-004/S-011/S-016/S-017).** The
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
  clause (§5's "Decisions made as sensible technical defaults") and the
  account-enumeration-safe error message are not yet implemented; Supabase
  Auth's own error responses are currently passed through as-is. The rest
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

- **Status: Partially implemented (Tier 0, S-011).** The pagination clause
  immediately below is a real, currently-unmet gap, not tiered out anywhere
  else: `GET /leagues/global/leaderboard`
  (`XGArcade.Api.Leagues.LeaderboardEndpoints`) returns every member of the
  global league in one unbounded response, no cursor/pageSize. Flagged by
  an architecture-reviewer pass during S-011 and deliberately left
  unfixed for this pass — pagination itself is out of scope. **Queued as
  `docs/backlog.md` S-034 (2026-07-12)**, ahead of the original "membership
  grows large" trigger actually firing — decided proactively rather than
  waited on, since the shape was already fully specified (see
  `implementation-document.md` §6's "Leaderboard pagination" pseudocode)
  and cheap to build now. The other two bullets below are unaffected by
  this note.
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

None — REQ-405's leaderboard time-window questions (the last entry in this
section) were resolved 2026-07-12: calendar-aligned windows, UTC, locked
rounds only. See REQ-405's own status note and `docs/backlog.md` S-027.

Both items from the terms-of-service/privacy-policy drafting were
resolved 2026-07-06:

- **Minimum age:** 16, enforced via a self-declared checkbox at signup
  ("I am at least 16 years old") — no age verification performed, but
  signup cannot proceed unchecked. See REQ-701.
- **Governing law / entity:** Swedish law; operated as a personal project
  (not under SyVe or a separate registered entity) unless that changes
  later. See `docs/legal/terms-of-service-draft.md`.
