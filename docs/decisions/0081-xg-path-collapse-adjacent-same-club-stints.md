# ADR-0081: xG Path — collapse adjacent same-club `PlayerCareerStint` rows for clue display

- **Status:** Accepted
- **Date:** 2026-08-19
- **Related requirements:** REQ-1203
- **Related components:** COMP-11 (Games.XGPath)
- **Related decisions:** ADR-0063 (`DuplicateCareerStintCleaner`'s
  null-tolerant, single-value-propagation `AppearanceCount` merge rule —
  contrasted with, and deliberately NOT reused by, this ADR's null-vs-sum
  rule; see Context and Decision below), ADR-0074 (2-seeded-club
  eligibility, S-138 — introduced the "eligible with too few real stints,
  empty first club-reveal turn" bug class this ADR's invariant must not
  reopen), ADR-0075 (B-team/reserve-team exclusion — this ADR's direct
  predecessor in `PathCareerStintFilter.cs`'s filter chain; must land
  first, since collapse changes the row count `IsEligible` and
  `PathClueSequenceBuilder` both see), ADR-0080 (inferred-loan label —
  same file, same "heuristic, iteratively refined, its own doc-comment
  history" precedent, but a different kind of change: display-only
  annotation vs. this ADR's row-count-changing collapse)

## Context

A puzzle matching Divock Origi's real career rendered three consecutive
"Lille" club-reveal entries back to back — three separate
`PlayerCareerStint` rows for the same club, adjacent in chronological
sequence, with different `AppearanceCount` values (e.g. 40/33/unknown).
This reads as broken/duplicated data to a player, regardless of the
administrative reason Wikidata recorded three separate statements (a
squad-list renewal, a sell-then-loan-back, etc.).

**Why this is NOT `DuplicateCareerStintCleaner`/ADR-0063's job.** That
class proves two DB rows are the SAME real-world stint (matching QID and
dates) and deletes one, permanently, at write time. ADR-0063 explicitly
refuses to merge two rows with different, both-populated `AppearanceCount`
values — exactly the Lille shape here — because they could be a genuine
loan-and-return, and an incorrect DELETE can't be undone. A DB-level merge
is therefore not safe here: we cannot prove these rows are the same real
stint, only that nothing else happened chronologically between them.

That weaker, always-true fact is enough for a **read-time, DISPLAY-ONLY**
collapse instead: if two (or three) same-`ClubName` rows are strictly
adjacent in a player's chronological stint sequence, showing them as one
continuous club chapter cannot be "wrong" the way an incorrect DB merge
could be — nothing else happened in between regardless of why Wikidata
recorded separate statements. No DB write, no deletion, reversible by
construction (the underlying rows are untouched).

**Why this isn't a same-day, single-file fix.** This collapse must run in
`PathCareerStintFilter`, chained alongside `ExcludeNationalTeams`/
`ExcludeBTeams` (ADR-0075) — but `XGPathGameModule.IsEligible`'s
`MinDocumentedStintCount` floor (>= 3) exists specifically so
`PathClueSequenceBuilder.SplitIntoTurns` always has >= 3 rows to split
across its 3 fixed club-reveal turns. Collapse shrinks the row count the
same way the two Exclude filters already do, so it MUST be applied,
identically, at both the eligibility call site
(`XGPathGameModule.GetEligiblePlayerIdsAsync`) and the clue-building call
site (`PathEndpoints.cs`'s `GET /path/current` handler), in the same
position in the filter chain — never only at one. Landing it at only the
display call site would silently reopen the exact "eligible with too few
real stints, empty first club-reveal turn" bug class ADR-0074/S-138's own
quality-gate review already found and fixed once.

## Decision

Add `CollapseAdjacentSameClub` to `PathCareerStintFilter.cs`:

```csharp
public static IReadOnlyList<PlayerCareerStint> CollapseAdjacentSameClub(
    IReadOnlyList<PlayerCareerStint> stintsChronological)
```

**Precondition** (matching `PathClueSequenceBuilder.BuildSequence`'s own
documented precondition): input must already be sorted ascending by
chronological order (`SequenceOrder`, or equivalently `StartYear`/
`EndYear`). "Adjacent" is defined purely as "next to each other in this
list" — this method does not re-sort.

**Merge rule**, applied to every maximal run of consecutive
same-`ClubName` rows:

| Field | Rule |
|---|---|
| `ClubName` | unchanged (the shared name) |
| `PlayerId` | from any row in the run (all the same player's data) |
| `Id` | a freshly generated `Guid` — a synthesized, never-persisted display value with no real row identity |
| `StartYear` | the FIRST row's `StartYear` (earliest) |
| `EndYear` | the LAST row's `EndYear` (latest — stays `null` if the last row is itself ongoing) |
| `SequenceOrder` | the FIRST row's `SequenceOrder` |
| `AppearanceCount` | the SUM of every row's count, **only if every row in the run has a non-null value; `null` if ANY row is null** |

A run of length 1 passes through with output values identical to its
input row (only `Id` is freshly generated). A same-club pair with a
DIFFERENT club's row anywhere between them does NOT merge — only strictly
adjacent rows merge.

**The `AppearanceCount` null-vs-sum reasoning, spelled out because it is
deliberately the OPPOSITE of ADR-0063's rule for a reason:**
`DuplicateCareerStintCleaner` is proving two rows are the literal SAME
real stint — there, "one side unknown" plausibly means "the other side's
row already told us the true count for this one stint," so propagating
the known value loses nothing. `CollapseAdjacentSameClub`'s rows are
explicitly NOT claimed to be the same stint; they may be genuinely
separate real registrations for one continuous, uninterrupted club
chapter, where appearance counts are ADDITIVE across the chapter, not
duplicative of one another. If one segment's count is unknown, silently
treating it as contributing zero — by showing only the known segment's
count as if it were the whole chapter's total — would UNDERSTATE a real
total that could be meaningfully larger. Showing no count at all for the
merged entry is the honest choice instead: exactly how any other single
stint with an unrecorded `AppearanceCount` already renders today.

**Both call sites apply the identical three-deep chain, in the identical
order**, immediately after an explicit `OrderBy(SequenceOrder)` (needed
because neither call site's underlying stint fetch otherwise guarantees
chronological order — see each site's own comment):

```csharp
PathCareerStintFilter.CollapseAdjacentSameClub(
    PathCareerStintFilter.ExcludeBTeams(PathCareerStintFilter.ExcludeNationalTeams(stints))
        .OrderBy(s => s.SequenceOrder)
        .ToList())
```

1. `XGPathGameModule.GetEligiblePlayerIdsAsync` (`structurallyEligibleIds`)
   — `IsEligible`'s `MinDocumentedStintCount >= 3` check now sees the
   POST-collapse row count.
2. `PathEndpoints.cs` (`GET /path/current`'s per-puzzle `stints` build) —
   feeds directly into `PathClueSequenceBuilder.BuildSequence`.

`PathClueSequenceBuilder.BuildSequence` itself is NOT changed to call
`CollapseAdjacentSameClub` — it stays a pure turn-splitter/formatter with
no filter-chain knowledge of its own, matching how it already has no
knowledge of `ExcludeNationalTeams`/`ExcludeBTeams` either. Collapse is
the caller's responsibility, same as the two Exclude filters.

## A documented, intentional side effect

Once collapse runs before `IsEligible`'s seeded-club appearance-count
check (`AppearanceCount >= MinAppearancesAtSeededClub`, currently 20, ADR-
0047), a player whose true single-club appearance total was split across
two adjacent sub-threshold rows (e.g. 15 + 15 = 30) now correctly counts
as qualifying, where before the split understated it. **This is a GOOD,
intentional consequence of merging consistently, not a bug to guard
against** — it fixes exactly the kind of understatement the whole
null-vs-sum `AppearanceCount` rule above exists to avoid, just for the
sum-of-knowns case rather than the any-unknown case.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Extend `DuplicateCareerStintCleaner`/ADR-0063 to also merge different-`AppearanceCount` adjacent rows at write time | One mechanism, no read-time filter to maintain | ADR-0063 explicitly and deliberately refuses this — a real DB DELETE on rows that could be a genuine loan-and-return is irreversible if wrong; this collapse's whole safety argument is that it never asserts "these are the same stint," only "nothing happened between them," which a DB merge can't express without also claiming identity | Would break ADR-0063's own safety invariant, not just extend it |
| Apply collapse only at the display call site (`PathEndpoints.cs`), leaving eligibility unaware of it | Smaller, one-file change | Reopens the exact "eligible with too few real stints, empty first club-reveal turn" bug class ADR-0074/S-138 already found and fixed for the two Exclude filters — `IsEligible`'s row-count floor would be checked against a row count the display path no longer agrees with | The whole point of `MinDocumentedStintCount` is to guarantee what `PathClueSequenceBuilder` actually renders; judging it against pre-collapse data breaks that guarantee |
| A fuzzy/near-match collapse (e.g. same club name with minor spelling variants, or a small date gap) | Could catch more real duplicate-looking cases | Introduces the same "how sure are we these are really adjacent/the same" ambiguity ADR-0063 already drew a hard line against for its own, narrower (identical-name) case; widening the match criteria here raises, not lowers, the risk of an incorrect display merge | Exact `ClubName` match and strict list-adjacency are the only two facts this method can rely on without becoming another unverified heuristic stacked on `NationalTeamPattern`/`BTeamPattern`'s existing risk profile |

## Consequences

- Positive: closes a real, reported "duplicate-looking" clue-display bug
  (Origi/Lille) without any DB write, deletion, or risk of destroying a
  genuine loan-and-return's distinct rows.
- Positive: the seeded-club appearance-count eligibility side effect above
  is a genuine correctness improvement (understated totals from an
  arbitrary Wikidata statement split no longer wrongly exclude a
  candidate), not just a side effect to tolerate.
- Negative / trade-off accepted: a merged entry's `AppearanceCount` can go
  from "two low-but-known numbers" to "unknown" if even one adjacent
  segment in the run has no recorded count — this is the deliberate,
  documented choice (no fabricated partial sum), but it does mean a player
  sometimes sees "count unknown" for a club chapter where a naive sum of
  the known segments would have shown a number. Judged acceptable: showing
  a plausible-looking but understated number is a worse failure mode than
  showing no number, matching how every other unknown-count stint already
  renders.
- Negative / trade-off accepted: this doubles the number of places a
  future refactor must keep in lockstep (now three chained filter calls,
  not two) — mitigated by the INVARIANT comment on
  `GetEligiblePlayerIdsAsync` and this ADR both calling out the exact
  three-deep chain and ordering requirement explicitly.
- Follow-up: none planned specifically for this story; if a future bug
  report shows a genuinely-separate spell (not one continuous chapter)
  being wrongly collapsed because it happens to be adjacent with an
  identical club name, that would need its own investigation — this
  method has no way to distinguish "adjacent because continuous" from
  "adjacent because coincidentally consecutive," the same class of
  imprecision `NationalTeamPattern`/`BTeamPattern`/`IsInferredLoan` already
  carry and are expected to need iteration on.

## For AI agents

Do NOT apply `CollapseAdjacentSameClub` at only one of the two call sites
(`XGPathGameModule.GetEligiblePlayerIdsAsync`,
`PathEndpoints.cs`'s `GET /path/current` handler) — both must apply the
identical three-deep chain
(`CollapseAdjacentSameClub(ExcludeBTeams(ExcludeNationalTeams(stints)))`,
sorted by `SequenceOrder` immediately before the collapse call) in the
identical order, or the eligibility guarantee `PathClueSequenceBuilder`
relies on (>= `MinDocumentedStintCount` rows to split across 3 club-reveal
turns) silently stops holding for the display path. Do NOT change the
null-vs-sum `AppearanceCount` rule (null wins over any known values in the
run) without a fresh ADR — it is the opposite of
`DuplicateCareerStintCleaner`/ADR-0063's null-tolerant
single-value-propagation rule, deliberately, and conflating the two is
exactly the mistake this ADR exists to prevent. Do NOT widen this into a
fuzzy or cross-club merge — only exact `ClubName` match (ordinal, matching
`IsEligible`'s own club-name comparison), only strictly adjacent rows
(nothing else, of any club, in between). Do NOT add this collapse inside
`PathClueSequenceBuilder.BuildSequence` itself — it stays a pure function
with no filter-chain knowledge; collapse is the caller's job, same as the
two Exclude filters.
