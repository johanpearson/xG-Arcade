# ADR-0080: xG Path inferred-loan label on club-reveal clues

- **Status:** Accepted
- **Date:** 2026-08-19
- **Related requirements:** REQ-1203
- **Related components:** COMP-11 (Games.XGPath)
- **Related decisions:** ADR-0042 (career-stint data model — confirmed to
  have no loan/parent-club field, which is why this ADR exists at all),
  ADR-0075 (xG Path B-team/reserve-team exclusion — this ADR's direct
  precedent in `PathCareerStintFilter.cs`: same class of inferred,
  imprecise, iteratively-refined heuristic, same "needs its own ADR" bar)

## Context

S-163 (`docs/backlog.md`, Epic 19) reports a real user-facing puzzle
matching David Beckham's actual career: it rendered "Manchester United"
and "Preston North End" together in the same club-reveal turn with no
indication that Preston (1994-95) was a loan spell chronologically NESTED
inside the Man Utd stint (1992-2003), not a sequential "next club." A
player reasoning about the clue sequence has no way to tell the two apart.

`PlayerCareerStint`/`ClubDefinition` record no loan-vs-permanent status
and no parent-club relationship at all — confirmed by reading ADR-0042,
the data-model ADR, which does not mention any such field. Wikidata does
have a real signal for this (property P1642, "on loan from," as a
qualifier on the relevant stint statement), but nothing in this schema
captures it today.

