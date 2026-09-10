# ADR-0112: International caps/goals are single-recorded-value stat categories, sourced via a new batch refresh mirroring PlayerCareerStintRefreshService — extends ADR-0111

- **Status:** Accepted
- **Date:** 2026-09-10
- **Related requirements:** REQ-1501, REQ-1506, REQ-203, REQ-501
- **Related components:** COMP-06 (Data.PlayerStore), COMP-18 (Games.XGHigherLower)

## Context

Direct product-owner feedback on xG Higher/Lower (S-228/S-229, already
live): `"club"` count reads as a boring stat, and the eligible player pool
feels too obscure. Two changes were confirmed (`docs/requirements-document.md`
§4.16's 2026-09-10 status notes, REQ-1506): drop `"club"`, add
**international caps** and **international goals** as candidate stat
categories (REQ-1501's own illustrative example list already names
"international caps" — this was always an intended fit, just not built in
S-224); and add REQ-1506's new pool-wide floor ("only players with 10+
international caps are ever selected, for any category").

ADR-0111 already answered "what does a numeric stat category sourced from
`PlayerAttribute`/`PlayerOverride` concretely mean" — but only for one
shape: **a count of a player's effective rows for one `AttributeType`**,
which fits `"club"`/`"trophy"` (Wikidata models each as one row per
distinct value: one row per career club, one row per trophy won). Caps and
goals don't fit that shape. Wikidata records international caps/goals as
**qualifiers on a single statement** — `P1350` ("number of matches played")
and `P1351` ("number of goals scored") on the `P54` ("member of sports
team") statement whose team is the player's national team — i.e. one
number per player, not a set of rows to count. Forcing this into
ADR-0111's COUNT-of-rows derivation would require synthesizing N
placeholder `PlayerAttribute` rows for a caps value of N, which is exactly
the kind of contortion ADR-0111 itself never intended and ADR-0015's
override precedent doesn't cleanly cover either — an override for a
92-cap player would need to know to insert exactly 92 placeholder rows, a
fragile, undocumented reinterpretation of "override replaces the value
set."

A second, separate question this story also needs answered: how is "the
player's national-team `P54` statement" identified at all, distinct from
their club statements? Existing precedent:
`QueryNationalTeamClubIntersectionAsync`/`QueryTeamTrophyNationalTeamIntersectionAsync`
(`IWikidataClient.cs`) already use `P1532` ("country for sport", truthy
`wdt:P1532`) to identify a team entity as *a* national team (used there to
handle England/Scotland/Wales/Northern Ireland, whose players'
`P27`/citizenship is uniformly United Kingdom) — but always joined against
one *specific*, already-known country. This story needs the reverse
direction: given a player whose national team is not already known, find
whichever of their `P54` statements points to a team carrying `P1532` at
all (any country), the same way `PlayerCareerStintRefreshService` fetches
a player's *entire* `P54` history in one batch call rather than querying
against one already-known club.

## Decision

**1. Derivation rule (extends ADR-0111):** a candidate stat category's
numeric value is now derived one of two ways, both reading
`PlayerAttribute`/`PlayerOverride`:
- **Count-of-rows** (ADR-0111, unchanged): `"trophy"` only, now that
  `"club"` is removed.
- **Single-recorded-value** (this ADR): `"international-caps"` and
  `"international-goals"`. Each player has **at most one**
  `PlayerAttribute` row per type (`AttributeType = "international-caps"` /
  `"international-goals"`), whose `AttributeValue` is the number itself
  (stored as a string, parsed on read — same storage shape
  `PlayerAttribute` already uses everywhere, no schema change). A new
  repository method, `GetEffectivePlayerValuesByAttributeTypeAsync`
  (`IPlayerOverrideRepository`), mirrors
  `GetEffectivePlayerCountsByAttributeTypeAsync`'s exact shape (bulk read,
  `IReadOnlyDictionary<Guid, int>`, absent-not-zero for "no recorded
  value") but returns the row's own parsed value instead of a row count.

**2. Override precedence (extends ADR-0015/ADR-0111's own extension of
it):** for a single-recorded-value type, a `PlayerOverride` for
`(PlayerId, AttributeType)` **is** the effective value — `int.Parse`'d
directly, no reinterpretation needed. This is actually a more direct fit
of ADR-0015's original "override replaces the whole effective value set"
rule than ADR-0111's count-derivation needed (ADR-0111 had to normalize
"the whole value set" down to a count of exactly 1; here "the whole value
set" already **is** one number, so no normalization step exists at all).

**3. Data sourcing:** a new `IPlayerInternationalStatsRefreshService`
(`Games.XGHigherLower`, mirroring `IPlayerCareerStintRefreshService`'s
exact shape and contract — batch `RefreshInternationalStatsAsync(IReadOnlyList<Guid> playerIds, bool throwOnFailure = false, ...)`,
same swallow-and-log-unless-throwOnFailure default, same "a player whose
refresh fails keeps whatever they already had, never worse than before"
guarantee, REQ-103's "never block generation on a Wikidata failure" rule
applied here to a third caller) queries, per player, their `P54`
statements (excluding deprecated rank, full statement path — same
non-negotiable rule every existing `P54` query in `IWikidataClient.cs`
already follows) joined to `?team wdt:P1532 ?anyCountry` (truthy, any
country — this player's own nationality is not required as a join key,
unlike the existing intersection queries) with `OPTIONAL { ?statement
pq:P1350 ?caps }` / `OPTIONAL { ?statement pq:P1351 ?goals }`. Writes at
most one `PlayerAttribute` row per type per player.

**4. Multiple national-team statements (a player capped for more than one
representative side, or a Wikidata item modeling youth vs. senior levels
as separate `P54` statements against teams that also carry `P1532`):**
the statement with the **highest recorded caps value** wins — a senior
full-international career total is virtually always the largest number
among a player's representative-team statements. Accepted as an
approximation, not proven correct for every player (see Follow-up).

**5. Eligibility floor (REQ-1506):** `HigherLowerGenerationService`'s
candidate pool is intersected with "international caps >= 10" (read via
the new single-value method above, category `"international-caps"`)
**regardless of the Round's active category** — applied once at
generation time, same timing ADR-0111/REQ-1501 already establish, never
per participant.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| **Two derivation shapes on the same repository interface (chosen)** | No schema change; reuses `PlayerAttribute`'s existing storage and `PlayerOverride`'s existing precedence mechanism exactly as ADR-0015 designed it; count and single-value are both just "what does this player's `PlayerAttribute` row(s) of this type mean," decided per `AttributeType` | Two derivation functions to keep straight instead of one; a future category must be explicitly assigned to one shape or the other | Chosen: smallest change that fits real Wikidata data shapes for both existing and new categories, no migration |
| Synthesize N placeholder rows for a caps value of N, keep ADR-0111's COUNT-only rule | No new derivation function | Grotesque: a 150-cap player gets 150 meaningless rows; an override becomes "insert exactly N rows," a rule nobody could infer from ADR-0015's text; makes `PlayerAttribute` unreadable for any other purpose | Rejected outright — the kind of contortion CLAUDE.md's ADR trigger exists to prevent by naming the real shape instead |
| New dedicated entity (e.g. `PlayerInternationalStat`) instead of reusing `PlayerAttribute` | Type-safe numeric column instead of a parsed string; no ambiguity about "one row max" | New migration, new repository, new override-precedence rule to design from scratch (ADR-0015 doesn't cover a new entity), more surface for a two-person team to maintain for a Tier-0 feature | Rejected for now: `PlayerAttribute`'s existing (PlayerId, AttributeType, AttributeValue) shape already fits "one row, one value" without a schema change — revisit only if a future category needs something a string value genuinely can't express |
| Require a specific national-team QID per player (resolved via `P27`/citizenship first, then joined) instead of "any `P54` statement to a `P1532`-bearing team" | More precise if a player's own nationality is already known and matches their national team exactly | Doesn't handle England/Scotland/Wales/Northern Ireland's own well-established `P1532` exception (same reason `QueryNationalTeamClubIntersectionAsync` exists at all) without re-deriving that whole lookup per player; this is a batch refresh over *many* players, not one targeted intersection query, so the extra join is disproportionate setup cost | Rejected: the "any `P1532`-bearing team" join already gets the right statement in the common case and doesn't need a second country-resolution step first |

## Consequences

- Positive: no migration — `PlayerAttribute`/`PlayerOverride` already fit
  both derivation shapes; `PlayerOverrideRepository` gains one new sibling
  method next to the existing count one, same testing pattern
  (`PlayerOverrideRepositoryTests`' existing absent-vs-zero-vs-override
  cases, mirrored).
- Positive: REQ-1506's pool-wide floor reuses the exact same read path
  (`GetEffectivePlayerValuesByAttributeTypeAsync("international-caps", ...)`)
  the caps *category* itself uses — one source of truth for "how many
  caps does this player have," never two.
- Negative / trade-off accepted: the "highest-caps statement wins" rule
  for a player with multiple `P1532`-bearing `P54` statements is a
  heuristic, not a verified-correct rule for every real player — flagged
  as a Follow-up, not fixed here.
- Negative / trade-off accepted, inherited from every other Wikidata sync
  job in this codebase (`PlayerCareerStintRefreshService` et al.):
  `IPlayerInternationalStatsRefreshService`'s actual query correctness and
  real-world data coverage (how many capped players actually have
  `P1350`/`P1351` qualifiers populated on Wikidata) **cannot be verified
  live in the implementing sandbox** — no network egress to
  `query.wikidata.org` from this environment. Must be verified via a real
  `ci.yml` `workflow_dispatch` run (or, more meaningfully, a real
  dev-environment sync run) before being trusted, same constraint already
  recorded in `NOTES.md`'s 2026-08-18 S-141 entry for a different
  Wikidata query.
- Follow-up: if real data shows international caps/goals coverage is
  sparse even among genuinely well-known players (Wikidata's sports
  statistics are less consistently maintained than club/trophy
  membership), REQ-1506's caps>=10 floor could shrink the eligible pool
  more than intended — worth a real coverage check against actual synced
  data before this ships to real players, mirroring ADR-0111's own
  "get explicit product confirmation... before it is ever played by a
  real user" follow-up that this whole story exists to close out for
  `"club"`.
- Follow-up: the "highest-caps statement wins" tie-break for multiple
  national-team statements is unverified against real edge cases (a
  player who switched senior national teams via a nationality change,
  e.g. via FIFA eligibility rules) — revisit if real data surfaces a
  player where this picks the wrong statement.

## Amendment (2026-09-10, follow-up to S-231/PR #367)

Two follow-ups from this ADR's own Consequences section, both closed by
the same session:

1. **Idempotency bug fix.** Point 3's batch refresh only ever wrote the
   `"international-caps"` `PlayerAttribute` row when Wikidata resolved a
   usable value — so `GetPlayersMissingInternationalStatsAsync`'s "row
   absent" signal couldn't distinguish "never checked" from "checked,
   Wikidata genuinely has no data" (the large majority of any football
   player pool). Proven in production: a same-day re-run attempted
   139,639 players instead of the expected ~2,600. Fixed by adding a
   `PlayerData`-only "checked" marker
   (`PlayerData.InternationalStatsCheckedField`) written
   for every player in a successfully-queried batch regardless of outcome
   — deliberately never a `PlayerAttribute` row, so it can never leak
   into eligibility logic. `GetPlayersMissingInternationalStatsAsync` now
   excludes a player with either the real caps row or the marker. See
   `docs/requirements-document.md`'s REQ-1501 2026-09-10 follow-up status
   note and `NOTES.md`'s 2026-09-10 entry for the full story — this is an
   implementation-detail bug fix, not a reversal of anything this ADR
   decided (the single-recorded-value derivation, override precedence,
   and data-sourcing shape in points 1-4 above are all unchanged).
2. **Real coverage numbers.** The "get real Wikidata coverage numbers
   before fully trusting this" Follow-up below is now answerable on
   demand, not just theoretically: `GET
   /admin/xg-higher-lower/international-stats-coverage`
   (`XGArcade.Api.Admin.AdminXGHigherLowerEndpoints`, REQ-1507) reports
   total player count, players with a real effective caps/goals/trophy
   value, and players meeting REQ-1506's caps>=10 floor — reusing this
   ADR's own `GetEffectivePlayerValuesByAttributeTypeAsync`/
   `GetEffectivePlayerCountsByAttributeTypeAsync` reads, never a new raw
   query. This endpoint reports the numbers; it does not itself decide
   whether the resulting coverage is "good enough" — that judgment still
   needs a human to actually call it against real synced data, which
   remains unexercised from this implementing sandbox (no
   `query.wikidata.org` egress here either).

## For AI agents

Do not add a new numeric `Player`/`PlayerAttribute` schema field or a new
entity to hold caps/goals "more cleanly" — `PlayerAttribute`'s existing
(PlayerId, AttributeType, AttributeValue) shape is the decision here,
reused via a second derivation function, not replaced. Do not derive caps
or goals as a COUNT of rows (ADR-0111's rule) — each is a single recorded
value per player, at most one row. Do not query `P1350`/`P1351` against a
specific national-team QID resolved from the player's `P27`/citizenship —
join `P54` to any team carrying `P1532`, mirroring
`QueryNationalTeamClubIntersectionAsync`'s existing truthy-`P1532`
convention. If code you are about to write would contradict this
decision, stop and flag it rather than silently working around it —
either this ADR needs a superseding one, or the approach needs to change.
