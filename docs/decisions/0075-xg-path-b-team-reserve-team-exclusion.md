# ADR-0075: xG Path B-team/reserve-team exclusion in `PathCareerStintFilter`

- **Status:** Accepted
- **Date:** 2026-08-18
- **Related requirements:** REQ-1203
- **Related components:** COMP-11 (Games.XGPath)
- **Related decisions:** ADR-0059 (career-stint club-name canonicalization —
  introduced `DuplicateCareerStintCleaner` as a DELETE-based backfill
  cleanup; contrasted with, not the origin of, the read-time-filter
  approach reused here — see `PathCareerStintFilter.cs`'s own doc comment
  for why a read-time filter was chosen instead of a DELETE-based cleanup
  for national-team rows, and is chosen again here), ADR-0074
  (2-seeded-club eligibility, S-138 — this ADR's direct predecessor in
  Epic 12; must land first, since B-team exclusion changes which stints
  ever reach the seeded-club-count check)

## Context

Epic 12 (`docs/backlog.md`, S-139) continues Epic 12's review of xG Path's
target-player eligibility and clue content, following S-137 (`BirthYear`
floor, ADR-0073) and S-138 (2-seeded-club eligibility, ADR-0074).

No B-team/reserve-team concept exists anywhere in this schema —
`ClubDefinition` has no type/tier field, and no B-team club is seeded in
`ReferenceDataSeeder.cs`'s 33-club `Clubs` array. As a result, a
`PlayerCareerStint` row for a reserve or development side — e.g. "Real
Madrid Castilla," "Barcelona Atlètic," "Manchester United U21" — currently
passes every existing check unfiltered:

- It never counts toward S-138's ≥2-distinct-qualifying-seeded-club
  eligibility check, since its `ClubName` string does not exactly match any
  seeded club name (e.g. "Real Madrid Castilla" != "Real Madrid"), so it
  does not inflate that count.
- **It DOES still surface as a raw clue-reveal club name** in
  `PathClueSequenceBuilder`'s club-reveal turns, via `GET /path/current`
  (`PathEndpoints.cs`) — this is the actual bug this ADR closes, the same
  class of REQ-1203 violation ("don't leak a non-answer-worthy team name as
  a clue") that `PathCareerStintFilter`'s existing `NationalTeamPattern`/
  `IsNationalTeam`/`ExcludeNationalTeams` already closes for national and
  representative sides (see that filter's own doc comment history,
  2026-08-08 and 2026-08-10 — both bug fixes, neither has its own ADR).

This ADR follows the same pattern already proven twice for national teams
in the same file: a conservative, read-time, label-matching regex, applied
at both existing call sites alongside (not instead of) the national-team
filter.

## Decision

Add `BTeamPattern`/`IsBTeam`/`ExcludeBTeams` to `PathCareerStintFilter.cs`,
parallel in shape to the existing `NationalTeamPattern`/`IsNationalTeam`/
`ExcludeNationalTeams`:

```csharp
private static readonly Regex BTeamPattern =
    new(@"\b(reserves?|B|II|U1[7-9]|U2[0-3]|castilla|atl[eè]tic)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

public static bool IsBTeam(string clubName) => BTeamPattern.IsMatch(clubName);

public static IReadOnlyList<PlayerCareerStint> ExcludeBTeams(
    IReadOnlyList<PlayerCareerStint> stints) =>
    stints.Count == 0
        ? stints
        : stints.Where(s => !IsBTeam(s.ClubName)).ToList();
```

The pattern is an alternation of independently word-bounded tokens
(`\b...\b` around each alternative, not just around the whole group),
covering the known label shapes for reserve/development sides:

- `reserves?` — an explicit "Reserve(s)" suffix (e.g. "Everton Reserves").
- `B` — a bare tier letter used as a club's own suffix (e.g.
  "Barcelona B").
- `II` — a bare Roman-numeral tier suffix (e.g. "Bayern Munich II").
- `U1[7-9]`/`U2[0-3]` — youth/age-grade markers (U17–U23) as used on
  development-squad labels. Deliberately narrower than
  `NationalTeamPattern`'s own unbounded "under-N" youth-team matching,
  since that pattern's job is catching youth NATIONAL teams of any age,
  not bounding what counts as a development-squad age.
