---
doc_id: requirements-document
title: Requirements Document
version: "0.27"
status: draft
last_updated: 2026-07-10
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

Version 0.27 · 2026-07-10

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
| Uniqueness score | Share of players who did NOT give the same answer for a cell |
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

- Given an NxN grid is being generated with randomized categories per row/column
- When the combination of a row and column category for a cell has fewer than
  `MIN_VALID_ANSWERS` (configurable, default 3) matching players in the local cache
- Then that combination is discarded and a new combination is randomized for that cell
- And this repeats until all N×N cells are valid, or a maximum number of
  attempts (e.g. 500) is reached, at which point generation aborts and logs an error

**Test level:** Unit (combination validation, retry logic), API (endpoint never
returns a grid with an invalid cell)

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

---

### 4.2 Guesses and scoring

**REQ-201 – Submit a guess**
> As a player, I want to guess a player for a cell, so I can participate in
> the round.

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

- Given a guess for cell X
- When the answer is checked against the effective data (an override always
  takes precedence over synced/unverified data)
- Then the guess is marked `correct = true/false` and this result is
  displayed to the player immediately — not deferred to round close
- And an incorrect guess yields 0 points regardless of uniqueness
- And a correct guess immediately locks the cell against further guesses
  (REQ-210), even though its final score isn't computed until the round
  closes (REQ-205) — "locked from further guessing" and "final score" are
  separate moments, not the same event

**Test level:** Unit

**REQ-204 – Live uniqueness percentage**
> As a player, I want to see how unique my guess is, updated live, so I get
> immediate feedback.

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
- And the value is clearly and visually marked as "live" (e.g. an icon/pulsing
  indicator plus text "updates until the round closes on [date/time]")
- And the value MAY change between page loads before the round closes

**Test level:** Unit (calculation logic), API, UI (visual "live" indicator is
present, updates on refresh)

**REQ-205 – Score locking at round close**
> As a player, I want my final score to be fixed once the round closes, so I
> know my result is permanent.

- **Status: Partially implemented (Tier 0, S-008).** `RoundCloseService`
  (`XGArcade.Core.Rounds`) is a close-only stub: given a round, it pulls
  `EndTime` forward (idempotently — never later than what's already
  scheduled) to force immediate closure, which is what REQ-806's
  `POST /internal/test-data/force-close-round/{roundId}` calls. It does not
  yet compute or persist `final_uniqueness_score`/`final_points` — that's
  deferred to S-011, once `Guess`/`Core.Scoring` exist. There is also no
  automated scheduled job yet that calls this at a round's real `end_time`
  in production — today it is only ever invoked via REQ-806's
  non-Production-only endpoint. The rest of this requirement's acceptance
  criteria are recorded below as the full/long-term definition.
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

- Given all cells in a round have been locked (REQ-205)
- When the total score is calculated
- Then the sum of `final_points` across all N×N cells for the player is shown
  as the round's total score
- And unanswered cells count as 0 points

**Test level:** Unit, API

**REQ-207 – Autocomplete must not leak answer validity**
> As a player, I want to be able to type any plausible player name, so that
> seeing a name suggested (or not) doesn't itself tell me whether it's the
> right answer.

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

- Given a cell where `allow_guess_change` is true for the round (REQ-202)
- When a player submits a guess for that cell
- Then they may submit at most 2 guesses total for that cell in that round
- And if a guess is correct, the cell locks immediately — no further
  guesses are accepted for it, even if only 1 of the 2 attempts was used
- And if both attempts are used without a correct answer, the cell locks
  as incorrect — the player sees this clearly, with 0 points guaranteed
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

- **Status: Partially implemented (Tier 0, S-008).** The status calculation
  itself is fully built and tested exactly as described below:
  `RoundStatusExtensions.GetStatus` (`XGArcade.Core.Rounds`) derives
  `Upcoming`/`Active`/`Closed` live from a `Round`'s `StartTime`/`EndTime`
  and the current time, with no separate stored status field. "Only
  `active` rounds accept new guesses" is not enforced yet — there is no
  guess-submission endpoint to enforce it against (S-009).
- Given a Round's `start_time` and `end_time`
- When a player visits the platform
- Then the Round status (`upcoming` / `active` / `closed`) is calculated
  correctly based on the current time
- And only `active` rounds accept new guesses

**Test level:** Unit, API

---

### 4.4 Leagues

**REQ-401 – Global League (default)**
> As a player, I want to automatically be part of a global leaderboard, so I
> can compare myself to all users without extra steps.

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

- Given a player is a member of at least one league
- When the player opens a league's leaderboard
- Then the ranking is based on the same underlying score data (no separate
  score calculation per league), filtered by league membership
- And the list is correctly sorted descending by total score

**Test level:** Unit, API, UI

---

### 4.5 Data management and overrides

**REQ-501 – Manual override always wins**
> As an admin, I want to manually correct incorrect player data and be
> confident the correction is not overwritten on the next sync.

- Given a `PlayerOverride` record exists for a player field
- When a sync runs and updates `PlayerData` for the same field
- Then the effective data (used by the game) continues to use the override
  value, not the newly synced value
- And the sync must not delete or modify the `PlayerOverride` table

**Test level:** Unit (merge logic), Integration (full sync cycle with an existing override)

**REQ-502 – Data source traceability**
> As an admin, I want to see where each data point came from, so I can judge
> its reliability.

- Given any player data point
- Then `source` (e.g. `wikidata`, `api_football`, `live_lookup`, `manual_override`)
  and `confidence` (`verified` / `unverified`) are always visible in the admin view

**Test level:** API, UI (admin)

**REQ-503 – Admin review of unverified data**
> As an admin, I want to quickly review and approve/correct auto-fetched
> data, so the cache is quality-assured over time.

- Given data with `confidence = "unverified"`
- When an admin opens the review view
- Then the admin can approve (→ `verified`), correct (creates a `PlayerOverride`),
  or remove the data point
- And the action is logged with `admin_id` and a timestamp

**Test level:** API, UI

---

### 4.7 Account creation and email confirmation

**REQ-701 – Create account with email and password**
> As a person, I want to create an account with my email and a password, so
> I can play and have my scores tracked.

- **Status: Partially implemented (Tier 0, S-004).** Only the 16+ checkbox
  clause below is built and enforced server-side (`POST /auth/signup`
  rejects the request with 400 before ever calling Supabase Auth if the
  checkbox is false) — see ADR-0013 (backend-mediated signup/login) and
  `MVP-SCOPE.md`. The password-policy clause (§5's "Decisions made as
  sensible technical defaults") and the account-enumeration-safe error
  message are not yet implemented; Supabase Auth's own error responses are
  currently passed through as-is. The rest of this requirement's acceptance
  criteria are recorded below as the full/long-term definition, not a claim
  of current behavior.
- Given a person provides an email address and a password meeting the
  platform's password policy
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

### 4.10 Account and data rights

**REQ-710 – Account deletion**
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

**Test level:** Unit (anonymization logic specifically — verify no
reversible link remains), API

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

None. Both items from the terms-of-service/privacy-policy drafting were
resolved 2026-07-06:

- **Minimum age:** 16, enforced via a self-declared checkbox at signup
  ("I am at least 16 years old") — no age verification performed, but
  signup cannot proceed unchecked. See REQ-701.
- **Governing law / entity:** Swedish law; operated as a personal project
  (not under SyVe or a separate registered entity) unless that changes
  later. See `docs/legal/terms-of-service-draft.md`.