Two options were considered (S-163's own text, `docs/backlog.md`):

- **(a) A real schema addition** sourced from Wikidata's P1642 qualifier —
  the precise, non-heuristic fix. This is real Tier 1/2-shaped scope per
  `MVP-SCOPE.md` (a new field, a `WikidataClient` SPARQL change, a backfill
  story for the ~608K existing rows) and, regardless of tier, is out of
  reach from this sandbox: no wikidata.org access here to verify the
  property's exact usage/shape against real data.
- **(b) A heuristic inferred purely from date-range containment**: a stint
  whose `[StartYear, EndYear]` is fully contained within a DIFFERENT
  club's concurrent range is PROBABLY a loan. This is the same class of
  imprecise, iteratively-refined heuristic `PathCareerStintFilter`'s
  `NationalTeamPattern`/`BTeamPattern` already are (ADR-0075) — same
  false-positive/negative risk profile, same "needs its own ADR" bar.

**Decision made 2026-08-19, explicit product request**: build (b),
accepting the inference-accuracy trade-off as a deliberate experiment
("test out" — S-163's own wording) rather than a load-bearing correctness
claim. This is acceptable specifically because the result is
**presentation-only** (no eligibility/scoring impact) and **reversible**
(a single boolean flowing through the pipeline, easy to strip back out if
the false-positive rate turns out unacceptable in practice).

## Decision

Add `PathCareerStintFilter.IsInferredLoan` (`PathCareerStintFilter.cs`),
parallel in spirit (same file, same class, same disclosure discipline) to
`NationalTeamPattern`/`BTeamPattern`, but a date-range containment check
rather than a label-text regex, since no label wording distinguishes a
loan from a permanent transfer:

```csharp
public static bool IsInferredLoan(PlayerCareerStint stint, IReadOnlyList<PlayerCareerStint> allStints) =>
    stint.EndYear is not null &&
    allStints.Any(other =>
        other.ClubName != stint.ClubName &&
        other.StartYear <= stint.StartYear &&
        (other.EndYear is null || other.EndYear >= stint.EndYear));
```

Note the `stint.EndYear is not null` guard sits in front of the whole
`allStints.Any(...)` call, not inside the inner `||`'s second branch — a
guard placed only inside that branch would let `other.EndYear is null`
short-circuit the `||` to `true` without ever consulting `stint.EndYear`,
wrongly flagging two simultaneously-ongoing stints at different clubs as
loan-contained (a real bug caught and fixed 2026-08-19, before this
formula reached production; see decision point 1 below, which this guard
placement exists to satisfy).

True when `allStints` contains a stint at a DIFFERENT club whose date
range fully contains `stint`'s. This exact rule is S-163's own interface
contract, agreed and implemented identically by both the backend
(`PathCareerStintFilter.IsInferredLoan`) and the frontend team building
`PathTimeline.tsx`'s rendering in parallel against the same contract.

The two ongoing-stint (`EndYear == null`) edge cases and the identical-
range edge case are resolved as follows (see the method's own doc comment
in `PathCareerStintFilter.cs` for the full reasoning, not repeated here in
full):

1. **The candidate stint itself is ongoing** (`stint.EndYear == null`):
   always `false`. An ongoing stint has no known end date yet, so it can
   never be judged "fully contained" — conservative by design, matching
   S-163's own wording, not an oversight.
2. **The containing stint is ongoing** (`other.EndYear == null`) while the
   candidate has already ended: CAN be `true`. An open-ended stint at a
   different club that started before the candidate necessarily still
   "covers" it today, regardless of what the open stint's eventual true
   end date turns out to be.
3. **Identical date range, different club**: `true` for both stints in the
   pair, symmetrically — the formula's non-strict `<=`/`>=` comparisons
   satisfy containment either direction. Chosen deliberately over adding a
   strict-inequality special case (which the agreed interface contract
   does not call for) because it keeps this a direct, literal
   implementation of that contract with no undocumented deviation. The
   practical cost is small: a genuinely coincidental identical-range data
   shape (e.g. a Wikidata data-quality duplicate, or two overlapping-exact-
   date rows for what's really one spell) gets BOTH stints labeled
   "(loan)" rather than picking one as "the real club" — acceptable given
   this is presentation-only with no eligibility impact.

`PathClubClue` (`PathClueTurn.cs`) gains a third field, `bool IsLoan =
false` (default value, so every existing positional
`new PathClubClue(name, count)` call site — including
`PathClueSequenceBuilderTests.cs` — keeps compiling unchanged).
`PathClueSequenceBuilder.BuildSequence` computes it per-stint using the
full `stintsChronological` list already in scope:
`IsLoan: PathCareerStintFilter.IsInferredLoan(s, stintsChronological)`.
`PathClubClueResponse` (`PathEndpoints.cs`) and its `ToTurnResponse`
mapping propagate the same boolean straight through to the API boundary.
The frontend (`frontend/src/lib/types.ts`'s `PathClubClue.isLoan`,
`PathTimeline.tsx`'s club-reveal rendering) renders a small "(loan)" text
qualifier next to the club name when the flag is set, reusing an existing
muted/secondary text token from `design-document.md` §2 rather than
introducing a new one.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| (a) Real schema addition sourced from Wikidata's P1642 "on loan from" qualifier | Precise, sourced from an actual recorded fact rather than an inference; would also let the parent-club relationship be surfaced elsewhere later | Real schema change (new field, `WikidataClient` SPARQL change, backfill story for ~608K existing rows) — Tier 1/2-shaped scope per `MVP-SCOPE.md`; out of reach from this sandbox regardless (no wikidata.org access to verify P1642's real usage/shape) | Far larger change than a display-only clue-clarity fix warrants right now; the explicit product decision (2026-08-19) was to ship the heuristic first as a deliberate experiment and revisit (a) only if the heuristic's false-positive rate proves unacceptable in practice |
| Do nothing — leave the ambiguous rendering as-is | No new code, no false-positive risk at all | Leaves a real, reported user-confusion bug open with a known, cheap, reversible fix available | The heuristic's cost (a wrong "(loan)" label on an edge case) is low and disclosed, while the benefit (resolving the Beckham/Preston-shaped confusion for the common case) is real; "do nothing" was rejected in favor of shipping something reversible |

## Consequences

- Positive: resolves the reported Beckham/Preston-shaped confusion — a
  club-reveal turn now visually distinguishes a chronologically-nested,
  probably-loan stint from a genuinely sequential next club, for the
  common real-world shape (permanent stint fully spanning a shorter loan).
- Positive: reversible and low-risk by construction — a single boolean
  flowing from `PathCareerStintFilter` through `PathClueSequenceBuilder`
  and the API DTO to the frontend, with no effect on eligibility, scoring,
  or which clubs get revealed at all. Stripping it back out (reverting
  this ADR) touches the same small set of files and nothing else.
- Negative / trade-off accepted: **this heuristic is not verified against
  live Wikidata data** (no wikidata.org access from this sandbox) **or
  against real production `PlayerCareerStint` rows** (no database access
  from this sandbox either). It is a pure date-range inference, not a
  Wikidata-sourced fact — a coincidental date overlap between two
  genuinely separate, non-loan spells (e.g. a data-entry quirk, or two
  clubs that happen to have identical or nested recorded ranges for
  unrelated reasons) will be mislabeled "(loan)" with no way for this
  method to tell the difference.
- Negative / trade-off accepted: the identical-range-different-club edge
  case (decision point 3 above) labels both stints in the pair as loans,
  which is very unlikely to be desirable framing in every such case — a
  narrow, disclosed exception to an otherwise reasonable rule.
- **This is a deliberate experiment, explicitly framed that way by the
  product decision that authorized it (2026-08-19, "test out" — S-163's
  own wording), not a load-bearing correctness claim.** Expected to need
  iteration the same way `NationalTeamPattern`/`BTeamPattern` did — that
  filter needed two real follow-up corrections after landing (broadening
  senior-team scope, 2026-08-10; fixing a Catalonia/Basque wording
  inconsistency, S-140/2026-08-18) rather than being solved correctly in
  one pass. `IsInferredLoan` should be expected to need the same kind of
  correction once real false positives/negatives surface against
  production data.
- Follow-up: if the false-positive rate proves unacceptable once real
  puzzle data is observed in production, either refine this heuristic
  further (with its own dated comment explaining the specific case found,
  same discipline as `NationalTeamPattern`'s history) or revisit option
  (a) above (the real P1642-sourced schema field) as a follow-up story.

## For AI agents

Do NOT widen `IsInferredLoan`'s use into eligibility or scoring logic — it
is display-only, wired through exactly one path (`PathClueSequenceBuilder`
→ `PathClubClue.IsLoan` → `PathClubClueResponse.IsLoan` → frontend
rendering). `XGPathGameModule.GetEligiblePlayerIdsAsync` must never call
it. Do NOT treat partial date-range overlap as containment — the
containment test is a full-containment check
(`stint.EndYear is not null && (other.StartYear <= stint.StartYear &&
(other.EndYear is null || other.EndYear >= stint.EndYear))`), not a
"do these ranges overlap at all" check; a partial overlap (e.g. two
concurrent-transfer-window stints that neither fully contains the other)
must return `false`, and `PathCareerStintFilterTests.cs` pins this down as
a real test case. Do NOT present this heuristic's output as verified
against live Wikidata or production data in any future doc/comment/PR
description — it has not been, for the same sandbox-access reasons
`NationalTeamPattern`/`BTeamPattern` disclose in `PathCareerStintFilter.cs`'s
own doc comments. Any further heuristic refinement (widening, narrowing,
or fixing a specific false positive/negative found against real data)
needs its own dated code comment explaining the specific case, the same
discipline `PathCareerStintFilter`'s existing comment history already
demonstrates for `NationalTeamPattern`/`BTeamPattern` — don't silently
change the containment rule without recording why.
