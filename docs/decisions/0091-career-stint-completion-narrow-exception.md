# ADR-0091: Career-stint completion — a narrow, scoped exception to "additive only"

- **Status:** Accepted
- **Date:** 2026-08-29
- **Related requirements:** REQ-1203
- **Related components:** COMP-06 (Data.PlayerStore), COMP-11 (Games.XGPath)

## Context

ADR-0054 gave xG Path its own direct, per-player Wikidata career-stint fetch
(`PlayerCareerStintRefreshService.BuildNewStintsByPlayerId`), deduped against
whatever `PlayerCareerStint` rows already existed and adding only genuinely
new stints via `AddCareerStintsBatchAsync`. Its Consequences section framed
this as deliberately, permanently **additive only, never a wipe-and-replace**:
a previously-wrong stint is not that method's concern. `PlayerCareerPrefetchService`
(ADR-0055) shares that exact same reconciliation logic for its own bulk
sweep, and `WikidataLookupService.PersistCareerStintsAsync` (xG Grid's own
byproduct writer, REQ-103/REQ-211) independently followed the same additive
discipline for its own, differently-shaped input.

Before this ADR, all three call sites matched a freshly-fetched stint
against an existing stored row on the **full tuple**
`(ClubName, StartYear, EndYear, AppearanceCount)`. This produced a concrete
bug: a stored ongoing stint (`EndYear = null`, since Wikidata had not yet
recorded a transfer date) whose real-world end date Wikidata later filled in
no longer matched the stored row on that full tuple — the fetched row's
non-null `EndYear` differed from the stored row's `null`. Every one of the
three call sites therefore inserted a **second row** for what is really the
same real-world stint, surfacing in xG Path's clue-reveal timeline
(REQ-1203) as a duplicate-looking club entry for one real spell. This is a
different bug from the ones ADR-0059/ADR-0061/ADR-0063/ADR-0081 already
fixed (cross-writer label mismatches, appearance-count merge gaps, and
adjacent-same-club display collapsing, respectively) — this one is
specifically about a stint whose `EndYear` transitions from unknown to
known between two fetches.