- `castilla` — Real Madrid's reserve side's Spanish name.
- `atl[eè]tic` — the Catalan/Spanish "Atlètic"/"Atlético" reserve-side
  qualifier (e.g. "Barcelona Atlètic"), as distinct from a senior club's
  own proper name that happens to share the same letters (see false-
  positive analysis below).

Both `ExcludeNationalTeams` and `ExcludeBTeams` are chained at both
existing call sites — they are **additive, not a replacement** for each
other, since they exclude disjoint categories of non-answer-worthy "club"
rows (representative/national sides vs. reserve/development sides):

1. `XGPathGameModule.GetEligiblePlayerIdsAsync` (`structurallyEligibleIds`):
   `PathCareerStintFilter.ExcludeBTeams(PathCareerStintFilter.ExcludeNationalTeams(kvp.Value))`.
2. `PathEndpoints.cs` (`GET /path/current`'s per-puzzle `stints` build):
   `PathCareerStintFilter.ExcludeBTeams(PathCareerStintFilter.ExcludeNationalTeams(playerStints))`.

### False-positive check against the current seeded club list

Hand-verified against `ReferenceDataSeeder.cs`'s current 33-club `Clubs`
array (Real Madrid, Barcelona, Manchester United, Manchester City,
Liverpool, Arsenal, Chelsea, Bayern Munich, Borussia Dortmund, Juventus, AC
Milan, Inter Milan, Paris Saint-Germain, Ajax, Benfica, Tottenham Hotspur,
Atletico Madrid, Napoli, AS Roma, Sevilla, Porto, RB Leipzig, Bayer
Leverkusen, Marseille, Lyon, Monaco, Lille, Lazio, Valencia, Real Sociedad,
Newcastle United, West Ham United, Celtic): none contain `reserve(s)`, a
standalone `B` or `II` token, `U17`–`U23`, `castilla`, or `atl[eè]tic` as
their own word-bounded token. Two names worth calling out explicitly
because they look close to a match:

- **"RB Leipzig"** does not match the bare `B` alternative — `R` and `B`
  are adjacent word characters with no boundary between them in "RB," so
  `\bB\b` never matches. Only a label with `B` as its own space-separated
  word (e.g. "Barcelona B") matches.
- **"Atletico Madrid"** does not match `atl[eè]tic` — the trailing `\b`
  fails inside "Atletico" because `c` and `o` are both word characters with
  no boundary between them; the pattern only matches a label where
  "atlètic"/"atletic" is itself a standalone final word (e.g. "Barcelona
  Atlètic").

This check was originally done by hand-tracing the regex against the 33
club-name strings; `PathCareerStintFilterTests.cs`'s
`REQ1203_IsBTeam_CurrentSeededClubNames_ReturnsFalse` now pins it down as a
real, parametrized test case per seeded club (added alongside this ADR, not
run against a compiler in this sandbox — no `dotnet` SDK here, see the "For
AI agents" section below). It has not been re-run against the
production `PlayerCareerStint` table's actual `ClubName` values, since this
sandbox has no database access either.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Add a `ClubDefinition` type/tier field and seed B-team rows explicitly | Precise, no regex false-positive risk at all | Real schema change, new seeding data entirely out of scope for a REQ-1203 clue-leak fix; conflicts with `MVP-SCOPE.md`'s Tier 0 framing (no B-team concept was ever scoped) | Far larger change than the bug (a clue leak) warrants; the label-pattern approach already has a proven precedent in this exact file for national teams |
| A single combined regex covering both national-team and B-team exclusion | One filter call instead of two | National and B-team labels share no common wording (unlike, say, two national-team phrasings) — a combined pattern would be harder to reason about and to test in isolation, and would conflate two independently-evolving heuristics (this filter is expected to need its own follow-up corrections, same as `NationalTeamPattern` needed twice) | Keeping the two filters separate, chained at each call site, mirrors this file's own existing pattern and keeps each heuristic's false-positive/negative history independently traceable |
| Wait for a live-Wikidata-verified pattern before landing anything | No unverified regex ships | This sandbox has no wikidata.org access (network-blocked) and no way to query production `PlayerCareerStint` rows either — waiting indefinitely means the known clue-leak bug (REQ-1203) stays open with no path to closing it from this environment | Same precedent as `NationalTeamPattern` itself: land a conservative, hand-verified-against-known-shapes pattern now, flagged explicitly for manual confirmation, and refine iteratively as real data surfaces (see Consequences below) |
| Drop the bare `B`/`II` alternatives to reduce false-positive risk against future seeded clubs | Removes the (currently theoretical) risk of catching a club like Faroese "B36 Tórshavn" if it's ever seeded | Also removes real, common reserve-side label shapes ("Barcelona B," "Bayern Munich II") that this story's own acceptance criteria call out as target cases | The false-positive risk is against clubs not currently seeded and not currently planned; narrowing pre-emptively trades a real, common case for a hypothetical one — better to flag the risk (done, in code comments and below) and revisit if/when it actually surfaces |

## Consequences

- Positive: closes a real REQ-1203 clue-leak gap (a B-team/reserve-team
  name surfacing as a raw clue-reveal club) using the same proven,
  low-risk, read-time-filter mechanism already established for national
  teams — no schema change, no new external call, no new repository
  method.
- Positive: verified by hand against the full current 33-club seeded list
  with no false positives found (see above), including the two closest
  near-misses ("RB Leipzig," "Atletico Madrid").
- Negative / trade-off accepted: **this pattern is explicitly not a
  complete B-team taxonomy and is not verified against live Wikidata data**
  (no wikidata.org access from this sandbox) or against the real,
  ~608K-row `PlayerCareerStint` table (no database access from this
  sandbox either). It is inferred from a small set of known label shapes.
  A bare `B`/`II` token in particular carries a real, currently-theoretical
  false-positive risk against a genuinely-named (non-reserve) club not in
  today's seeded list — e.g. Faroese "B36 Tórshavn"-style names use "B" as
  part of a proper name, not a reserve-tier marker. Not a problem today
  (no such club is seeded), but a concrete thing to check before any future
  story adds a club whose name could collide.
- **This will not be perfect on day one, by design, not by oversight** —
  the national-team filter in this exact file/class needed two real
  follow-up corrections after landing: the 2026-08-08 version was
  initially scoped to youth-only national teams, then broadened
  2026-08-10 to senior teams too after a real bug report showed senior
  teams leaking; and a genuine Catalonia/Basque wording inconsistency in
  that same regex was found later and fixed under S-140
  (`docs/backlog.md`, 2026-08-18, `PathCareerStintFilter.NationalTeamPattern`
  broadened to also match "regional" + "team"/"representative"). Label-pattern filters
  over free-text Wikidata data get refined iteratively as real false
  positives/negatives surface in production, not solved correctly in one
  pass — this ADR's `BTeamPattern` should be expected to need the same
  kind of follow-up correction, not treated as a closed, final answer.
- Follow-up: S-141 (`docs/backlog.md`) re-verifies xG Path's eligible-pool
  size after this and S-137/S-138/S-140 land together, since this
  exclusion (combined with the others) could shrink the pool enough to
  matter for `PathTargetCycle`'s no-repeat tracking (ADR-0058).

## For AI agents

Do NOT replace `ExcludeNationalTeams` with `ExcludeBTeams` at either call
site, or vice versa — they must both run, chained, since they exclude
disjoint categories of non-answer-worthy rows. Do NOT treat the false-
positive check above as a substitute for real verification: it is a
hand-trace against the 33 club name strings in `ReferenceDataSeeder.cs`,
performed in a sandbox with no `dotnet` SDK, no wikidata.org access, and no
database access — it has not been run as an actual test, and the pattern
itself has not been checked against a single real `PlayerCareerStint.
ClubName` value. Before trusting this in production, or before widening
the seeded club list, re-run `BTeamPattern` by hand (or in a real test
run) against any new club name being added. Do NOT fold S-140's
Catalonia/Basque national-team regex fix into this filter or this ADR —
that is `NationalTeamPattern`'s own, separate, already-tracked bug, out of
scope here. If a real false positive or false negative is found against
production `PlayerCareerStint` data, correct `BTeamPattern` directly (same
file) with a dated comment explaining the specific case found, the same
discipline `NationalTeamPattern`'s own comment history already
demonstrates — don't silently widen or narrow it without recording why.