Fixing this at only two of the three call sites was flagged by
`architecture-reviewer` during this story's first review pass as an
undocumented gap — `WikidataLookupService.PersistCareerStintsAsync` was left
with its own stale full-tuple dedup after the other two call sites were
fixed, closed in a follow-up commit (`85924af`, "Close third duplicate-stint
door in WikidataLookupService").

## Decision

A fetched stint that matches an existing stored row on
**`(PlayerId, ClubName, StartYear)`** — narrowed from the previous full
4-field tuple — with a differing `EndYear` and/or `AppearanceCount` now
**completes that row in place** (`UpdateCareerStintCompletionsAsync`,
`IPlayerCareerStintRepository`) instead of inserting a duplicate. A
genuinely new `(ClubName, StartYear)` for that player still inserts as a new
row, everywhere, exactly as before.

The per-candidate reconciliation decision itself — no-op / insert / complete
— is extracted into one shared, internal primitive,
`CareerStintReconciler.Reconcile(existingByKey, clubName, startYear, endYear, appearanceCount)`,
used by **all three** reconciliation call sites:

- `PlayerCareerStintRefreshService.BuildNewStintsByPlayerId` — xG Path's own
  reactive, per-target refresh (ADR-0054).
- `PlayerCareerPrefetchService.FetchAndPersistBatchAsync` — the bulk sweep,
  including this story's own new rotating re-sweep (ADR-0090/ADR-0055).
- `WikidataLookupService.PersistCareerStintsAsync` — xG Grid's REQ-103
  generation-time and REQ-211 guess-time byproduct career-stint writes, the
  most frequently invoked of the three, closed in the follow-up commit above.

`StartYear` and `ClubName` themselves are **never** corrected by this path,
at any of the three call sites. A wrong start year or a wrong club name
remains explicitly out of scope — governed unchanged by ADR-0054's original
"additive only" principle for everything except this one narrow completion
case. This is a **narrow carve-out from, not a reversal of**, ADR-0054's
Consequences section language ("a previously wrong stint is not this
method's concern"): filling in a previously-unknown, now-known end date (and
appearance count) on an otherwise-correct row is the one case this ADR
removes from that trade-off; every other flavor of "wrong stint" — a bad
`StartYear`, a bad `ClubName`, a spuriously-inserted stint that shouldn't
exist at all — is still explicitly not any of these methods' concern, and
still requires the same manual/cleanup tooling (`DuplicateCareerStintCleaner`,
`clean-duplicate-career-stints`) it always has.

`UpdateCareerStintCompletionsAsync` never touches `SequenceOrder`. This is
deliberate, not an oversight: `SequenceOrder` is resolved by
`(StartYear, EndYear ?? int.MaxValue)` ordering across a player's full stint
set, and every row this method updates keeps its own `StartYear` unchanged
— the only field that can move within that ordering (`EndYear` going from
`null`, which already sorts last, to a real value) can only move a completed
stint **earlier** among stints sharing the exact same `StartYear` for the
exact same player: a practically nonexistent case for real single-career
data, since a player cannot debut at two different clubs in the same year.
A full re-sequencing pass — the same one `AddCareerStintsBatchAsync` already
performs for a genuinely new row, since a newly-discovered stint really can
chronologically precede existing ones — is therefore unnecessary overhead
for a completion and is deliberately not run here.

## The shared `CareerStintReconciler` primitive

The three call sites' outer loops could not be unified — `PlayerCareerStintRefreshService`/
`PlayerCareerPrefetchService` both operate over `WikidataCareerStintEntry`,
which carries its own per-entry `ClubName`/`ClubQid`, while
`WikidataLookupService.PersistCareerStintsAsync` operates over
`CareerStintQualifiers`, scoped to one caller-supplied `clubName` shared
across every qualifier in that call (that method is already scoped to one
club by the time it runs, since it's called once per `(nationality, club)`
intersection). The two input shapes are batched differently and keyed
differently enough that sharing the whole per-player/per-batch loop across
all three would have meant distorting at least one caller's natural shape
to fit a common interface.

What genuinely is identical across all three is narrower: given one
player's already-narrowed existing-stints-by-`(ClubName, StartYear)`
lookup, and one candidate stint's four scalar fields
(`clubName, startYear, endYear, appearanceCount`), the same three-way
decision (no-op / insert / complete) applies. `CareerStintReconciler.Reconcile`
is exactly that narrower primitive — it knows nothing about `PlayerId`,
batching, or which Wikidata record type produced the candidate, only the
one-candidate-in/one-outcome-out decision. This is the structural choice
that let one fix close all three call sites (including the third,
originally-missed one) without three separate, easy-to-drift-apart
implementations of the same reconciliation logic — only the outer loop
differs per caller; the actual rule is defined exactly once.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Keep the full 4-field tuple key, add a separate cleanup pass (like `DuplicateCareerStintCleaner`) to retroactively merge completion-shaped duplicates | No change to the live write path; consistent with ADR-0059's existing precedent of fixing display-shape bugs via a provable-only cleanup script | Doesn't stop new duplicates of this exact shape from being created going forward — every future Wikidata sync that fills in a previously-null `EndYear` would keep producing a fresh duplicate for the cleanup pass to chase; treats the symptom repeatedly rather than the cause once | Rejected — this bug shape (unlike the label-mismatch bugs ADR-0059/ADR-0063 fixed) recurs continuously as real transfers complete over time, not just once against legacy data; fixing the write path itself is the only way to stop new duplicates from appearing |
| Widen the narrowed key further to allow correcting `StartYear`/`ClubName` too, not just `EndYear`/`AppearanceCount` | Would fix a broader class of "wrong stint" bugs in one pass | Reopens exactly the trade-off ADR-0054's Consequences section deliberately accepted ("a previously wrong stint is not this method's concern") for a much larger, riskier surface — correcting a stored `ClubName`/`StartYear` from a live fetch risks silently overwriting data that was manually corrected via `PlayerOverride`-adjacent tooling for other reasons, with no proof the fetched value is actually more correct | Rejected — out of scope for this story; ADR-0054's original trade-off for anything beyond end-date/appearance-count completion is deliberately left standing |
| One shared reconciliation primitive spanning the full per-player/per-batch loop across all three callers | Maximal code reuse | The three callers' input shapes (`WikidataCareerStintEntry` vs. `CareerStintQualifiers`) differ enough that unifying the outer loop would force at least one caller into an unnatural shape, or require a new intermediate DTO layer purely to make the loop shareable | Rejected in favor of sharing only the narrower per-candidate decision (`CareerStintReconciler.Reconcile`), which is genuinely identical across all three without distorting any caller |

## Consequences

- Positive: a real-world transfer whose end date Wikidata later fills in no
  longer produces a duplicate-looking club-reveal entry in xG Path's clue
  timeline (REQ-1203) — fixed at the source, for every future sync, not only
  retroactively cleaned once.
- Positive: all three reconciliation call sites — including
  `WikidataLookupService.PersistCareerStintsAsync`, xG Grid's own
  generation-time/guess-time byproduct writer — now apply the identical
  rule via one shared primitive, closing the gap `architecture-reviewer`
  flagged in the first review pass rather than leaving a third, differently
  behaved copy in place.
- Positive: no re-sequencing pass is needed for a completion, since a
  completed row's own `StartYear` never moves — `UpdateCareerStintCompletionsAsync`
  is a plain, cheap field update, not a full re-sequence of the player's
  stint set.
- Negative / trade-off accepted: the match key is now narrower
  (`ClubName, StartYear` instead of the full 4-field tuple), which means a
  stored row's `StartYear`/`ClubName` are trusted at face value going
  forward for the purpose of matching — if either was ever wrong to begin
  with, this reconciliation will now silently "complete" that wrong row's
  `EndYear`/`AppearanceCount` rather than surfacing a second, differently-keyed
  row that might have prompted closer inspection. This is the accepted
  narrowing this ADR's Decision section describes; correcting a wrong
  `StartYear`/`ClubName` remains explicitly out of scope and requires
  existing manual/override tooling.
- Negative / trade-off accepted: `IPlayerCareerStintRepository` gains a new
  method (`UpdateCareerStintCompletionsAsync`) alongside
  `AddCareerStintsBatchAsync`, a second write shape (in-place update, no
  re-sequencing) that future maintainers must not conflate with the insert
  path's re-sequencing responsibility.
- Follow-up: none currently identified. `DuplicateCareerStintCleaner`
  (ADR-0059/ADR-0063) is unaffected and remains strictly more conservative
  than this narrower live-write-path key — its own full-tuple matching is
  now documented as intentionally more cautious than the write path, not an
  inconsistency to reconcile.

## For AI agents

Do not widen `CareerStintReconciler.Reconcile`'s matching key to include
`StartYear`/`ClubName` correction, or otherwise let a "completion" silently
overwrite either field, without a fresh ADR — this ADR's whole scope is
end-of-stint field completion only, and ADR-0054's original "additive only,
a previously wrong stint is not this method's concern" trade-off still
governs everything else about a stored stint's correctness.

Do not add a fourth reconciliation call site that reimplements this
decision inline instead of calling `CareerStintReconciler.Reconcile` — the
whole point of extracting it was to stop a third copy from drifting apart
the way the first two already had; a fourth copy would repeat exactly the
gap `architecture-reviewer` flagged in this story's first review pass.

Do not add a re-sequencing pass to `UpdateCareerStintCompletionsAsync`
"for safety" without re-reading the Decision section's reasoning above —
it is currently unneeded because a completed row's own `StartYear` is
never touched by this method, and adding one would be unnecessary
overhead, not a correctness fix.
